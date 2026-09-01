using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// Core Temp is the second optional sensor source. It exists beside HWiNFO for a licensing
/// reason rather than a technical one - HWiNFO's free build switches its shared memory off after
/// twelve hours, which makes it a poor thing to tell users to install - and it covers the CPU
/// temperature only.
///
/// Its block carries no signature and no poll clock, so the two flags inside it do all the work:
/// one says the numbers are Fahrenheit, the other says they are headroom rather than
/// temperature. Reading either wrong produces a plausible-looking number that is simply false,
/// which is what most of these tests are about.
/// </summary>
[TestClass]
public sealed class CoreTempSensorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-SYSMON-080 [MON-001] The CPU temperature is the hottest core, not the average")]
    public void TheCpuTemperatureIsTheHottestCore()
    {
        // A hybrid part idles its efficiency cores while one performance core carries the load.
        // The average across them describes nothing on the die; the hottest core is the one
        // about to throttle, and is what every other tool calls the CPU temperature.
        using var block = new FakeCoreTempBlock(BuildBlock([41d, 39d, 72.5d, 38d]));
        using var reader = new CoreTempSensorReader([block.MapName]);

        CoreTempSensorSample sample = reader.Read(Now);

        Assert.IsTrue(sample.IsSourcePresent);
        Assert.AreEqual(72.5d, sample.CpuTemperatureCelsius!.Value, 0.01d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-081 [MON-001] Headroom mode is undone rather than shown as a temperature")]
    public void HeadroomModeIsUndone()
    {
        // Core Temp can be set to report the distance to TjMax. Those numbers look like
        // temperatures and get smaller as the CPU gets hotter, so a reader that ignores the
        // flag shows a machine that cools down under load.
        using var block = new FakeCoreTempBlock(
            BuildBlock([59d, 28d], tjMax: 100u, deltaToTjMax: true));
        using var reader = new CoreTempSensorReader([block.MapName]);

        // 100 - 28 = 72 is the hottest core; 100 - 59 = 41 is the coolest.
        Assert.AreEqual(72d, reader.Read(Now).CpuTemperatureCelsius!.Value, 0.01d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-082 [MON-001] A Fahrenheit block is converted, not shown as Celsius")]
    public void FahrenheitIsConverted()
    {
        using var block = new FakeCoreTempBlock(BuildBlock([140d, 122d], fahrenheit: true));
        using var reader = new CoreTempSensorReader([block.MapName]);

        // 140 °F is 60 °C. Shown unconverted it would read as a CPU at 140 degrees.
        Assert.AreEqual(60d, reader.Read(Now).CpuTemperatureCelsius!.Value, 0.01d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-083 [MON-001] Both flags together are applied in the right order")]
    public void BothFlagsApplyInOrder()
    {
        // Headroom is undone first, while it and TjMax are still in the same unit; converting
        // first would subtract a Fahrenheit TjMax from a Celsius delta.
        using var block = new FakeCoreTempBlock(
            BuildBlock([50d], tjMax: 212u, fahrenheit: true, deltaToTjMax: true));
        using var reader = new CoreTempSensorReader([block.MapName]);

        // 212 - 50 = 162 °F = 72.2 °C.
        Assert.AreEqual(72.22d, reader.Read(Now).CpuTemperatureCelsius!.Value, 0.01d);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-084 [MON-001] A block that describes no machine is present but unreadable")]
    public void ABlockThatDescribesNoMachineIsPresentButUnreadable()
    {
        // There is no signature to check, so the counts are the integrity check: the source is
        // there, but nothing in it can be believed. That is a different fact from "start Core
        // Temp", and the surfaces must not turn it into one.
        byte[] bogus = BuildBlock([65d]);
        BinaryPrimitives.WriteUInt32LittleEndian(bogus.AsSpan(1536), 0u);
        using (var block = new FakeCoreTempBlock(bogus))
        using (var reader = new CoreTempSensorReader([block.MapName]))
        {
            CoreTempSensorSample sample = reader.Read(Now);
            Assert.IsTrue(sample.IsSourcePresent);
            Assert.IsNull(sample.CpuTemperatureCelsius);
        }

        byte[] tooManyCores = BuildBlock([65d]);
        BinaryPrimitives.WriteUInt32LittleEndian(tooManyCores.AsSpan(1536), 4096u);
        Assert.IsNull(CoreTempSharedMemory.TryReadHottestCoreCelsius(tooManyCores));

        // A temperature outside anything hardware does is dropped rather than displayed.
        Assert.IsNull(CoreTempSharedMemory.TryReadHottestCoreCelsius(BuildBlock([9000d])));

        // And a block shorter than the fields this parser reads is not read at all.
        Assert.IsNull(CoreTempSharedMemory.TryReadHottestCoreCelsius(new byte[512]));
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-085 [MON-001] No block at all reads as absent rather than as an error")]
    public void AMissingBlockReadsAsAbsent()
    {
        using var reader = new CoreTempSensorReader(
            ["WwbCoreTempAbsent_" + Guid.NewGuid().ToString("N")]);

        Assert.IsFalse(reader.Read(Now).IsSourcePresent);
        Assert.IsFalse(reader.Read(Now.AddSeconds(1)).IsSourcePresent);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-086 [MON-001] A source that covers only the CPU does not speak for the fan")]
    public void ASourceThatCoversOnlyTheCpuDoesNotSpeakForTheFan()
    {
        // Core Temp publishes no GPU temperature and no fan speed. Counting it as "a sensor
        // source is running" would tell a Core Temp user to start something for two readings
        // that Core Temp was never going to provide.
        var coreTempOnly = new SystemMetricSample
        {
            HasBaseline = true,
            HasCpuTemperatureSource = true,
            CpuTemperatureCelsius = 64d,
        };

        Assert.AreEqual(
            SystemMonitorMetricStatus.Ready,
            Status(coreTempOnly, SystemMonitorContract.CpuTemperature));
        Assert.AreEqual(
            SystemMonitorMetricStatus.NeedsSensorSource,
            Status(coreTempOnly, SystemMonitorContract.GpuTemperature));
        Assert.AreEqual(
            SystemMonitorMetricStatus.NeedsSensorSource,
            Status(coreTempOnly, SystemMonitorContract.FanSpeed));

        // With a source that does cover them and still no reading, the machine is the reason.
        var everything = new SystemMetricSample
        {
            HasBaseline = true,
            HasCpuTemperatureSource = true,
            HasGpuTemperatureSource = true,
            HasFanSource = true,
        };
        Assert.AreEqual(
            SystemMonitorMetricStatus.Unavailable,
            Status(everything, SystemMonitorContract.FanSpeed));
    }

    private static string Status(SystemMetricSample sample, string metricId) =>
        SystemMonitorFormatter.FormatMetric(sample, metricId, SystemMonitorDetail.Normal).Status;

    /// <summary>
    /// Writes the leading 2688 bytes of Core Temp's own struct: the per-core load array, the
    /// per-package TjMax array, the two counts, the per-core temperatures, and - right at the
    /// end - the two flags that say how to read them.
    /// </summary>
    private static byte[] BuildBlock(
        double[] coreTemperatures,
        uint tjMax = 100u,
        uint cpuCount = 1u,
        bool fahrenheit = false,
        bool deltaToTjMax = false)
    {
        byte[] block = new byte[CoreTempSharedMemory.MinimumBlockSize];

        for (uint cpu = 0; cpu < cpuCount; cpu++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                block.AsSpan(1024 + ((int)cpu * sizeof(uint))), tjMax);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            block.AsSpan(1536), (uint)(coreTemperatures.Length / cpuCount));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(1540), cpuCount);

        for (int index = 0; index < coreTemperatures.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                block.AsSpan(1544 + (index * sizeof(float))), (float)coreTemperatures[index]);
        }

        block[2684] = fahrenheit ? (byte)1 : (byte)0;
        block[2685] = deltaToTjMax ? (byte)1 : (byte)0;
        return block;
    }

    /// <summary>A named section standing in for the one Core Temp publishes.</summary>
    private sealed class FakeCoreTempBlock : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;

        public FakeCoreTempBlock(byte[] block)
        {
            MapName = "WwbTestCoreTemp_" + Guid.NewGuid().ToString("N");
            _file = MemoryMappedFile.CreateNew(MapName, block.Length);
            _view = _file.CreateViewAccessor();
            _view.WriteArray(0L, block, 0, block.Length);
        }

        public string MapName { get; }

        public void Dispose()
        {
            _view.Dispose();
            _file.Dispose();
        }
    }
}
