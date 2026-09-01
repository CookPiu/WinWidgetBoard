namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Which readings in a HWiNFO block are the ones the three sensor metrics want. -1 means this
/// machine publishes nothing that answers that metric, which is a different fact from HWiNFO
/// not running at all.
/// </summary>
internal readonly record struct HwInfoSensorSelection(
    int CpuTemperature,
    int GpuTemperature,
    int Fan)
{
    public static HwInfoSensorSelection None { get; } = new(-1, -1, -1);
}

/// <summary>
/// Picks the CPU temperature, the GPU temperature and the fan out of the few hundred readings
/// HWiNFO publishes.
///
/// There is no identifier to key on - HWiNFO's reading IDs are not stable across machines or
/// versions - so the choice is made from the reading's type, its label and the name of the
/// sensor it belongs to, in a fixed order of preference. The order matters: a machine typically
/// publishes several plausible CPU temperatures (the package sensor, the per-core aggregate,
/// and the motherboard's own reading of the socket), and picking whichever came first would
/// report a different sensor on every machine.
///
/// Pure and allocation-free apart from the candidate list, so the whole policy is testable
/// without HWiNFO installed.
/// </summary>
internal static class HwInfoSensorSelector
{
    /// <summary>
    /// Outside this a "temperature" is not one: HWiNFO reports the same type for a few derived
    /// values, and a sensor that has lost its source reads as a large negative number.
    /// </summary>
    private const double MinTemperatureCelsius = -40d;

    private const double MaxTemperatureCelsius = 150d;

    private const double MaxFanRpm = 30_000d;

    /// <summary>
    /// Temperature-typed readings that are not a temperature of anything: they are headroom or
    /// limits, measured in degrees, and one of them sitting at 38 would read as a very cool CPU.
    /// </summary>
    private static readonly string[] DerivedTemperatureMarkers =
    [
        "distance",
        "tjmax",
        "limit",
        "throttl",
        "delta",
    ];

    /// <summary>
    /// CPU temperature labels, best first. The first two are the vendors' own die sensors and
    /// are what every other tool shows; the rest are fallbacks for machines that publish
    /// neither.
    /// </summary>
    private static readonly string[] CpuTemperatureLabels =
    [
        "CPU (Tctl/Tdie)",
        "CPU Package",
        "CPU Die (average)",
        "Core Max",
        "Core Average",
        "CPU Temperature",
        "CPU",
    ];

    /// <summary>
    /// GPU temperature labels, best first. The hot spot deliberately ranks below the core: it
    /// is the hottest point on the die and reads several degrees higher, so showing it as "the"
    /// GPU temperature would disagree with every other tool on the same machine.
    /// </summary>
    private static readonly string[] GpuTemperatureLabels =
    [
        "GPU Temperature",
        "GPU Core Temperature",
        "GPU Die (average)",
        "GPU Hot Spot Temperature",
        "Temperature",
    ];

    private static readonly string[] FanLabels =
    [
        "CPU Fan",
        "CPU Fan Speed",
        "CPU Fan1",
    ];

    public static HwInfoSensorSelection Select(IReadOnlyList<HwInfoSensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        int cpu = -1;
        int cpuRank = int.MaxValue;
        int gpu = -1;
        int gpuRank = int.MaxValue;
        int fan = -1;
        int fanRank = int.MaxValue;

        for (int index = 0; index < readings.Count; index++)
        {
            HwInfoSensorReading reading = readings[index];

            Consider(RankCpuTemperature(reading), reading.ElementIndex, ref cpu, ref cpuRank);
            Consider(RankGpuTemperature(reading), reading.ElementIndex, ref gpu, ref gpuRank);
            Consider(RankFan(reading), reading.ElementIndex, ref fan, ref fanRank);
        }

        return new HwInfoSensorSelection(cpu, gpu, fan);
    }

    public static bool IsPlausibleTemperature(double value) =>
        double.IsFinite(value) &&
        value > MinTemperatureCelsius &&
        value <= MaxTemperatureCelsius;

    public static bool IsPlausibleFanRpm(double value) =>
        double.IsFinite(value) && value >= 0d && value <= MaxFanRpm;

    private static void Consider(int rank, int elementIndex, ref int chosen, ref int chosenRank)
    {
        // Strictly better only, so that among equally ranked candidates the first one HWiNFO
        // published wins - which is the primary sensor on every machine seen so far.
        if (rank >= 0 && rank < chosenRank)
        {
            chosen = elementIndex;
            chosenRank = rank;
        }
    }

    private static int RankCpuTemperature(in HwInfoSensorReading reading)
    {
        if (reading.Type != HwInfoReadingType.Temperature ||
            !IsPlausibleTemperature(reading.Value) ||
            IsDerived(reading.Label))
        {
            return -1;
        }

        int rank = IndexOfLabel(CpuTemperatureLabels, reading.Label);
        if (rank < 0)
        {
            return -1;
        }

        // A label that does not name the CPU itself only counts when the sensor it came from
        // is the CPU: "Core Max" also exists on a graphics card, and on a drive.
        return reading.Label.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) ||
            IsCpuSensor(reading.SensorName)
                ? rank
                : -1;
    }

    private static int RankGpuTemperature(in HwInfoSensorReading reading)
    {
        if (reading.Type != HwInfoReadingType.Temperature ||
            !IsPlausibleTemperature(reading.Value) ||
            IsDerived(reading.Label))
        {
            return -1;
        }

        int rank = IndexOfLabel(GpuTemperatureLabels, reading.Label);
        if (rank < 0)
        {
            return -1;
        }

        return reading.Label.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) ||
            IsGpuSensor(reading.SensorName)
                ? rank
                : -1;
    }

    private static int RankFan(in HwInfoSensorReading reading)
    {
        if (reading.Type != HwInfoReadingType.Fan || !IsPlausibleFanRpm(reading.Value))
        {
            return -1;
        }

        int rank = IndexOfLabel(FanLabels, reading.Label);
        if (rank >= 0)
        {
            // A CPU fan reading zero is a fact - fans stop under a zero-RPM curve - so it is
            // kept, unlike the fallback below.
            return rank;
        }

        // Any other fan, but only one that is turning: a board publishes a header per connector
        // and the empty ones all sit at zero.
        return reading.Value > 0d ? FanLabels.Length : -1;
    }

    private static bool IsDerived(string label)
    {
        foreach (string marker in DerivedTemperatureMarkers)
        {
            if (label.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCpuSensor(string sensorName) =>
        sensorName.StartsWith("CPU", StringComparison.OrdinalIgnoreCase);

    private static bool IsGpuSensor(string sensorName) =>
        sensorName.StartsWith("GPU", StringComparison.OrdinalIgnoreCase);

    private static int IndexOfLabel(string[] labels, string label)
    {
        for (int index = 0; index < labels.Length; index++)
        {
            if (string.Equals(labels[index], label, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
