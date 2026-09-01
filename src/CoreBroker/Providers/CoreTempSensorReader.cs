using System.IO.MemoryMappedFiles;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// One tick of what Core Temp publishes. It reads the CPU only, so unlike the HWiNFO block this
/// is a single value plus the separate fact of whether the source was there at all.
/// </summary>
internal sealed record CoreTempSensorSample(bool IsSourcePresent, double? CpuTemperatureCelsius)
{
    public static CoreTempSensorSample Absent { get; } = new(false, null);
}

/// <summary>
/// Reads CPU temperature out of Core Temp's shared memory.
///
/// This exists beside the HWiNFO reader because of a licensing detail rather than a technical
/// one: HWiNFO's free build switches its shared memory off after twelve hours and needs it
/// re-enabled by hand, which makes it unusable as the thing a shipped product tells its users to
/// install. Core Temp is free, small, has no such limit, and publishes a documented block.
///
/// It supplies **only** CPU temperature - no GPU, no fan - so it does not replace the HWiNFO
/// reader, it fills the one reading most people are actually asking for.
///
/// Same rules as the other source: open an existing section read-only, install and start
/// nothing, and give up quietly when it is not there.
/// </summary>
internal sealed class CoreTempSensorReader : IDisposable
{
    /// <summary>How long to wait before looking for the section again once it is not there.</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Core Temp's block carries no poll clock, so there is no way to notice from the contents
    /// that nobody is writing to them any more - and the section outlives Core Temp because we
    /// hold a handle to it. Dropping the handle on a timer is the whole answer: if Core Temp is
    /// gone the section goes with our handle and the next open fails, and if it is still running
    /// the reopen costs one call a minute.
    /// </summary>
    private static readonly TimeSpan ReopenInterval = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<string> _mapNames;
    private readonly byte[] _block = new byte[CoreTempSharedMemory.MinimumBlockSize];

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private DateTimeOffset _nextProbeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _reopenAtUtc = DateTimeOffset.MaxValue;
    private bool _disposed;

    public CoreTempSensorReader(IReadOnlyList<string>? mapNames = null) =>
        _mapNames = mapNames is { Count: > 0 } ? mapNames : CoreTempSharedMemory.MapNames;

    public CoreTempSensorSample Read(DateTimeOffset nowUtc)
    {
        if (_disposed)
        {
            return CoreTempSensorSample.Absent;
        }

        if (_view is not null && nowUtc >= _reopenAtUtc)
        {
            Close();
        }

        if (_view is null)
        {
            if (nowUtc < _nextProbeUtc)
            {
                return CoreTempSensorSample.Absent;
            }

            _nextProbeUtc = nowUtc + ProbeInterval;
            if (!TryOpen())
            {
                return CoreTempSensorSample.Absent;
            }

            _reopenAtUtc = nowUtc + ReopenInterval;
        }

        if (_view is null ||
            _view.Capacity < CoreTempSharedMemory.MinimumBlockSize ||
            _view.ReadArray(0L, _block, 0, _block.Length) != _block.Length)
        {
            Close();
            _nextProbeUtc = nowUtc + ProbeInterval;
            return CoreTempSensorSample.Absent;
        }

        // The source being present and it having a usable reading are two different facts: a
        // block whose counts do not describe a machine is present but unreadable, and the
        // surfaces must not turn that into "start Core Temp".
        return new CoreTempSensorSample(
            true,
            CoreTempSharedMemory.TryReadHottestCoreCelsius(_block));
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
        foreach (string mapName in _mapNames)
        {
            try
            {
                MemoryMappedFile file = MemoryMappedFile.OpenExisting(
                    mapName,
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
                return true;
            }
            catch (FileNotFoundException)
            {
                // Core Temp is not running under this name. The expected case for both names on
                // a machine that does not have it at all.
            }
            catch (UnauthorizedAccessException)
            {
                // The section exists but is not readable by this user.
            }
            catch (IOException)
            {
            }
        }

        return false;
    }

    private void Close()
    {
        _view?.Dispose();
        _view = null;
        _file?.Dispose();
        _file = null;
        _reopenAtUtc = DateTimeOffset.MaxValue;
    }
}
