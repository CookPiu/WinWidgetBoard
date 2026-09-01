using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// Temperature and fan speed have no user-mode API on Windows, so they are read out of the
/// shared memory block HWiNFO publishes when the user is already running it. Everything here
/// runs against a synthetic block written by the test itself: the parser, the choice of which
/// of the few hundred readings answers each metric, and what the surfaces say when the source
/// is not there at all.
///
/// The block is a foreign process's memory, so the refusal cases matter as much as the happy
/// one - a header that does not describe the layout this code knows must degrade to "no source"
/// rather than being read anyway.
/// </summary>
[TestClass]
public sealed class HwInfoSensorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-SYSMON-070 [MON-001] A live block supplies CPU temperature, GPU temperature and fan")]
    public void LiveBlockSuppliesTheThreeSensorReadings()
    {
        using var block = new FakeHwInfoBlock(
            BuildBlock(
                ["CPU [#0]: Intel Core Ultra 5 225H", "GPU [#0]: NVIDIA GeForce RTX 4070", "Motherboard"],
                [
                    Reading(HwInfoReadingType.Temperature, 0, 1u, "CPU Package", 58.5d),
                    Reading(HwInfoReadingType.Temperature, 1, 2u, "GPU Temperature", 44d),
                    Reading(HwInfoReadingType.Fan, 2, 3u, "CPU Fan", 1180d),
                ]));
        using var reader = new HwInfoSensorReader(block.MapName);

        HwInfoSensorSample sample = reader.Read(Now);

        Assert.IsTrue(sample.IsSourcePresent);
        Assert.AreEqual(58.5d, sample.CpuTemperatureCelsius!.Value, 0.001d);
        Assert.AreEqual(44d, sample.GpuTemperatureCelsius!.Value, 0.001d);
        Assert.AreEqual(1180d, sample.FanRpm!.Value, 0.001d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-071 [MON-001] Headroom readings are not mistaken for a temperature")]
    public void HeadroomReadingsAreNotTemperatures()
    {
        // HWiNFO reports these as temperatures, in degrees, because that is their unit - but
        // they measure how far the part is from its limit, not how hot it is. One of them
        // showing up as "CPU 42 °C" on a throttling machine would be exactly backwards.
        HwInfoSensorSelection selection = HwInfoSensorSelector.Select(
        [
            Candidate(0, HwInfoReadingType.Temperature, "CPU [#0]: AMD Ryzen 9",
                "CPU Package Distance to TjMAX", 38d),
            Candidate(1, HwInfoReadingType.Temperature, "CPU [#0]: AMD Ryzen 9",
                "Core Max Distance to TjMAX", 36d),
            Candidate(2, HwInfoReadingType.Temperature, "CPU [#0]: AMD Ryzen 9",
                "CPU (Tctl/Tdie)", 71.4d),
        ]);

        Assert.AreEqual(2, selection.CpuTemperature);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-072 [MON-001] The die sensor wins over the board's reading of the socket")]
    public void DieSensorOutranksTheBoardReading()
    {
        // A machine publishes several plausible CPU temperatures and they disagree by several
        // degrees. Order in the block is not the answer: the board's own sensor is listed first
        // here, and picking it would report a different sensor than every other tool does.
        HwInfoSensorSelection selection = HwInfoSensorSelector.Select(
        [
            Candidate(0, HwInfoReadingType.Temperature, "Motherboard", "CPU", 46d),
            Candidate(1, HwInfoReadingType.Temperature, "CPU [#0]: Intel", "Core Max", 63d),
            Candidate(2, HwInfoReadingType.Temperature, "CPU [#0]: Intel", "CPU Package", 61d),
        ]);

        Assert.AreEqual(2, selection.CpuTemperature);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-073 [MON-001] The GPU hot spot stands in only when there is no core reading")]
    public void HotSpotOnlyStandsInForTheCore()
    {
        HwInfoSensorReading core = Candidate(
            1, HwInfoReadingType.Temperature, "GPU [#0]: NVIDIA", "GPU Temperature", 52d);
        HwInfoSensorReading hotSpot = Candidate(
            0, HwInfoReadingType.Temperature, "GPU [#0]: NVIDIA", "GPU Hot Spot Temperature", 68d);

        // The hot spot is the hottest point on the die and reads well above the core, so a card
        // that publishes both must not be summarized by it.
        Assert.AreEqual(1, HwInfoSensorSelector.Select([hotSpot, core]).GpuTemperature);
        Assert.AreEqual(0, HwInfoSensorSelector.Select([hotSpot]).GpuTemperature);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-074 [MON-001] An empty fan header is skipped, a stopped CPU fan is not")]
    public void EmptyFanHeadersAreSkipped()
    {
        // Boards publish a reading per connector and the unused ones sit at zero. A CPU fan at
        // zero is a different fact - that is what a zero-RPM curve looks like - so it is kept.
        HwInfoSensorSelection unlabelled = HwInfoSensorSelector.Select(
        [
            Candidate(0, HwInfoReadingType.Fan, "Motherboard", "Fan2", 0d),
            Candidate(1, HwInfoReadingType.Fan, "Motherboard", "Fan3", 940d),
        ]);
        Assert.AreEqual(1, unlabelled.Fan);

        HwInfoSensorSelection stoppedCpuFan = HwInfoSensorSelector.Select(
        [
            Candidate(0, HwInfoReadingType.Fan, "Motherboard", "Fan2", 700d),
            Candidate(1, HwInfoReadingType.Fan, "Motherboard", "CPU Fan", 0d),
        ]);
        Assert.AreEqual(1, stoppedCpuFan.Fan);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-075 [MON-001] A header that does not describe the known layout is refused")]
    public void AForeignHeaderIsRefused()
    {
        byte[] good = BuildBlock(
            ["CPU [#0]: Intel"],
            [Reading(HwInfoReadingType.Temperature, 0, 1u, "CPU Package", 55d)]);
        Assert.IsTrue(
            HwInfoSharedMemory.TryReadHeader(good, good.Length, out _),
            "the reference block should parse");

        // Shutting down: HWiNFO stamps the block dead and the values stop being real.
        byte[] dead = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(dead, HwInfoSharedMemory.DeadSignature);
        Assert.IsFalse(HwInfoSharedMemory.TryReadHeader(dead, dead.Length, out _));

        // A layout this parser does not implement must not be reinterpreted as the one it does.
        byte[] futureVersion = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(futureVersion.AsSpan(4), 3u);
        Assert.IsFalse(HwInfoSharedMemory.TryReadHeader(futureVersion, futureVersion.Length, out _));

        // A section that runs past the end of the mapping, and one whose element count would
        // overflow a 32-bit offset calculation. Both are read out of foreign memory.
        byte[] pastTheEnd = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(pastTheEnd.AsSpan(40), 64u);
        Assert.IsFalse(HwInfoSharedMemory.TryReadHeader(pastTheEnd, pastTheEnd.Length, out _));

        byte[] overflowing = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(overflowing.AsSpan(40), uint.MaxValue);
        Assert.IsFalse(HwInfoSharedMemory.TryReadHeader(overflowing, long.MaxValue, out _));
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-076 [MON-001] No block at all reads as absent rather than as an error")]
    public void AMissingBlockReadsAsAbsent()
    {
        using var reader = new HwInfoSensorReader(
            "WwbHwInfoAbsent_" + Guid.NewGuid().ToString("N"));

        HwInfoSensorSample sample = reader.Read(Now);

        Assert.IsFalse(sample.IsSourcePresent);
        Assert.IsNull(sample.CpuTemperatureCelsius);
        // The machine this ships to usually has no HWiNFO at all, so this path runs every tick
        // and must stay silent: no exception, no log, nothing to clean up.
        Assert.IsFalse(reader.Read(Now.AddSeconds(1)).IsSourcePresent);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-077 [MON-001] A block whose poll clock stopped is not reported as live")]
    public void AStalledBlockStopsBeingReported()
    {
        const long pollTime = 1_772_000_000L;
        using var block = new FakeHwInfoBlock(
            BuildBlock(
                ["CPU [#0]: Intel"],
                [Reading(HwInfoReadingType.Temperature, 0, 1u, "CPU Package", 49d)],
                pollTime));
        using var reader = new HwInfoSensorReader(block.MapName);

        Assert.AreEqual(49d, reader.Read(Now).CpuTemperatureCelsius!.Value, 0.001d);

        // We hold a handle, so the block survives the process that wrote it: a crashed HWiNFO
        // would otherwise leave its last temperature on screen forever, looking live.
        Assert.IsFalse(reader.Read(Now.AddSeconds(90)).IsSourcePresent);

        // ... and it must come back when HWiNFO does, which is why the reader reopens rather
        // than latching the source off.
        block.Write(
            BuildBlock(
                ["CPU [#0]: Intel"],
                [Reading(HwInfoReadingType.Temperature, 0, 1u, "CPU Package", 51d)],
                pollTime + 120L));
        HwInfoSensorSample resumed = reader.Read(Now.AddSeconds(160));
        Assert.IsTrue(resumed.IsSourcePresent);
        Assert.AreEqual(51d, resumed.CpuTemperatureCelsius!.Value, 0.001d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-078 [MON-001] Without the source a temperature says so, and only a temperature")]
    public void WithoutTheSourceOnlySensorMetricsSaySo()
    {
        var withoutSource = new SystemMetricSample { HasBaseline = true, HasSensorSource = false };

        foreach (string metricId in new[]
        {
            SystemMonitorContract.CpuTemperature,
            SystemMonitorContract.GpuTemperature,
            SystemMonitorContract.FanSpeed,
        })
        {
            Assert.AreEqual(
                SystemMonitorMetricStatus.NeedsSensorSource,
                SystemMonitorFormatter
                    .FormatMetric(withoutSource, metricId, SystemMonitorDetail.Normal)
                    .Status,
                metricId);
        }

        // Everything else that has no value has none because this machine does not expose it,
        // and no amount of running HWiNFO changes that.
        Assert.AreEqual(
            SystemMonitorMetricStatus.Unavailable,
            SystemMonitorFormatter
                .FormatMetric(withoutSource, SystemMonitorContract.GpuMemory, SystemMonitorDetail.Normal)
                .Status);

        // With the source running and still no reading, the machine really is the reason.
        var withSource = new SystemMetricSample { HasBaseline = true, HasSensorSource = true };
        Assert.AreEqual(
            SystemMonitorMetricStatus.Unavailable,
            SystemMonitorFormatter
                .FormatMetric(withSource, SystemMonitorContract.FanSpeed, SystemMonitorDetail.Normal)
                .Status);
    }

    private static HwInfoSensorReading Candidate(
        int elementIndex,
        HwInfoReadingType type,
        string sensorName,
        string label,
        double value) =>
        new(elementIndex, type, 0, (uint)elementIndex, sensorName, label, value);

    private static (HwInfoReadingType Type, int SensorIndex, uint ReadingId, string Label, double Value)
        Reading(HwInfoReadingType type, int sensorIndex, uint readingId, string label, double value) =>
        (type, sensorIndex, readingId, label, value);

    /// <summary>
    /// Writes the exact bytes HWiNFO writes: a 44-byte header naming two sections, a sensor
    /// section of 264-byte elements and a reading section of 316-byte ones, all packed with no
    /// alignment padding.
    /// </summary>
    private static byte[] BuildBlock(
        string[] sensorNames,
        (HwInfoReadingType Type, int SensorIndex, uint ReadingId, string Label, double Value)[] readings,
        long pollTimeUnixSeconds = 1_772_000_000L)
    {
        int sensorOffset = HwInfoSharedMemory.HeaderSize;
        int readingOffset =
            sensorOffset + (sensorNames.Length * HwInfoSharedMemory.SensorElementSize);
        byte[] block = new byte[
            readingOffset + (readings.Length * HwInfoSharedMemory.ReadingElementSize)];

        BinaryPrimitives.WriteUInt32LittleEndian(block, HwInfoSharedMemory.ActiveSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(4), HwInfoSharedMemory.SupportedVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 1u);
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(12), pollTimeUnixSeconds);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), (uint)sensorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(24), HwInfoSharedMemory.SensorElementSize);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), (uint)sensorNames.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(32), (uint)readingOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(36), HwInfoSharedMemory.ReadingElementSize);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(40), (uint)readings.Length);

        for (int index = 0; index < sensorNames.Length; index++)
        {
            int at = sensorOffset + (index * HwInfoSharedMemory.SensorElementSize);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), (uint)index);
            WriteFixedString(block.AsSpan(at + 8, 128), sensorNames[index]);
            WriteFixedString(block.AsSpan(at + 136, 128), sensorNames[index]);
        }

        for (int index = 0; index < readings.Length; index++)
        {
            int at = readingOffset + (index * HwInfoSharedMemory.ReadingElementSize);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), (uint)readings[index].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(
                block.AsSpan(at + 4), (uint)readings[index].SensorIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(
                block.AsSpan(at + 8), readings[index].ReadingId);
            WriteFixedString(block.AsSpan(at + 12, 128), readings[index].Label);
            WriteFixedString(block.AsSpan(at + 140, 128), readings[index].Label);
            WriteFixedString(
                block.AsSpan(at + 268, 16),
                readings[index].Type == HwInfoReadingType.Fan ? "RPM" : "°C");
            BinaryPrimitives.WriteDoubleLittleEndian(
                block.AsSpan(at + 284), readings[index].Value);
        }

        return block;
    }

    private static void WriteFixedString(Span<byte> destination, string value)
    {
        destination.Clear();
        int written = Encoding.Latin1.GetBytes(
            value.Length < destination.Length ? value : value[..(destination.Length - 1)],
            destination);
        if (written < destination.Length)
        {
            destination[written] = 0;
        }
    }

    /// <summary>A named section standing in for the one HWiNFO publishes.</summary>
    private sealed class FakeHwInfoBlock : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;

        public FakeHwInfoBlock(byte[] block)
        {
            // A session-local name, never the global one HWiNFO owns: the test must not be able
            // to disturb a real HWiNFO on the machine running it.
            MapName = "WwbTestHwInfo_" + Guid.NewGuid().ToString("N");
            _file = MemoryMappedFile.CreateNew(MapName, block.Length);
            _view = _file.CreateViewAccessor();
            _view.WriteArray(0L, block, 0, block.Length);
        }

        public string MapName { get; }

        public void Write(byte[] block) => _view.WriteArray(0L, block, 0, block.Length);

        public void Dispose()
        {
            _view.Dispose();
            _file.Dispose();
        }
    }
}
