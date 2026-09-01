using System.Buffers.Binary;
using System.Text;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// What kind of quantity one HWiNFO reading is. The ordinals are HWiNFO's own
/// <c>SENSOR_READING_TYPE</c>, so they must not be reordered; anything this build does not know
/// arrives as <see cref="Other"/> rather than being guessed at.
/// </summary>
internal enum HwInfoReadingType
{
    None = 0,
    Temperature = 1,
    Voltage = 2,
    Fan = 3,
    Current = 4,
    Power = 5,
    Clock = 6,
    Usage = 7,
    Other = 8,
}

/// <summary>
/// The header of HWiNFO's shared memory block, reduced to what a reader needs: where the two
/// sections are, how big one element is, and how many there are. Sizes come from the header
/// rather than from a hard-coded <c>sizeof</c> so a future HWiNFO that appends fields to an
/// element still parses - the fields this code reads are all in the leading bytes.
/// </summary>
internal readonly record struct HwInfoSharedMemoryHeader(
    long PollTimeUnixSeconds,
    long SensorSectionOffset,
    int SensorElementSize,
    int SensorElementCount,
    long ReadingSectionOffset,
    int ReadingElementSize,
    int ReadingElementCount);

/// <summary>
/// One reading, paired with the name of the sensor that produced it. The sensor name is what
/// separates a CPU package temperature from a drive temperature when both are simply labelled
/// "Temperature", so it travels with the reading rather than being looked up later.
/// </summary>
internal readonly record struct HwInfoSensorReading(
    int ElementIndex,
    HwInfoReadingType Type,
    int SensorIndex,
    uint ReadingId,
    string SensorName,
    string Label,
    double Value);

/// <summary>
/// Parses the bytes HWiNFO publishes in <c>Global\HWiNFO_SENS_SM2</c>.
///
/// This is a foreign process's memory and is treated as untrusted input: every offset, element
/// size and count is checked against the mapping's own length before a single byte is read, and
/// the caller allocates nothing sized by a number that came out of the block. Only the exact
/// layout version this code knows is accepted - a different one degrades to "no sensor source"
/// rather than being reinterpreted.
///
/// Nothing here touches Win32 or the file system, so the whole parse is unit-testable from a
/// synthetic block.
/// </summary>
internal static class HwInfoSharedMemory
{
    /// <summary>
    /// The only name this reader opens. HWiNFO publishes into the global kernel namespace,
    /// which needs <c>SeCreateGlobalPrivilege</c> to write to; the session-local namespace does
    /// not, so any process in the session could squat a local name and feed us numbers. The
    /// fallback is deliberately absent.
    /// </summary>
    public const string GlobalMapName = @"Global\HWiNFO_SENS_SM2";

    /// <summary>'HWiS' - the block is live.</summary>
    public const uint ActiveSignature = 0x53695748u;

    /// <summary>'DEAD' - HWiNFO is shutting the block down and the values are no longer real.</summary>
    public const uint DeadSignature = 0x44414544u;

    /// <summary>The layout revision this parser implements (HWiNFO's SM2).</summary>
    public const uint SupportedVersion = 2u;

    public const int HeaderSize = 44;

    // Pack=1 in HWiNFO's own headers, so these are plain byte counts with no alignment padding.
    public const int SensorElementSize = 264;
    public const int ReadingElementSize = 316;

    private const int SensorNameOffset = 8;
    private const int ReadingTypeOffset = 0;
    private const int ReadingLabelOriginalOffset = 12;
    private const int ReadingLabelUserOffset = 140;

    /// <summary>
    /// The three fields a caller that already located an element reads directly, without
    /// copying the element out: the two that identify which sensor it is, and the value.
    /// </summary>
    public const int ReadingSensorIndexOffset = 4;

    public const int ReadingIdOffset = 8;

    public const int ReadingValueOffset = 284;

    private const int StringLength = 128;

    /// <summary>
    /// An element far larger than the one we know is a layout this parser does not understand;
    /// an element count far past what any machine reports is a corrupt or hostile block. Both
    /// bound the scan before it starts.
    /// </summary>
    private const int MaxElementSize = 4096;

    private const int MaxElementCount = 65536;

    public static bool TryReadHeader(
        ReadOnlySpan<byte> header,
        long capacity,
        out HwInfoSharedMemoryHeader parsed)
    {
        parsed = default;
        if (header.Length < HeaderSize || capacity < HeaderSize)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != ActiveSignature ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != SupportedVersion)
        {
            return false;
        }

        long pollTime = BinaryPrimitives.ReadInt64LittleEndian(header[12..]);
        uint sensorOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        uint sensorSize = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        uint sensorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
        uint readingOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        uint readingSize = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
        uint readingCount = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);

        if (!IsSectionWithin(capacity, sensorOffset, sensorSize, sensorCount, SensorElementSize) ||
            !IsSectionWithin(capacity, readingOffset, readingSize, readingCount, ReadingElementSize))
        {
            return false;
        }

        parsed = new HwInfoSharedMemoryHeader(
            pollTime,
            sensorOffset,
            (int)sensorSize,
            (int)sensorCount,
            readingOffset,
            (int)readingSize,
            (int)readingCount);
        return true;
    }

    /// <summary>
    /// Reads one reading element. <paramref name="element"/> must be at least
    /// <see cref="ReadingElementSize"/> bytes; a longer element from a newer HWiNFO is fine,
    /// because everything read here lives in the leading bytes.
    /// </summary>
    public static bool TryReadReading(
        ReadOnlySpan<byte> element,
        int elementIndex,
        string sensorName,
        out HwInfoSensorReading reading)
    {
        reading = default;
        if (element.Length < ReadingElementSize || elementIndex < 0)
        {
            return false;
        }

        uint rawType = BinaryPrimitives.ReadUInt32LittleEndian(element[ReadingTypeOffset..]);
        uint sensorIndex =
            BinaryPrimitives.ReadUInt32LittleEndian(element[ReadingSensorIndexOffset..]);
        if (sensorIndex > int.MaxValue)
        {
            return false;
        }

        double value = BinaryPrimitives.ReadDoubleLittleEndian(element[ReadingValueOffset..]);
        if (!double.IsFinite(value))
        {
            return false;
        }

        // The original label is the one HWiNFO ships and the one the tables below are written
        // against; the user label only stands in when a build leaves the original empty. A
        // renamed reading therefore still matches, which a user-label-first order would break.
        string label = ReadFixedString(element.Slice(ReadingLabelOriginalOffset, StringLength));
        if (label.Length == 0)
        {
            label = ReadFixedString(element.Slice(ReadingLabelUserOffset, StringLength));
        }

        reading = new HwInfoSensorReading(
            elementIndex,
            rawType <= (uint)HwInfoReadingType.Other
                ? (HwInfoReadingType)rawType
                : HwInfoReadingType.Other,
            (int)sensorIndex,
            BinaryPrimitives.ReadUInt32LittleEndian(element[ReadingIdOffset..]),
            sensorName,
            label,
            value);
        return true;
    }

    /// <summary>Reads one sensor element's name, which is what the readings are grouped under.</summary>
    public static string ReadSensorName(ReadOnlySpan<byte> element) =>
        element.Length < SensorElementSize
            ? string.Empty
            : ReadFixedString(element.Slice(SensorNameOffset, StringLength));

    /// <summary>
    /// A fixed-width, NUL-terminated byte string. Decoded byte-per-character rather than as
    /// UTF-8: HWiNFO writes these in the machine's ANSI code page, a strict UTF-8 decode would
    /// throw on the degree sign in a unit, and every label matched here is ASCII anyway.
    /// </summary>
    public static string ReadFixedString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> text = end < 0 ? bytes : bytes[..end];
        return text.Length == 0 ? string.Empty : Encoding.Latin1.GetString(text).Trim();
    }

    private static bool IsSectionWithin(
        long capacity,
        uint offset,
        uint elementSize,
        uint elementCount,
        int minimumElementSize)
    {
        if (elementSize < (uint)minimumElementSize ||
            elementSize > MaxElementSize ||
            elementCount > MaxElementCount)
        {
            return false;
        }

        // In long arithmetic throughout: the products below overflow 32 bits long before they
        // reach the caps above, and an overflowed bounds check is not a bounds check.
        long end = (long)offset + ((long)elementSize * elementCount);
        return offset >= HeaderSize && end <= capacity;
    }
}
