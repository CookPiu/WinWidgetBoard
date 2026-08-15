using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

public interface ICardSnapshotPublisher
{
    ValueTask PublishAsync(
        CardStateSnapshot snapshot,
        CancellationToken cancellationToken);
}

/// <summary>
/// Maps the scheduler's provider result into the versioned card snapshot
/// contract. It owns the per-card sequence and never references WorkspacePanel
/// or WinUI.
/// </summary>
public sealed class ProviderCardSnapshotAdapter : IProviderRefreshSink
{
    private readonly string _instanceId;
    private readonly string _cardTypeId;
    private readonly int _schemaVersion;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ICardSnapshotPublisher _publisher;
    private readonly IReadOnlyList<string> _readyActions;
    private readonly IReadOnlyList<string> _failureActions;
    private long _sequence;

    public ProviderCardSnapshotAdapter(
        string instanceId,
        string cardTypeId,
        int schemaVersion,
        ICardSnapshotPublisher publisher,
        IEnumerable<string>? readyActions = null,
        IEnumerable<string>? failureActions = null,
        Func<DateTimeOffset>? utcNow = null,
        long initialSequence = 0)
    {
        if (!CardsContract.IsValidIdentifier(
                instanceId,
                CardsContract.MaxInstanceIdLength))
        {
            throw new ArgumentException(
                "Instance ID is invalid.",
                nameof(instanceId));
        }

        if (!CardsContract.IsValidIdentifier(
                cardTypeId,
                CardsContract.MaxCardTypeIdLength))
        {
            throw new ArgumentException(
                "Card type ID is invalid.",
                nameof(cardTypeId));
        }

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Schema version must be positive.");
        }

        if (initialSequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialSequence),
                initialSequence,
                "Initial sequence cannot be negative.");
        }

        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _instanceId = instanceId;
        _cardTypeId = cardTypeId;
        _schemaVersion = schemaVersion;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _readyActions = NormalizeActions(readyActions);
        _failureActions = NormalizeActions(failureActions);
        _sequence = initialSequence;
    }

    public ValueTask ApplyAsync(
        ProviderRefreshRequest request,
        ProviderRefreshResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = _utcNow().ToUniversalTime();
        CardStateSnapshot snapshot = CreateSnapshot(
            result,
            checked(Interlocked.Increment(ref _sequence)),
            now);
        return _publisher.PublishAsync(snapshot, cancellationToken);
    }

    private CardStateSnapshot CreateSnapshot(
        ProviderRefreshResult result,
        long sequence,
        DateTimeOffset now)
    {
        (CardSnapshotStatus status, CardSnapshotFreshness freshness) =
            MapStatus(result.Kind, result.ValidUntilUtc, now);
        IReadOnlyList<string> actions = status is
            CardSnapshotStatus.Error or
            CardSnapshotStatus.Offline or
            CardSnapshotStatus.PermissionRequired or
            CardSnapshotStatus.Unavailable
            ? _failureActions
            : _readyActions;

        string? diagnosticCode = status is
            CardSnapshotStatus.Error or
            CardSnapshotStatus.Offline or
            CardSnapshotStatus.PermissionRequired or
            CardSnapshotStatus.Unavailable
            ? result.ErrorCode
            : null;
        return new CardStateSnapshot
        {
            InstanceId = _instanceId,
            CardTypeId = _cardTypeId,
            SchemaVersion = _schemaVersion,
            Sequence = sequence,
            GeneratedAtUtc = result.ProducedAtUtc.ToUniversalTime(),
            ValidUntilUtc = result.ValidUntilUtc?.ToUniversalTime(),
            Status = status,
            Freshness = freshness,
            Payload = result.Payload.Clone(),
            AllowedActions = actions,
            DiagnosticCode = diagnosticCode,
        };
    }

    private static (CardSnapshotStatus Status, CardSnapshotFreshness Freshness) MapStatus(
        ProviderRefreshResultKind kind,
        DateTimeOffset? validUntilUtc,
        DateTimeOffset now)
    {
        CardSnapshotFreshness freshness = validUntilUtc is { } validUntil &&
            validUntil <= now
            ? CardSnapshotFreshness.Stale
            : validUntilUtc is null
                ? CardSnapshotFreshness.Unknown
                : CardSnapshotFreshness.Fresh;
        return kind switch
        {
            ProviderRefreshResultKind.Success =>
                (CardSnapshotStatus.Ready, freshness),
            ProviderRefreshResultKind.Empty =>
                (CardSnapshotStatus.Empty, freshness),
            ProviderRefreshResultKind.Offline =>
                (CardSnapshotStatus.Offline, CardSnapshotFreshness.Stale),
            ProviderRefreshResultKind.PermissionRequired =>
                (CardSnapshotStatus.PermissionRequired, CardSnapshotFreshness.Unknown),
            ProviderRefreshResultKind.Unavailable =>
                (CardSnapshotStatus.Unavailable, CardSnapshotFreshness.Unknown),
            ProviderRefreshResultKind.Failed or
                ProviderRefreshResultKind.RateLimited or
                ProviderRefreshResultKind.TimedOut or
                _ => (CardSnapshotStatus.Error, CardSnapshotFreshness.Stale),
        };
    }

    private static IReadOnlyList<string> NormalizeActions(
        IEnumerable<string>? actions)
    {
        if (actions is null)
        {
            return Array.Empty<string>();
        }

        string[] normalized = actions
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length > CardsContract.MaxAllowedActions ||
            normalized.Any(action => !CardsContract.IsValidIdentifier(action, 128)))
        {
            throw new ArgumentException(
                "Allowed action IDs are invalid or exceed the limit.",
                nameof(actions));
        }

        return Array.AsReadOnly(normalized);
    }
}

/// <summary>
/// A bounded, per-instance coalescing buffer for future cards.subscribe
/// transport wiring. A slow consumer is marked overflowed instead of silently
/// dropping a state update.
/// </summary>
public sealed class CardSnapshotEventBuffer : ICardSnapshotPublisher
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, CardStateSnapshot> _pending =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _order = [];
    private bool _overflowed;

    public CardSnapshotEventBuffer(int capacity = 100)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "Buffer capacity must be positive.");
        }

        _capacity = capacity;
    }

    public bool IsOverflowed
    {
        get
        {
            lock (_gate)
            {
                return _overflowed;
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public CardSnapshotPublishOutcome Publish(CardStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_overflowed)
            {
                return CardSnapshotPublishOutcome.IgnoredOverflow;
            }

            if (_pending.ContainsKey(snapshot.InstanceId))
            {
                _pending[snapshot.InstanceId] = snapshot;
                return CardSnapshotPublishOutcome.Updated;
            }

            if (_pending.Count >= _capacity)
            {
                _overflowed = true;
                return CardSnapshotPublishOutcome.Overflowed;
            }

            _pending.Add(snapshot.InstanceId, snapshot);
            _order.Enqueue(snapshot.InstanceId);
            return CardSnapshotPublishOutcome.Added;
        }
    }

    public ValueTask PublishAsync(
        CardStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        Publish(snapshot);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<CardStateSnapshot> Drain(int maximumCount)
    {
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                "Maximum count must be positive.");
        }

        var snapshots = new List<CardStateSnapshot>(
            Math.Min(maximumCount, _capacity));
        lock (_gate)
        {
            while (snapshots.Count < maximumCount && _order.Count > 0)
            {
                string instanceId = _order.Dequeue();
                if (_pending.Remove(instanceId, out CardStateSnapshot? snapshot))
                {
                    snapshots.Add(snapshot);
                }
            }
        }

        return snapshots.AsReadOnly();
    }
}

public enum CardSnapshotPublishOutcome
{
    Added = 0,
    Updated,
    Overflowed,
    IgnoredOverflow,
}
