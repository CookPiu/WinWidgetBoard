namespace WinWidgetBoard.Contracts.Protocol;

public static class NotesContract
{
    public const string SaveMethod = "notes.save";
    public const string GetMethod = "notes.get";
    public const string SearchMethod = "notes.search";
    public const string DeleteMethod = "notes.delete";

    public const string PlainTextFormat = "plain-text";
    public const string MarkdownFormat = "markdown";

    public const int MaxNoteIdLength = 200;
    public const int MaxTitleLength = 512;
    public const int MaxBodyLength = 256 * 1024;
    public const int MaxTimestampLength = 64;
    public const int MaxSearchLength = 256;
    public const int MaxSearchResults = 100;

    public static IReadOnlyList<string> Methods { get; } =
    [
        SaveMethod,
        GetMethod,
        SearchMethod,
        DeleteMethod,
    ];
}

public sealed record NoteSaveRequest
{
    public Guid ClientOperationId { get; init; }

    public string? NoteId { get; init; }

    public string? Title { get; init; }

    public string? Body { get; init; }

    public string? BodyFormat { get; init; }

    public string? ExpectedUpdatedAtUtc { get; init; }
}

public sealed record NoteSaveResponse
{
    public Guid ClientOperationId { get; init; }

    public NoteDto Note { get; init; } = new();
}

public sealed record NoteGetRequest
{
    public string? NoteId { get; init; }
}

public sealed record NoteGetResponse
{
    public NoteDto Note { get; init; } = new();
}

public sealed record NoteSearchRequest
{
    public string? Query { get; init; }
}

public sealed record NoteSearchResponse
{
    public IReadOnlyList<NoteDto> Notes { get; init; } = Array.Empty<NoteDto>();
}

public sealed record NoteDeleteRequest
{
    public Guid ClientOperationId { get; init; }

    public string? NoteId { get; init; }

    public string? ExpectedUpdatedAtUtc { get; init; }
}

public sealed record NoteDeleteResponse
{
    public Guid ClientOperationId { get; init; }

    public bool Deleted { get; init; }
}

public sealed record NoteDto
{
    public string NoteId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    public string BodyFormat { get; init; } = NotesContract.PlainTextFormat;

    public string CreatedAtUtc { get; init; } = string.Empty;

    public string UpdatedAtUtc { get; init; } = string.Empty;
}
