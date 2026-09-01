namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// What counts as a believable sensor reading, shared by every optional sensor source.
///
/// These blocks are other processes' memory, read without a handshake and without a schema
/// version in some cases, so a range check is the last thing standing between a torn read or a
/// misread offset and a card that says the CPU is at -3000 degrees. The bounds are deliberately
/// wide - they are a sanity filter, not a claim about what hardware does.
/// </summary>
internal static class SensorReadingBounds
{
    private const double MinTemperatureCelsius = -40d;

    private const double MaxTemperatureCelsius = 150d;

    private const double MaxFanRpm = 30_000d;

    public static bool IsPlausibleTemperature(double value) =>
        double.IsFinite(value) &&
        value > MinTemperatureCelsius &&
        value <= MaxTemperatureCelsius;

    public static bool IsPlausibleFanRpm(double value) =>
        double.IsFinite(value) && value >= 0d && value <= MaxFanRpm;
}
