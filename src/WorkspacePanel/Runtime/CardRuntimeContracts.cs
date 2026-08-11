using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

public enum CardRuntimeStatus
{
    Unknown = 0,
    Loading,
    Ready,
    Empty,
    Stale,
    Offline,
    PermissionRequired,
    Unavailable,
    Error,
    Disabled,
}

public enum CardRuntimeFreshness
{
    Unknown = 0,
    Fresh,
    Stale,
}

public enum CardLifecycleState
{
    Created = 0,
    Initialized,
    Visible,
    Hidden,
    Suspended,
    Disposed,
}

public static class CardRuntimeActionIds
{
    public const string Retry = "retry";
    public const string Refresh = "refresh";
    public const string Edit = "edit";
    public const string Configure = "configure";
    public const string OpenDiagnostics = "diagnostics.open";
    public const string Disable = "disable";
}

public static class CardRuntimeErrorCodes
{
    public const string UnknownStatus = "runtime.unknown-status";
    public const string UnknownCard = "runtime.unknown-card";
    public const string SnapshotProviderFailed =
        "runtime.snapshot-provider-failed";
    public const string NoteUnavailable = "runtime.note-unavailable";
    public const string NoteFailed = "runtime.note-failed";
    public const string NoteStatusUnknown = "runtime.note-status-unknown";
    public const string NotImplemented = "runtime.not-implemented";
}

public interface ICardDefinition
{
    string CardTypeId { get; }

    string TitleResourceKey { get; }

    CardSize DefaultSize { get; }

    IReadOnlySet<CardSize> SupportedSizes { get; }

    bool SupportsSize(CardSize size);
}

public interface ICardLifecycle
{
    CardLifecycleState LifecycleState { get; }

    bool TransitionTo(CardLifecycleState targetState);
}

internal static class CardRuntimeContractGuards
{
    private const int MaxIdentifierLength = 128;

    public static string RequireIdentifier(
        string value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!IsValidIdentifier(value))
        {
            throw new ArgumentException(
                $"'{parameterName}' must be at most {MaxIdentifierLength} " +
                "characters and cannot contain whitespace or control characters.",
                parameterName);
        }

        return value;
    }

    public static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxIdentifierLength &&
        !value.Any(char.IsWhiteSpace) &&
        !value.Any(char.IsControl);
}
