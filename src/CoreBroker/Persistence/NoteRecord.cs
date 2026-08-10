using System.Globalization;

namespace WinWidgetBoard.CoreBroker.Persistence;

public enum NoteBodyFormat
{
    PlainText,
    Markdown,
}

/// <summary>
/// Persisted note data for the NTE-001 storage boundary. Timestamps are canonical UTC
/// round-trip strings so the persisted value can also be used as an optimistic-concurrency token.
/// </summary>
public sealed record NoteRecord
{
    public NoteRecord(
        string noteId,
        string title,
        string body,
        NoteBodyFormat bodyFormat,
        string createdAtUtc,
        string updatedAtUtc)
    {
        NoteId = ValidateId(noteId);
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Body = body ?? throw new ArgumentNullException(nameof(body));
        BodyFormat = ValidateBodyFormat(bodyFormat);
        CreatedAtUtc = NormalizeTimestamp(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = NormalizeTimestamp(updatedAtUtc, nameof(updatedAtUtc));

        if (string.CompareOrdinal(UpdatedAtUtc, CreatedAtUtc) < 0)
        {
            throw new ArgumentException(
                "updatedAtUtc cannot be earlier than createdAtUtc.",
                nameof(updatedAtUtc));
        }
    }

    public string NoteId { get; }

    public string Title { get; }

    public string Body { get; }

    public NoteBodyFormat BodyFormat { get; }

    public string CreatedAtUtc { get; }

    public string UpdatedAtUtc { get; }

    internal string BodyFormatValue => BodyFormat switch
    {
        NoteBodyFormat.PlainText => "plain-text",
        NoteBodyFormat.Markdown => "markdown",
        _ => throw new ArgumentOutOfRangeException(nameof(BodyFormat)),
    };

    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseTimestamp(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            throw new ArgumentException(
                "Timestamp must be a valid ISO-8601 value.",
                parameterName);
        }

        return parsed.ToUniversalTime();
    }

    private static string ValidateId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Note IDs are limited to 200 characters.");
        }

        return value;
    }

    private static NoteBodyFormat ValidateBodyFormat(NoteBodyFormat value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }

    private static string NormalizeTimestamp(string value, string parameterName) =>
        FormatTimestamp(ParseTimestamp(value, parameterName));
}

public sealed class NoteNotFoundException : KeyNotFoundException
{
    public NoteNotFoundException(string noteId)
        : base($"The note '{noteId}' was not found.")
    {
        NoteId = noteId;
    }

    public string NoteId { get; }
}

public sealed class NoteRevisionConflictException : InvalidOperationException
{
    public NoteRevisionConflictException(
        string noteId,
        string expectedUpdatedAtUtc,
        string actualUpdatedAtUtc)
        : base($"The note '{noteId}' changed since the expected revision was read.")
    {
        NoteId = noteId;
        ExpectedUpdatedAtUtc = expectedUpdatedAtUtc;
        ActualUpdatedAtUtc = actualUpdatedAtUtc;
    }

    public string NoteId { get; }

    public string ExpectedUpdatedAtUtc { get; }

    public string ActualUpdatedAtUtc { get; }
}
