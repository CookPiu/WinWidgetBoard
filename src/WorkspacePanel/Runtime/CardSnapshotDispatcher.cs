using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// Marshals CoreBroker card snapshots onto the WorkspacePanel runtime's UI
/// dispatcher. The dispatcher delegate is injected so this layer remains
/// testable and does not reference WinUI DispatcherQueue directly.
/// </summary>
public sealed class CardSnapshotDispatcher
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CardRuntimeInstance> _runtimes =
        new(StringComparer.Ordinal);
    private readonly Func<
        Func<CardSnapshotDispatchResult>,
        ValueTask<CardSnapshotDispatchResult>> _dispatch;

    public CardSnapshotDispatcher(
        Func<Func<CardSnapshotDispatchResult>, ValueTask<CardSnapshotDispatchResult>> dispatch)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public bool Register(CardRuntimeInstance runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
        {
            return _runtimes.TryAdd(runtime.InstanceId, runtime);
        }
    }

    public bool Unregister(string instanceId, CardRuntimeInstance runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
        {
            return _runtimes.TryGetValue(instanceId, out CardRuntimeInstance? current) &&
                ReferenceEquals(current, runtime) &&
                _runtimes.Remove(instanceId);
        }
    }

    public ValueTask<CardSnapshotDispatchResult> DispatchAsync(
        CardStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatch(() => ApplyCore(snapshot));
    }

    private CardSnapshotDispatchResult ApplyCore(CardStateSnapshot snapshot)
    {
        if (!IsValidSnapshot(snapshot))
        {
            return CardSnapshotDispatchResult.InvalidSnapshot;
        }

        CardRuntimeInstance? runtime;
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(snapshot.InstanceId, out runtime))
            {
                return CardSnapshotDispatchResult.UnknownInstance;
            }
        }

        if (!string.Equals(
                runtime.Definition.CardTypeId,
                snapshot.CardTypeId,
                StringComparison.Ordinal))
        {
            return CardSnapshotDispatchResult.CardTypeMismatch;
        }

        if (runtime.Snapshot.SchemaVersion != snapshot.SchemaVersion)
        {
            return CardSnapshotDispatchResult.SchemaMismatch;
        }

        CardRuntimeSnapshot runtimeSnapshot = new(
            snapshot.InstanceId,
            snapshot.CardTypeId,
            snapshot.SchemaVersion,
            snapshot.Sequence,
            snapshot.GeneratedAtUtc,
            MapFreshness(snapshot.Freshness),
            MapStatus(snapshot.Status),
            snapshot.Payload,
            snapshot.AllowedActions,
            snapshot.DiagnosticCode);
        try
        {
            return runtime.ApplySnapshot(runtimeSnapshot)
                ? CardSnapshotDispatchResult.Applied
                : CardSnapshotDispatchResult.IgnoredOlder;
        }
        catch (ObjectDisposedException)
        {
            return CardSnapshotDispatchResult.Disposed;
        }
    }

    private static bool IsValidSnapshot(CardStateSnapshot snapshot) =>
        CardsContract.IsValidIdentifier(
            snapshot.InstanceId,
            CardsContract.MaxInstanceIdLength) &&
        CardsContract.IsValidIdentifier(
            snapshot.CardTypeId,
            CardsContract.MaxCardTypeIdLength) &&
        snapshot.SchemaVersion > 0 &&
        snapshot.Sequence >= 0 &&
        snapshot.GeneratedAtUtc != default &&
        snapshot.GeneratedAtUtc.Offset == TimeSpan.Zero &&
        (snapshot.ValidUntilUtc is null ||
            snapshot.ValidUntilUtc.Value.Offset == TimeSpan.Zero) &&
        Enum.IsDefined(snapshot.Status) &&
        Enum.IsDefined(snapshot.Freshness) &&
        snapshot.Payload.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
        snapshot.AllowedActions is not null &&
        snapshot.AllowedActions.Count <= CardsContract.MaxAllowedActions &&
        snapshot.AllowedActions.All(action => CardsContract.IsValidIdentifier(action, 128)) &&
        (snapshot.DiagnosticCode is null ||
            CardsContract.IsValidIdentifier(
                snapshot.DiagnosticCode,
                CardsContract.MaxDiagnosticCodeLength));

    private static CardRuntimeStatus MapStatus(CardSnapshotStatus status) =>
        status switch
        {
            CardSnapshotStatus.Loading => CardRuntimeStatus.Loading,
            CardSnapshotStatus.Ready => CardRuntimeStatus.Ready,
            CardSnapshotStatus.Empty => CardRuntimeStatus.Empty,
            CardSnapshotStatus.Stale => CardRuntimeStatus.Stale,
            CardSnapshotStatus.Offline => CardRuntimeStatus.Offline,
            CardSnapshotStatus.PermissionRequired => CardRuntimeStatus.PermissionRequired,
            CardSnapshotStatus.Unavailable => CardRuntimeStatus.Unavailable,
            CardSnapshotStatus.Error => CardRuntimeStatus.Error,
            CardSnapshotStatus.Disabled => CardRuntimeStatus.Disabled,
            _ => CardRuntimeStatus.Unknown,
        };

    private static CardRuntimeFreshness MapFreshness(CardSnapshotFreshness freshness) =>
        freshness switch
        {
            CardSnapshotFreshness.Fresh => CardRuntimeFreshness.Fresh,
            CardSnapshotFreshness.Stale => CardRuntimeFreshness.Stale,
            _ => CardRuntimeFreshness.Unknown,
        };
}

public enum CardSnapshotDispatchResult
{
    Applied = 0,
    IgnoredOlder,
    UnknownInstance,
    CardTypeMismatch,
    SchemaMismatch,
    InvalidSnapshot,
    Disposed,
}
