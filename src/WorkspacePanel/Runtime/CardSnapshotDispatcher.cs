using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// Marshals CoreBroker card snapshots onto the WorkspacePanel runtime's UI
/// dispatcher. The dispatcher delegate is injected so this layer remains
/// testable and does not reference WinUI DispatcherQueue directly.
///
/// Runtimes are keyed by their BASE instance ID: the broker publishes for
/// "demo.weather" only, and every duplicate ("demo.weather#2") is fed the same
/// snapshot with its own instance identity stamped back on.
/// </summary>
public sealed class CardSnapshotDispatcher
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<CardRuntimeInstance>> _runtimes =
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
        string baseId = BuiltInCardCatalog.BaseInstanceId(runtime.InstanceId);
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(baseId, out List<CardRuntimeInstance>? registered))
            {
                registered = [];
                _runtimes[baseId] = registered;
            }

            if (registered.Any(existing =>
                    string.Equals(
                        existing.InstanceId,
                        runtime.InstanceId,
                        StringComparison.Ordinal)))
            {
                return false;
            }

            registered.Add(runtime);
            return true;
        }
    }

    public bool Unregister(string instanceId, CardRuntimeInstance runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(runtime);
        string baseId = BuiltInCardCatalog.BaseInstanceId(instanceId);
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(baseId, out List<CardRuntimeInstance>? registered))
            {
                return false;
            }

            int index = registered.FindIndex(existing =>
                ReferenceEquals(existing, runtime));
            if (index < 0)
            {
                return false;
            }

            registered.RemoveAt(index);
            if (registered.Count == 0)
            {
                _runtimes.Remove(baseId);
            }

            return true;
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

        CardRuntimeInstance[] runtimes;
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(
                    BuiltInCardCatalog.BaseInstanceId(snapshot.InstanceId),
                    out List<CardRuntimeInstance>? registered) ||
                registered.Count == 0)
            {
                return CardSnapshotDispatchResult.UnknownInstance;
            }

            runtimes = [.. registered];
        }

        CardSnapshotDispatchResult worst = CardSnapshotDispatchResult.IgnoredOlder;
        bool applied = false;
        foreach (CardRuntimeInstance runtime in runtimes)
        {
            if (!string.Equals(
                    runtime.Definition.CardTypeId,
                    snapshot.CardTypeId,
                    StringComparison.Ordinal))
            {
                worst = CardSnapshotDispatchResult.CardTypeMismatch;
                continue;
            }

            if (runtime.Snapshot.SchemaVersion != snapshot.SchemaVersion)
            {
                worst = CardSnapshotDispatchResult.SchemaMismatch;
                continue;
            }

            // Duplicates receive the broker's snapshot with their own identity stamped
            // back on: the runtime validates that a snapshot names the instance it lands on.
            CardRuntimeSnapshot runtimeSnapshot = new(
                runtime.InstanceId,
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
                // Broker sequences are compared only against other broker sequences; see
                // CardRuntimeInstance.ApplyRemoteSnapshot.
                applied |= runtime.ApplyRemoteSnapshot(runtimeSnapshot);
            }
            catch (ObjectDisposedException)
            {
                worst = CardSnapshotDispatchResult.Disposed;
            }
        }

        return applied ? CardSnapshotDispatchResult.Applied : worst;
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
