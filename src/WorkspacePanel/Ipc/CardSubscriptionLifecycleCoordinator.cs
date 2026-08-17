using Microsoft.UI.Dispatching;
using System.Diagnostics;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Ipc;

/// <summary>
/// Coordinates the WorkspacePanel lifetime of a cards subscription.
/// Transport and snapshot dispatch remain in CardSnapshotSubscriptionCoordinator;
/// this type owns initialization, refresh coalescing, reconnect recovery, and disposal.
/// </summary>
public sealed class CardSubscriptionLifecycleCoordinator : IAsyncDisposable
{
    private readonly CardSnapshotSubscriptionCoordinator _subscription;
    private readonly DispatcherQueue _uiDispatcherQueue;
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly Func<CancellationToken, Task<CardSubscriptionState>> _captureStateAsync;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private int _disposed;
    private int _initialized;

    public CardSubscriptionLifecycleCoordinator(
        CoreBrokerCardsClient cardsClient,
        CardSnapshotDispatcher snapshotDispatcher,
        DispatcherQueue uiDispatcherQueue,
        Func<CancellationToken, Task<CardSubscriptionState>> captureStateAsync)
    {
        ArgumentNullException.ThrowIfNull(cardsClient);
        ArgumentNullException.ThrowIfNull(snapshotDispatcher);
        _uiDispatcherQueue = uiDispatcherQueue ??
            throw new ArgumentNullException(nameof(uiDispatcherQueue));
        _captureStateAsync = captureStateAsync ??
            throw new ArgumentNullException(nameof(captureStateAsync));
        _subscription = new CardSnapshotSubscriptionCoordinator(
            cardsClient,
            snapshotDispatcher);
        _refreshTimer = _uiDispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(100);
        _refreshTimer.Tick += RefreshTimer_Tick;
    }

    public bool IsInitialized => Volatile.Read(ref _initialized) != 0;

    public void SynchronizeRuntimes(
        IReadOnlyList<CardRuntimeInstance> runtimes)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _subscription.SynchronizeRuntimes(runtimes);
    }

    public Task<bool> InitializeAsync(CancellationToken cancellationToken) =>
        RefreshAsync(markInitialized: true, cancellationToken);

    public void HandleBrokerReconnected(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsInitialized)
        {
            return;
        }

        if (_uiDispatcherQueue.HasThreadAccess)
        {
            RequestRefresh();
            return;
        }

        _uiDispatcherQueue.TryEnqueue(RequestRefresh);
    }

    public void RequestRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsInitialized)
        {
            return;
        }

        _refreshTimer.Start();
    }

    public void OnWindowClosed()
    {
        _refreshTimer.Stop();
        _disposeCancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        OnWindowClosed();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        await _subscription.DisposeAsync().ConfigureAwait(false);
        _disposeCancellation.Dispose();
    }

    private async Task<bool> RefreshAsync(
        bool markInitialized,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            CardSubscriptionState state = await _captureStateAsync(
                cancellationToken).ConfigureAwait(false);
            await _subscription
                .SubscribeAsync(
                    state.Runtimes,
                    state.PanelVisible,
                    state.VisibleInstanceIds,
                    cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _initialized, 1);
            return true;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            _disposeCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
            when (exception is CoreBrokerClientException or
                IOException or
                InvalidOperationException or
                TimeoutException)
        {
            Debug.WriteLine(
                $"WorkspacePanel card subscription failed: {exception.Message}");
            if (markInitialized)
            {
                Volatile.Write(ref _initialized, 0);
            }

            return false;
        }
    }

    private async void RefreshTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (Volatile.Read(ref _disposed) == 0)
        {
            await RefreshAsync(
                markInitialized: false,
                cancellationToken: _disposeCancellation.Token).ConfigureAwait(false);
        }
    }
}

public sealed record CardSubscriptionState(
    CardRuntimeInstance[] Runtimes,
    bool PanelVisible,
    string[] VisibleInstanceIds);
