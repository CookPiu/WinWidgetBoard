using System.Diagnostics;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Ipc;

/// <summary>
/// Coordinates the connection-scoped cards subscription with the current
/// WorkspacePanel runtime set. It keeps transport work out of XAML event
/// handlers and leaves UI thread switching to CardSnapshotDispatcher.
/// </summary>
public sealed class CardSnapshotSubscriptionCoordinator : IAsyncDisposable
{
    private readonly object _runtimeGate = new();
    private readonly CoreBrokerCardsClient _cardsClient;
    private readonly CardSnapshotDispatcher _snapshotDispatcher;
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Dictionary<string, CardRuntimeInstance> _registeredRuntimes =
        new(StringComparer.Ordinal);
    private int _disposed;
    private bool _isSubscribed;
    private Guid? _subscriptionId;

    public CardSnapshotSubscriptionCoordinator(
        CoreBrokerCardsClient cardsClient,
        CardSnapshotDispatcher snapshotDispatcher)
    {
        _cardsClient = cardsClient ?? throw new ArgumentNullException(nameof(cardsClient));
        _snapshotDispatcher = snapshotDispatcher ??
            throw new ArgumentNullException(nameof(snapshotDispatcher));
        _cardsClient.SnapshotReceived += CardsClient_SnapshotReceived;
    }

    public bool IsSubscribed
    {
        get
        {
            lock (_runtimeGate)
            {
                return _isSubscribed;
            }
        }
    }

    public Guid? SubscriptionId
    {
        get
        {
            lock (_runtimeGate)
            {
                return _subscriptionId;
            }
        }
    }

    public void SynchronizeRuntimes(
        IReadOnlyList<CardRuntimeInstance> runtimes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var desired = new Dictionary<string, CardRuntimeInstance>(
            StringComparer.Ordinal);
        foreach (CardRuntimeInstance runtime in runtimes)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            if (!desired.TryAdd(runtime.InstanceId, runtime))
            {
                throw new ArgumentException(
                    $"Duplicate card runtime instance ID '{runtime.InstanceId}'.",
                    nameof(runtimes));
            }
        }

        lock (_runtimeGate)
        {
            foreach ((string instanceId, CardRuntimeInstance runtime) in
                _registeredRuntimes.ToArray())
            {
                if (desired.TryGetValue(instanceId, out CardRuntimeInstance? current) &&
                    ReferenceEquals(runtime, current))
                {
                    continue;
                }

                _snapshotDispatcher.Unregister(instanceId, runtime);
                _registeredRuntimes.Remove(instanceId);
            }

            foreach ((string instanceId, CardRuntimeInstance runtime) in desired)
            {
                if (_registeredRuntimes.ContainsKey(instanceId))
                {
                    continue;
                }

                _snapshotDispatcher.Register(runtime);
                _registeredRuntimes.Add(instanceId, runtime);
            }
        }
    }

    public async Task<CardsSubscribeResponse> SubscribeAsync(
        IReadOnlyList<CardRuntimeInstance> runtimes,
        bool panelVisible,
        IReadOnlyList<string> visibleInstanceIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(visibleInstanceIds);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token);
        await _subscriptionGate.WaitAsync(linkedCancellation.Token)
            .ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            CardRuntimeInstance[] runtimeArray = runtimes.ToArray();
            SynchronizeRuntimes(runtimeArray);

            // The broker knows base instance IDs only - its providers publish for
            // "demo.weather", never for a duplicate's "#n" - so the wire carries base IDs
            // and the dispatcher fans each snapshot out to every duplicate. A duplicate
            // being visible therefore keeps its base's provider refreshing.
            string[] instanceIds = runtimeArray
                .Select(runtime => BuiltInCardCatalog.BaseInstanceId(runtime.InstanceId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            HashSet<string> instanceIdSet = instanceIds
                .ToHashSet(StringComparer.Ordinal);
            string[] visibleIds = visibleInstanceIds
                .Select(BuiltInCardCatalog.BaseInstanceId)
                .Where(instanceId => instanceIdSet.Contains(instanceId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            CardsSubscribeResponse response = await _cardsClient
                .SubscribeAsync(
                    new CardsSubscribeRequest
                    {
                        InstanceIds = instanceIds,
                        Visibility = new CardSubscriptionVisibility
                        {
                            PanelVisible = panelVisible,
                            VisibleInstanceIds = visibleIds,
                        },
                    },
                    linkedCancellation.Token)
                .ConfigureAwait(false);

            foreach (CardStateSnapshot snapshot in response.InitialSnapshots)
            {
                await _snapshotDispatcher
                    .DispatchAsync(snapshot, linkedCancellation.Token)
                    .ConfigureAwait(false);
            }

            lock (_runtimeGate)
            {
                _isSubscribed = true;
                _subscriptionId = response.SubscriptionId;
            }

            return response;
        }
        finally
        {
            _subscriptionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Cancel();
        _cardsClient.SnapshotReceived -= CardsClient_SnapshotReceived;

        try
        {
            await _subscriptionGate.WaitAsync().ConfigureAwait(false);
            _subscriptionGate.Release();
        }
        catch (ObjectDisposedException)
        {
        }

        CardRuntimeInstance[] runtimes;
        lock (_runtimeGate)
        {
            runtimes = _registeredRuntimes.Values.ToArray();
            _registeredRuntimes.Clear();
            _isSubscribed = false;
            _subscriptionId = null;
        }

        foreach (CardRuntimeInstance runtime in runtimes)
        {
            _snapshotDispatcher.Unregister(runtime.InstanceId, runtime);
        }

        _subscriptionGate.Dispose();
        _disposeCancellation.Dispose();
    }

    private void CardsClient_SnapshotReceived(
        object? sender,
        CardSnapshotReceivedEventArgs args)
    {
        _ = ApplySnapshotAsync(args.Snapshot);
    }

    private async Task ApplySnapshotAsync(CardStateSnapshot snapshot)
    {
        try
        {
            CardSnapshotDispatchResult result = await _snapshotDispatcher
                .DispatchAsync(snapshot, _disposeCancellation.Token)
                .ConfigureAwait(false);
            if (result is CardSnapshotDispatchResult.InvalidSnapshot or
                CardSnapshotDispatchResult.CardTypeMismatch or
                CardSnapshotDispatchResult.SchemaMismatch)
            {
                Debug.WriteLine(
                    $"WorkspacePanel rejected card snapshot for '{snapshot.InstanceId}': {result}.");
            }
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException &&
                exception is not StackOverflowException &&
                exception is not AccessViolationException)
        {
            Debug.WriteLine(
                $"WorkspacePanel card snapshot dispatch failed: {exception.Message}");
        }
    }
}
