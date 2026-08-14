using System.Text.Json;

namespace WinWidgetBoard.Contracts.Protocol;

public static class CardsContract
{
    public const string SubscribeMethod = "cards.subscribe";
    public const string SnapshotEventMethod = "cards.snapshot";

    public const string RefreshActionId = "refresh";
    public const string OpenDiagnosticsActionId = "diagnostics.open";
    public const string DisableActionId = "disable";

    public const int MaxInstanceIds = 100;
    public const int MaxInstanceIdLength = 200;
    public const int MaxCardTypeIdLength = 128;
    public const int MaxDiagnosticCodeLength = 128;
    public const int MaxAllowedActions = 32;

    public static IReadOnlyList<string> Methods { get; } =
    [
        SubscribeMethod,
    ];

    public static bool IsValidIdentifier(
        string? value,
        int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsWhiteSpace) &&
        !value.Any(char.IsControl);
}

public sealed record CardsSubscribeRequest
{
    public IReadOnlyList<string> InstanceIds { get; init; } =
        Array.Empty<string>();

    public CardSubscriptionVisibility Visibility { get; init; } = new();
}

public sealed record CardSubscriptionVisibility
{
    public bool PanelVisible { get; init; }

    public IReadOnlyList<string> VisibleInstanceIds { get; init; } =
        Array.Empty<string>();
}

public sealed record CardsSubscribeResponse
{
    public Guid SubscriptionId { get; init; }

    public IReadOnlyList<CardStateSnapshot> InitialSnapshots { get; init; } =
        Array.Empty<CardStateSnapshot>();
}

public sealed record CardSnapshotEvent
{
    public CardStateSnapshot Snapshot { get; init; } = new();
}

public enum CardSnapshotStatus
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

public enum CardSnapshotFreshness
{
    Unknown = 0,
    Fresh,
    Stale,
}

public sealed record CardStateSnapshot
{
    public string InstanceId { get; init; } = string.Empty;

    public string CardTypeId { get; init; } = string.Empty;

    public int SchemaVersion { get; init; }

    public long Sequence { get; init; }

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public DateTimeOffset? ValidUntilUtc { get; init; }

    public CardSnapshotStatus Status { get; init; }

    public CardSnapshotFreshness Freshness { get; init; }

    public JsonElement Payload { get; init; }

    public IReadOnlyList<string> AllowedActions { get; init; } =
        Array.Empty<string>();

    public string? DiagnosticCode { get; init; }
}
