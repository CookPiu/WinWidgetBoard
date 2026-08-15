using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.CoreBroker.Ipc;

/// <summary>
/// Owns the connection-scoped cards.subscribe state and fans out the latest
/// snapshot to matching clients. The hub never writes to a pipe directly.
/// </summary>
public sealed class CardSnapshotSubscriptionHub : ICardSnapshotPublisher, IDisposable
{
    public const int DefaultSubscriptionBufferCapacity = 32;
    public const int DefaultLatestSnapshotCapacity = 1024;

    private readonly object _gate = new();
    private readonly int _subscriptionBufferCapacity;
    private readonly int _latestSnapshotCapacity;
    private readonly Dictionary<Guid, CardSnapshotSubscription> _subscriptions = [];
    private readonly Dictionary<string, CardStateSnapshot> _latestSnapshots =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _latestSnapshotOrder = [];
    private bool _disposed;

    public CardSnapshotSubscriptionHub(
        int subscriptionBufferCapacity = DefaultSubscriptionBufferCapacity,
        int latestSnapshotCapacity = DefaultLatestSnapshotCapacity)
    {
        if (subscriptionBufferCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subscriptionBufferCapacity),
                subscriptionBufferCapacity,
                "Subscription buffer capacity must be positive.");
        }

        if (latestSnapshotCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latestSnapshotCapacity),
                latestSnapshotCapacity,
                "Latest snapshot capacity must be positive.");
        }

        _subscriptionBufferCapacity = subscriptionBufferCapacity;
        _latestSnapshotCapacity = latestSnapshotCapacity;
    }

    public bool TrySubscribe(
        Guid connectionId,
        CardsSubscribeRequest? request,
        out CardsSubscribeResponse? response,
        out CardSnapshotSubscription? subscription)
    {
        response = null;
        subscription = null;
        if (connectionId == Guid.Empty ||
            !TryNormalizeRequest(request, out CardsSubscribeRequest? parsedRequest) ||
            parsedRequest is null)
        {
            return false;
        }

        CardsSubscribeRequest normalized = parsedRequest;

        CardSnapshotSubscription created;
        CardSnapshotSubscription? previous;
        CardStateSnapshot[] initialSnapshots;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            created = new CardSnapshotSubscription(
                Guid.NewGuid(),
                normalized.InstanceIds,
                normalized.Visibility,
                _subscriptionBufferCapacity);
            previous = _subscriptions.GetValueOrDefault(connectionId);
            _subscriptions[connectionId] = created;
            initialSnapshots = normalized.InstanceIds
                .Where(_ => normalized.Visibility.PanelVisible)
                .Where(IsVisibleForSubscription)
                .Where(instanceId => _latestSnapshots.ContainsKey(instanceId))
                .Select(instanceId => CloneSnapshot(_latestSnapshots[instanceId]))
                .ToArray();
        }

        previous?.Dispose();
        response = new CardsSubscribeResponse
        {
            SubscriptionId = created.SubscriptionId,
            InitialSnapshots = initialSnapshots,
        };
        subscription = created;
        return true;

        bool IsVisibleForSubscription(string instanceId) =>
            normalized.Visibility.VisibleInstanceIds.Count == 0 ||
            normalized.Visibility.VisibleInstanceIds.Contains(
                instanceId,
                StringComparer.Ordinal);
    }

    public void Remove(Guid connectionId)
    {
        CardSnapshotSubscription? subscription;
        lock (_gate)
        {
            if (!_subscriptions.Remove(connectionId, out subscription))
            {
                return;
            }
        }

        subscription.Dispose();
    }

    public ValueTask PublishAsync(
        CardStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        CardStateSnapshot normalized = CloneSnapshot(snapshot);
        CardSnapshotSubscription[] matchingSubscriptions;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_latestSnapshots.ContainsKey(normalized.InstanceId))
            {
                while (_latestSnapshots.Count >= _latestSnapshotCapacity &&
                       _latestSnapshotOrder.Count > 0)
                {
                    string evictedInstanceId = _latestSnapshotOrder.Dequeue();
                    _latestSnapshots.Remove(evictedInstanceId);
                }

                _latestSnapshotOrder.Enqueue(normalized.InstanceId);
            }

            _latestSnapshots[normalized.InstanceId] = normalized;
            matchingSubscriptions = _subscriptions.Values
                .Where(subscription => subscription.Accepts(normalized.InstanceId))
                .ToArray();
        }

        foreach (CardSnapshotSubscription subscription in matchingSubscriptions)
        {
            subscription.Enqueue(normalized);
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        CardSnapshotSubscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = _subscriptions.Values.ToArray();
            _subscriptions.Clear();
            _latestSnapshots.Clear();
            _latestSnapshotOrder.Clear();
        }

        foreach (CardSnapshotSubscription subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    private static bool TryNormalizeRequest(
        CardsSubscribeRequest? request,
        out CardsSubscribeRequest? normalized)
    {
        normalized = null;
        if (request is null || request.Visibility is null)
        {
            return false;
        }

        if (!TryNormalizeIdentifiers(
                request.InstanceIds,
                CardsContract.MaxInstanceIds,
                out string[] instanceIds) ||
            !TryNormalizeIdentifiers(
                request.Visibility.VisibleInstanceIds,
                CardsContract.MaxInstanceIds,
                out string[] visibleInstanceIds))
        {
            return false;
        }

        var instanceSet = instanceIds.ToHashSet(StringComparer.Ordinal);
        if (visibleInstanceIds.Any(instanceId => !instanceSet.Contains(instanceId)))
        {
            return false;
        }

        normalized = new CardsSubscribeRequest
        {
            InstanceIds = Array.AsReadOnly(instanceIds),
            Visibility = new CardSubscriptionVisibility
            {
                PanelVisible = request.Visibility.PanelVisible,
                VisibleInstanceIds = Array.AsReadOnly(visibleInstanceIds),
            },
        };
        return true;
    }

    private static bool TryNormalizeIdentifiers(
        IReadOnlyList<string>? values,
        int maximumCount,
        out string[] normalized)
    {
        normalized = [];
        if (values is null || values.Count > maximumCount)
        {
            return false;
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? value in values)
        {
            if (!CardsContract.IsValidIdentifier(
                    value,
                    CardsContract.MaxInstanceIdLength) ||
                !unique.Add(value!))
            {
                return false;
            }
        }

        normalized = unique.Count == values.Count
            ? values.ToArray()
            : unique.ToArray();
        return true;
    }

    private static CardStateSnapshot CloneSnapshot(CardStateSnapshot snapshot) =>
        new()
        {
            InstanceId = snapshot.InstanceId,
            CardTypeId = snapshot.CardTypeId,
            SchemaVersion = snapshot.SchemaVersion,
            Sequence = snapshot.Sequence,
            GeneratedAtUtc = snapshot.GeneratedAtUtc.ToUniversalTime(),
            ValidUntilUtc = snapshot.ValidUntilUtc?.ToUniversalTime(),
            Status = snapshot.Status,
            Freshness = snapshot.Freshness,
            Payload = snapshot.Payload.ValueKind == JsonValueKind.Undefined
                ? default
                : snapshot.Payload.Clone(),
            AllowedActions = snapshot.AllowedActions?.ToArray() ?? Array.Empty<string>(),
            DiagnosticCode = snapshot.DiagnosticCode,
        };
}

public sealed class CardSnapshotSubscription : IDisposable
{
    private readonly HashSet<string> _instanceIds;
    private readonly HashSet<string> _visibleInstanceIds;
    private readonly bool _panelVisible;
    private readonly CardSnapshotEventBuffer _buffer;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _wakePending;
    private Action? _overflowHandler;
    private int _overflowNotified;
    private int _disposed;

    internal CardSnapshotSubscription(
        Guid subscriptionId,
        IReadOnlyList<string> instanceIds,
        CardSubscriptionVisibility visibility,
        int bufferCapacity)
    {
        SubscriptionId = subscriptionId;
        _instanceIds = instanceIds.ToHashSet(StringComparer.Ordinal);
        _visibleInstanceIds = visibility.VisibleInstanceIds.ToHashSet(StringComparer.Ordinal);
        _panelVisible = visibility.PanelVisible;
        _buffer = new CardSnapshotEventBuffer(bufferCapacity);
    }

    public Guid SubscriptionId { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal bool Accepts(string instanceId) =>
        !IsDisposed &&
        _panelVisible &&
        _instanceIds.Contains(instanceId) &&
        (_visibleInstanceIds.Count == 0 || _visibleInstanceIds.Contains(instanceId));

    internal void SetOverflowHandler(Action overflowHandler)
    {
        ArgumentNullException.ThrowIfNull(overflowHandler);
        _overflowHandler = overflowHandler;
        if (_buffer.IsOverflowed)
        {
            NotifyOverflow();
        }
    }

    internal void Enqueue(CardStateSnapshot snapshot)
    {
        if (IsDisposed || !Accepts(snapshot.InstanceId))
        {
            return;
        }

        CardSnapshotPublishOutcome outcome = _buffer.Publish(snapshot);
        if (outcome is not CardSnapshotPublishOutcome.IgnoredOverflow)
        {
            Signal();
        }

        if (outcome == CardSnapshotPublishOutcome.Overflowed)
        {
            NotifyOverflow();
        }
    }

    public async ValueTask<IReadOnlyList<CardStateSnapshot>> WaitAndDrainAsync(
        CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _wakePending, 0);
        if (IsDisposed)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (_buffer.IsOverflowed)
        {
            throw new CardSnapshotSubscriptionOverflowException(SubscriptionId);
        }

        IReadOnlyList<CardStateSnapshot> snapshots = _buffer.Drain(
            CardsContract.MaxInstanceIds);
        if (_buffer.PendingCount > 0)
        {
            Signal();
        }

        return snapshots;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Signal();
        }
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) != 0)
        {
            return;
        }

        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // The subscription is only disposed after its writer has stopped.
        }
    }

    private void NotifyOverflow()
    {
        if (Interlocked.Exchange(ref _overflowNotified, 1) != 0)
        {
            return;
        }

        _overflowHandler?.Invoke();
    }
}

public sealed class CardSnapshotSubscriptionOverflowException : Exception
{
    public CardSnapshotSubscriptionOverflowException(Guid subscriptionId)
        : base($"Card snapshot subscription '{subscriptionId}' overflowed.")
    {
        SubscriptionId = subscriptionId;
    }

    public Guid SubscriptionId { get; }
}
