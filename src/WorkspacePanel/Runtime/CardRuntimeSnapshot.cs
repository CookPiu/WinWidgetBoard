using System.Collections.Frozen;
using System.Text.Json;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

public sealed class CardRuntimeSnapshot
{
    private static readonly JsonElement EmptyPayload = CreateEmptyPayload();
    private readonly FrozenSet<string> _allowedActionIds;

    public CardRuntimeSnapshot(
        string instanceId,
        string cardTypeId,
        int schemaVersion,
        long sequence,
        DateTimeOffset timestampUtc,
        CardRuntimeFreshness freshness,
        CardRuntimeStatus status,
        JsonElement payload = default,
        IEnumerable<string>? allowedActionIds = null,
        string? errorCode = null)
    {
        InstanceId = CardRuntimeContractGuards.RequireIdentifier(
            instanceId,
            nameof(instanceId));
        CardTypeId = CardRuntimeContractGuards.RequireIdentifier(
            cardTypeId,
            nameof(cardTypeId));
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Schema version must be positive.");
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "Sequence cannot be negative.");
        }

        SchemaVersion = schemaVersion;
        Sequence = sequence;
        TimestampUtc = timestampUtc.ToUniversalTime();
        Freshness = Enum.IsDefined(freshness)
            ? freshness
            : CardRuntimeFreshness.Unknown;
        Status = Enum.IsDefined(status)
            ? status
            : CardRuntimeStatus.Unknown;
        Payload = payload.ValueKind == JsonValueKind.Undefined
            ? EmptyPayload
            : payload.Clone();

        string? validatedErrorCode = errorCode is null
            ? null
            : CardRuntimeContractGuards.RequireIdentifier(
                errorCode,
                nameof(errorCode));
        if (Status == CardRuntimeStatus.Unknown)
        {
            _allowedActionIds = Array.Empty<string>()
                .ToFrozenSet(StringComparer.Ordinal);
            ErrorCode = validatedErrorCode ??
                CardRuntimeErrorCodes.UnknownStatus;
            return;
        }

        _allowedActionIds = CreateAllowedActions(allowedActionIds);
        ErrorCode = validatedErrorCode;
    }

    public string InstanceId { get; }

    public string CardTypeId { get; }

    public int SchemaVersion { get; }

    public long Sequence { get; }

    public DateTimeOffset TimestampUtc { get; }

    public CardRuntimeFreshness Freshness { get; }

    public CardRuntimeStatus Status { get; }

    public JsonElement Payload { get; }

    public IReadOnlySet<string> AllowedActionIds => _allowedActionIds;

    public string? ErrorCode { get; }

    public bool AllowsAction(string actionId) =>
        CardRuntimeContractGuards.IsValidIdentifier(actionId) &&
        _allowedActionIds.Contains(actionId);

    public static CardRuntimeSnapshot CreateError(
        CardRuntimeSnapshot previous,
        string errorCode,
        DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return new CardRuntimeSnapshot(
            previous.InstanceId,
            previous.CardTypeId,
            previous.SchemaVersion,
            checked(previous.Sequence + 1),
            timestampUtc,
            CardRuntimeFreshness.Stale,
            CardRuntimeStatus.Error,
            previous.Payload,
            [
                CardRuntimeActionIds.Retry,
                CardRuntimeActionIds.OpenDiagnostics,
                CardRuntimeActionIds.Disable,
            ],
            errorCode);
    }

    private static FrozenSet<string> CreateAllowedActions(
        IEnumerable<string>? actionIds)
    {
        if (actionIds is null)
        {
            return Array.Empty<string>()
                .ToFrozenSet(StringComparer.Ordinal);
        }

        var validatedActionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string actionId in actionIds)
        {
            validatedActionIds.Add(
                CardRuntimeContractGuards.RequireIdentifier(
                    actionId,
                    nameof(actionIds)));
        }

        return validatedActionIds.ToFrozenSet(StringComparer.Ordinal);
    }

    private static JsonElement CreateEmptyPayload()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

public sealed class CardRuntimeSnapshotChangedEventArgs : EventArgs
{
    public CardRuntimeSnapshotChangedEventArgs(
        CardRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
    }

    public CardRuntimeSnapshot Snapshot { get; }
}
