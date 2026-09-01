using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// One tick of the sensor readings that Windows itself does not expose. Every value is null
/// when this machine publishes nothing for it. <see cref="IsSourcePresent"/> is the separate
/// fact of whether the source was readable at all, which is what lets the surfaces say "start
/// HWiNFO" instead of "this PC cannot read it".
/// </summary>
internal sealed record HwInfoSensorSample(
    bool IsSourcePresent,
    double? CpuTemperatureCelsius,
    double? GpuTemperatureCelsius,
    double? FanRpm)
{
    public static HwInfoSensorSample Absent { get; } = new(false, null, null, null);
}

/// <summary>
/// Reads CPU temperature, GPU temperature and fan speed out of HWiNFO's shared memory block.
///
/// Windows has no user-mode API for any of the three - they live behind a kernel driver, and
/// this product does not ship one (ADR-0029). HWiNFO already runs on many machines and publishes
/// its readings into a named section, so a machine that has it gets real values and a machine
/// that does not degrades to exactly what it showed before. Nothing is installed, elevated or
/// started by this code: it opens an existing section read-only, and gives up quietly when it
/// is not there.
///
/// The reader is deliberately cheap on the common path. A full scan of the block happens only
/// when the set of readings changes; after that each tick reads three values straight out of
/// the elements it already located, re-checking their identity so a re-ordered block is caught
/// rather than silently reporting the wrong sensor.
/// </summary>
internal sealed class HwInfoSensorReader : IDisposable
{
    /// <summary>How long to wait before looking for the section again once it is not there.</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The block outlives the process that publishes it - we hold a handle, so a crashed HWiNFO
    /// leaves its last values sitting there forever. Past this much silence from HWiNFO's own
    /// poll clock the values are no longer a measurement of anything, and the source is dropped
    /// and reopened so that a restarted HWiNFO is picked up.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private readonly string _mapName;
    private readonly byte[] _headerBuffer = new byte[HwInfoSharedMemory.HeaderSize];

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private long _capacity;
    private DateTimeOffset _nextProbeUtc = DateTimeOffset.MinValue;

    // Kept across a close on purpose: a block whose poll clock never advances again must not
    // look fresh merely because the handle to it was reopened.
    private long _lastPollTime;
    private DateTimeOffset _lastPollAdvancedAtUtc;

    private int _scannedReadingCount = -1;
    private Slot _cpuTemperature = Slot.Empty;
    private Slot _gpuTemperature = Slot.Empty;
    private Slot _fan = Slot.Empty;
    private bool _disposed;

    public HwInfoSensorReader(string? mapName = null) =>
        _mapName = string.IsNullOrWhiteSpace(mapName)
            ? HwInfoSharedMemory.GlobalMapName
            : mapName;

    public HwInfoSensorSample Read(DateTimeOffset nowUtc)
    {
        if (_disposed)
        {
            return HwInfoSensorSample.Absent;
        }

        if (_view is null)
        {
            if (nowUtc < _nextProbeUtc)
            {
                return HwInfoSensorSample.Absent;
            }

            _nextProbeUtc = nowUtc + ProbeInterval;
            if (!TryOpen())
            {
                return HwInfoSensorSample.Absent;
            }
        }

        if (!TryReadHeader(out HwInfoSharedMemoryHeader header) || !IsFresh(header, nowUtc))
        {
            // Either HWiNFO marked the block dead, or it stopped writing to it. Both mean the
            // handle we hold is worth nothing; the next probe picks up a fresh one if there is.
            Close();
            _nextProbeUtc = nowUtc + ProbeInterval;
            return HwInfoSensorSample.Absent;
        }

        if (header.ReadingElementCount != _scannedReadingCount)
        {
            Rescan(header);
        }

        double? cpu = ReadSlot(header, _cpuTemperature, HwInfoSensorSelector.IsPlausibleTemperature);
        double? gpu = ReadSlot(header, _gpuTemperature, HwInfoSensorSelector.IsPlausibleTemperature);
        double? fan = ReadSlot(header, _fan, HwInfoSensorSelector.IsPlausibleFanRpm);

        // A slot that no longer holds the reading it was chosen for means the block was
        // rebuilt without changing its element count - rare, but it would otherwise report one
        // sensor's value under another's name.
        if ((_cpuTemperature.HasValue && cpu is null) ||
            (_gpuTemperature.HasValue && gpu is null) ||
            (_fan.HasValue && fan is null))
        {
            Rescan(header);
            cpu = ReadSlot(header, _cpuTemperature, HwInfoSensorSelector.IsPlausibleTemperature);
            gpu = ReadSlot(header, _gpuTemperature, HwInfoSensorSelector.IsPlausibleTemperature);
            fan = ReadSlot(header, _fan, HwInfoSensorSelector.IsPlausibleFanRpm);
        }

        return new HwInfoSensorSample(true, cpu, gpu, fan);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }

    private bool TryOpen()
    {
        try
        {
            MemoryMappedFile file = MemoryMappedFile.OpenExisting(
                _mapName,
                MemoryMappedFileRights.Read);
            MemoryMappedViewAccessor view;
            try
            {
                view = file.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read);
            }
            catch
            {
                file.Dispose();
                throw;
            }

            _file = file;
            _view = view;
            _capacity = view.Capacity;
            _scannedReadingCount = -1;
            return true;
        }
        catch (FileNotFoundException)
        {
            // HWiNFO is not running, or its shared memory support is off. The expected case.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The section exists but this user may not read it. Nothing to retry differently.
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void Close()
    {
        _view?.Dispose();
        _view = null;
        _file?.Dispose();
        _file = null;
        _capacity = 0L;
        _scannedReadingCount = -1;
        _cpuTemperature = Slot.Empty;
        _gpuTemperature = Slot.Empty;
        _fan = Slot.Empty;
    }

    private bool TryReadHeader(out HwInfoSharedMemoryHeader header)
    {
        header = default;
        if (_view is null || _capacity < HwInfoSharedMemory.HeaderSize)
        {
            return false;
        }

        return TryReadBytes(0L, _headerBuffer, _headerBuffer.Length) &&
            HwInfoSharedMemory.TryReadHeader(_headerBuffer, _capacity, out header);
    }

    private bool IsFresh(in HwInfoSharedMemoryHeader header, DateTimeOffset nowUtc)
    {
        if (header.PollTimeUnixSeconds <= 0L)
        {
            // A build that does not publish its poll clock cannot be judged this way.
            return true;
        }

        if (header.PollTimeUnixSeconds != _lastPollTime)
        {
            _lastPollTime = header.PollTimeUnixSeconds;
            _lastPollAdvancedAtUtc = nowUtc;
            return true;
        }

        return nowUtc - _lastPollAdvancedAtUtc <= StaleAfter;
    }

    private void Rescan(in HwInfoSharedMemoryHeader header)
    {
        _scannedReadingCount = header.ReadingElementCount;
        _cpuTemperature = Slot.Empty;
        _gpuTemperature = Slot.Empty;
        _fan = Slot.Empty;

        string[] sensorNames = ReadSensorNames(header);
        var candidates = new List<HwInfoSensorReading>();
        byte[] element = new byte[header.ReadingElementSize];

        for (int index = 0; index < header.ReadingElementCount; index++)
        {
            long offset = header.ReadingSectionOffset + ((long)index * header.ReadingElementSize);

            // Only two of the nine reading kinds are wanted, and the type is the first field,
            // so most of the block is skipped without copying it.
            if (!TryReadUInt32(offset, out uint type) ||
                (type != (uint)HwInfoReadingType.Temperature &&
                    type != (uint)HwInfoReadingType.Fan))
            {
                continue;
            }

            if (!TryReadBytes(offset, element, element.Length))
            {
                continue;
            }

            int sensorIndex = ReadSensorIndex(element);
            string sensorName = sensorIndex >= 0 && sensorIndex < sensorNames.Length
                ? sensorNames[sensorIndex]
                : string.Empty;

            if (HwInfoSharedMemory.TryReadReading(
                    element,
                    index,
                    sensorName,
                    out HwInfoSensorReading reading))
            {
                candidates.Add(reading);
            }
        }

        HwInfoSensorSelection selection = HwInfoSensorSelector.Select(candidates);
        _cpuTemperature = ToSlot(candidates, selection.CpuTemperature);
        _gpuTemperature = ToSlot(candidates, selection.GpuTemperature);
        _fan = ToSlot(candidates, selection.Fan);
    }

    private string[] ReadSensorNames(in HwInfoSharedMemoryHeader header)
    {
        var names = new string[header.SensorElementCount];
        byte[] element = new byte[header.SensorElementSize];
        for (int index = 0; index < names.Length; index++)
        {
            long offset = header.SensorSectionOffset + ((long)index * header.SensorElementSize);
            names[index] = TryReadBytes(offset, element, element.Length)
                ? HwInfoSharedMemory.ReadSensorName(element)
                : string.Empty;
        }

        return names;
    }

    private double? ReadSlot(
        in HwInfoSharedMemoryHeader header,
        in Slot slot,
        Func<double, bool> isPlausible)
    {
        if (!slot.HasValue || slot.ElementIndex >= header.ReadingElementCount)
        {
            return null;
        }

        long offset =
            header.ReadingSectionOffset + ((long)slot.ElementIndex * header.ReadingElementSize);

        // Identity first: the value is only this metric's if the element still holds the
        // reading that was chosen for it.
        if (!TryReadUInt32(offset + HwInfoSharedMemory.ReadingSensorIndexOffset, out uint sensorIndex) ||
            !TryReadUInt32(offset + HwInfoSharedMemory.ReadingIdOffset, out uint readingId) ||
            sensorIndex != (uint)slot.SensorIndex ||
            readingId != slot.ReadingId ||
            !TryReadDouble(offset + HwInfoSharedMemory.ReadingValueOffset, out double value))
        {
            return null;
        }

        return isPlausible(value) ? value : null;
    }

    private static int ReadSensorIndex(ReadOnlySpan<byte> element)
    {
        uint index = BinaryPrimitives.ReadUInt32LittleEndian(
            element[HwInfoSharedMemory.ReadingSensorIndexOffset..]);
        return index <= int.MaxValue ? (int)index : -1;
    }

    private static Slot ToSlot(List<HwInfoSensorReading> candidates, int elementIndex)
    {
        if (elementIndex < 0)
        {
            return Slot.Empty;
        }

        foreach (HwInfoSensorReading candidate in candidates)
        {
            if (candidate.ElementIndex == elementIndex)
            {
                return new Slot(elementIndex, candidate.SensorIndex, candidate.ReadingId);
            }
        }

        return Slot.Empty;
    }

    private bool TryReadBytes(long offset, byte[] buffer, int count)
    {
        if (_view is null || offset < 0L || count <= 0 || offset + count > _capacity)
        {
            return false;
        }

        return _view.ReadArray(offset, buffer, 0, count) == count;
    }

    private bool TryReadUInt32(long offset, out uint value)
    {
        value = 0u;
        if (_view is null || offset < 0L || offset + sizeof(uint) > _capacity)
        {
            return false;
        }

        value = _view.ReadUInt32(offset);
        return true;
    }

    private bool TryReadDouble(long offset, out double value)
    {
        value = 0d;
        if (_view is null || offset < 0L || offset + sizeof(double) > _capacity)
        {
            return false;
        }

        value = _view.ReadDouble(offset);
        return true;
    }

    /// <summary>
    /// Where one chosen reading sits, plus the identity it had when it was chosen. Both halves
    /// are needed: the index makes the per-tick read a direct one, and the identity is what
    /// proves the index still points at the same sensor.
    /// </summary>
    private readonly record struct Slot(int ElementIndex, int SensorIndex, uint ReadingId)
    {
        public static Slot Empty { get; } = new(-1, -1, 0u);

        public bool HasValue => ElementIndex >= 0;
    }
}
