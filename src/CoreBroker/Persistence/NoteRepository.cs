using System.Globalization;

namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Persistence-only repository for NTE-001. UI and IPC layers receive immutable records and
/// must provide the last <see cref="NoteRecord.UpdatedAtUtc"/> token when saving or deleting.
/// </summary>
public sealed class NoteRepository
{
    private readonly SqliteRepository _repository;

    public NoteRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _repository = new SqliteRepository(database);
    }

    public NoteRecord Create(
        string noteId,
        string title,
        string body,
        NoteBodyFormat bodyFormat,
        DateTimeOffset? nowUtc = null)
    {
        string timestamp = NoteRecord.FormatTimestamp(nowUtc ?? DateTimeOffset.UtcNow);
        var record = new NoteRecord(
            noteId,
            title,
            body,
            bodyFormat,
            timestamp,
            timestamp);

        _repository.Execute(
            "INSERT INTO notes (note_id, title, body, body_format, created_at_utc, updated_at_utc) " +
            "VALUES (@id, @title, @body, @format, @created, @updated);",
            statement =>
            {
                statement.BindText("@id", record.NoteId);
                statement.BindText("@title", record.Title);
                statement.BindText("@body", record.Body);
                statement.BindText("@format", record.BodyFormatValue);
                statement.BindText("@created", record.CreatedAtUtc);
                statement.BindText("@updated", record.UpdatedAtUtc);
            });

        return record;
    }

    public NoteRecord? Get(string noteId)
    {
        ValidateNoteId(noteId);
        NoteRecord? result = null;
        _repository.Query(
            "SELECT note_id, title, body, body_format, created_at_utc, updated_at_utc " +
            "FROM notes WHERE note_id = @id;",
            statement => statement.BindText("@id", noteId),
            statement => result = ReadRecord(statement));
        return result;
    }

    public IReadOnlyList<NoteRecord> List()
    {
        var records = new List<NoteRecord>();
        _repository.Query(
            "SELECT note_id, title, body, body_format, created_at_utc, updated_at_utc " +
            "FROM notes ORDER BY updated_at_utc DESC, note_id ASC;",
            bind: null,
            statement => records.Add(ReadRecord(statement)));
        return records;
    }

    public IReadOnlyList<NoteRecord> Search(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
        {
            return List();
        }

        string pattern = $"%{EscapeLikePattern(query)}%";
        var records = new List<NoteRecord>();
        _repository.Query(
            "SELECT note_id, title, body, body_format, created_at_utc, updated_at_utc " +
            "FROM notes WHERE title LIKE @pattern ESCAPE '\\' " +
            "OR body LIKE @pattern ESCAPE '\\' " +
            "ORDER BY updated_at_utc DESC, note_id ASC;",
            statement => statement.BindText("@pattern", pattern),
            statement => records.Add(ReadRecord(statement)));
        return records;
    }

    public NoteRecord Update(
        string noteId,
        string expectedUpdatedAtUtc,
        string title,
        string body,
        NoteBodyFormat bodyFormat)
    {
        ValidateNoteId(noteId);
        string expected = NormalizeExpectedTimestamp(expectedUpdatedAtUtc);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        string updated = NextTimestamp(expected);

        int changes = _repository.Execute(
            "UPDATE notes SET title = @title, body = @body, body_format = @format, " +
            "updated_at_utc = @updated WHERE note_id = @id AND updated_at_utc = @expected;",
            statement =>
            {
                statement.BindText("@title", title);
                statement.BindText("@body", body);
                statement.BindText("@format", ToStorageValue(bodyFormat));
                statement.BindText("@updated", updated);
                statement.BindText("@id", noteId);
                statement.BindText("@expected", expected);
            });

        if (changes == 0)
        {
            ThrowUpdateFailure(noteId, expected);
        }

        return Get(noteId) ?? throw new NoteNotFoundException(noteId);
    }

    public void Delete(string noteId, string expectedUpdatedAtUtc)
    {
        ValidateNoteId(noteId);
        string expected = NormalizeExpectedTimestamp(expectedUpdatedAtUtc);
        int changes = _repository.Execute(
            "DELETE FROM notes WHERE note_id = @id AND updated_at_utc = @expected;",
            statement =>
            {
                statement.BindText("@id", noteId);
                statement.BindText("@expected", expected);
            });

        if (changes == 0)
        {
            ThrowUpdateFailure(noteId, expected);
        }
    }

    private static NoteRecord ReadRecord(SqliteStatement statement)
    {
        return new NoteRecord(
            statement.ReadText(0) ?? throw new SqliteException(1, "notes.note_id is NULL."),
            statement.ReadText(1) ?? string.Empty,
            statement.ReadText(2) ?? string.Empty,
            ParseBodyFormat(statement.ReadText(3)),
            statement.ReadText(4) ?? throw new SqliteException(1, "notes.created_at_utc is NULL."),
            statement.ReadText(5) ?? throw new SqliteException(1, "notes.updated_at_utc is NULL."));
    }

    private void ThrowUpdateFailure(string noteId, string expected)
    {
        NoteRecord? current = Get(noteId);
        if (current is null)
        {
            throw new NoteNotFoundException(noteId);
        }

        throw new NoteRevisionConflictException(noteId, expected, current.UpdatedAtUtc);
    }

    private static string NormalizeExpectedTimestamp(string value) =>
        NoteRecord.FormatTimestamp(NoteRecord.ParseTimestamp(value, nameof(value)));

    private static string NextTimestamp(string expected)
    {
        DateTimeOffset expectedValue = NoteRecord.ParseTimestamp(expected, nameof(expected));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return NoteRecord.FormatTimestamp(now <= expectedValue ? expectedValue.AddTicks(1) : now);
    }

    private static NoteBodyFormat ParseBodyFormat(string? value) => value switch
    {
        "plain-text" => NoteBodyFormat.PlainText,
        "markdown" => NoteBodyFormat.Markdown,
        _ => throw new SqliteException(1, $"Unknown notes.body_format '{value}'."),
    };

    private static string ToStorageValue(NoteBodyFormat value) => value switch
    {
        NoteBodyFormat.PlainText => "plain-text",
        NoteBodyFormat.Markdown => "markdown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static void ValidateNoteId(string noteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        if (noteId.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(noteId));
        }
    }
}
