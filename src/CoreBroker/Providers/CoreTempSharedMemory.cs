using System.Buffers.Binary;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Parses the block Core Temp publishes in <c>CoreTempMappingObjectEx</c>.
///
/// Unlike HWiNFO's block this one carries no signature and no poll clock - it is a bare struct -
/// so there is less to check and the checks that exist matter more: the mapping has to be long
/// enough for the fields actually read, the core and package counts have to describe a machine
/// that could exist, and every temperature has to land in a plausible range. Nothing is sized
/// from a number that came out of the block.
///
/// Only the leading 2688 bytes are touched, which is the whole of the older
/// <c>CORE_TEMP_SHARED_DATA</c> and the start of <c>CoreTempSharedDataEx</c>; both layouts are
/// therefore read by the same code, and the fields beyond (TDP, power, per-core multipliers)
/// are deliberately ignored.
/// </summary>
internal static class CoreTempSharedMemory
{
    /// <summary>
    /// Core Temp names its section without a namespace prefix, so it lands in the session-local
    /// namespace when Core Temp is not elevated and in the global one when it is. Both are
    /// tried, global first: falling back from global to session-local is the safe direction,
    /// because the global name is the one that cannot be squatted without
    /// <c>SeCreateGlobalPrivilege</c>. The reverse order would let any process in the session
    /// pre-empt a trustworthy publisher.
    /// </summary>
    public static IReadOnlyList<string> MapNames { get; } =
        [@"Global\CoreTempMappingObjectEx", "CoreTempMappingObjectEx"];

    /// <summary>Everything this parser reads lives below this offset.</summary>
    public const int MinimumBlockSize = 2688;

    private const int TjMaxOffset = 1024;
    private const int CoreCountOffset = 1536;
    private const int CpuCountOffset = 1540;
    private const int TemperatureOffset = 1544;
    private const int FahrenheitOffset = 2684;
    private const int DeltaToTjMaxOffset = 2685;

    private const int MaxTjMaxEntries = 128;
    private const int MaxTemperatureEntries = 256;

    /// <summary>
    /// The hottest core, in Celsius, or null when the block does not describe one.
    ///
    /// The hottest rather than the average: it is the core that is about to throttle, it is what
    /// every other tool calls the CPU temperature, and an average over a hybrid part - eight
    /// idle efficiency cores and one loaded performance core - describes nothing on the die.
    /// </summary>
    public static double? TryReadHottestCoreCelsius(ReadOnlySpan<byte> block)
    {
        if (block.Length < MinimumBlockSize)
        {
            return null;
        }

        uint coreCount = BinaryPrimitives.ReadUInt32LittleEndian(block[CoreCountOffset..]);
        uint cpuCount = BinaryPrimitives.ReadUInt32LittleEndian(block[CpuCountOffset..]);
        if (coreCount == 0u ||
            cpuCount == 0u ||
            cpuCount > MaxTjMaxEntries ||
            coreCount > MaxTemperatureEntries ||
            (long)coreCount * cpuCount > MaxTemperatureEntries)
        {
            return null;
        }

        // Core Temp reports in whatever unit it is displaying, and can be configured to report
        // headroom instead of temperature. Both are flags in the block rather than separate
        // fields, so a reader that ignores them shows Fahrenheit numbers labelled Celsius, or
        // a machine that gets colder the harder it works.
        bool fahrenheit = block[FahrenheitOffset] != 0;
        bool deltaToTjMax = block[DeltaToTjMaxOffset] != 0;

        double? hottest = null;
        for (uint cpu = 0; cpu < cpuCount; cpu++)
        {
            double tjMax = BinaryPrimitives.ReadUInt32LittleEndian(
                block[(TjMaxOffset + ((int)cpu * sizeof(uint)))..]);

            for (uint core = 0; core < coreCount; core++)
            {
                int index = (int)((cpu * coreCount) + core);
                double value = BinaryPrimitives.ReadSingleLittleEndian(
                    block[(TemperatureOffset + (index * sizeof(float)))..]);
                if (!double.IsFinite(value))
                {
                    continue;
                }

                // Undo the headroom first, while both numbers are still in the same unit.
                if (deltaToTjMax)
                {
                    if (tjMax <= 0d)
                    {
                        continue;
                    }

                    value = tjMax - value;
                }

                if (fahrenheit)
                {
                    value = (value - 32d) * 5d / 9d;
                }

                if (SensorReadingBounds.IsPlausibleTemperature(value) &&
                    (hottest is not { } best || value > best))
                {
                    hottest = value;
                }
            }
        }

        return hottest;
    }
}
