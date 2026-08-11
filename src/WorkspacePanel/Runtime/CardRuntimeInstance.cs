using System.ComponentModel;

namespace WinWidgetBoard.WorkspacePanel.Runtime;

public sealed class CardRuntimeInstance :
    INotifyPropertyChanged,
    ICardLifecycle,
    IDisposable
{
    private readonly object _gate = new();
    private CardRuntimeSnapshot _snapshot;
    private CardLifecycleState _lifecycleState =
        CardLifecycleState.Created;

    public CardRuntimeInstance(
        ICardDefinition definition,
        string instanceId,
        CardRuntimeSnapshot initialSnapshot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        InstanceId = CardRuntimeContractGuards.RequireIdentifier(
            instanceId,
            nameof(instanceId));
        ValidateSnapshot(definition, InstanceId, initialSnapshot);
        Definition = definition;
        _snapshot = initialSnapshot;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<CardRuntimeSnapshotChangedEventArgs>?
        SnapshotChanged;

    public ICardDefinition Definition { get; }

    public string InstanceId { get; }

    public CardRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public CardLifecycleState LifecycleState
    {
        get
        {
            lock (_gate)
            {
                return _lifecycleState;
            }
        }
    }

    public bool Initialize() =>
        TransitionTo(CardLifecycleState.Initialized);

    public bool TransitionTo(CardLifecycleState targetState)
    {
        if (!Enum.IsDefined(targetState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetState),
                targetState,
                "Lifecycle target must be a defined state.");
        }

        if (targetState == CardLifecycleState.Disposed)
        {
            return DisposeCore();
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                _lifecycleState == CardLifecycleState.Disposed,
                this);
            if (_lifecycleState == targetState)
            {
                return false;
            }

            if (!IsValidTransition(_lifecycleState, targetState))
            {
                throw new InvalidOperationException(
                    $"Card lifecycle cannot transition from " +
                    $"'{_lifecycleState}' to '{targetState}'.");
            }

            _lifecycleState = targetState;
        }

        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LifecycleState)));
        return true;
    }

    public bool ApplySnapshot(CardRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateSnapshot(Definition, InstanceId, snapshot);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                _lifecycleState == CardLifecycleState.Disposed,
                this);
            if (snapshot.Sequence <= _snapshot.Sequence)
            {
                return false;
            }

            _snapshot = snapshot;
        }

        PublishSnapshot(snapshot);
        return true;
    }

    public async Task<bool> RefreshAsync(
        Func<CancellationToken, ValueTask<CardRuntimeSnapshot>>
            snapshotProvider,
        CancellationToken cancellationToken)
    {
        return await RefreshAsync(
            snapshotProvider,
            ApplySnapshot,
            cancellationToken);
    }

    public async Task<bool> RefreshAsync(
        Func<CancellationToken, ValueTask<CardRuntimeSnapshot>>
            snapshotProvider,
        Func<CardRuntimeSnapshot, bool> snapshotApplier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(snapshotApplier);
        ThrowIfDisposed();

        CardRuntimeSnapshot snapshot;
        try
        {
            snapshot = await snapshotProvider(
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (IsRecoverableCardFailure(exception))
        {
            snapshotApplier(CreateProviderFailureSnapshot());
            return false;
        }

        return snapshotApplier(snapshot);
    }

    public void Dispose()
    {
        DisposeCore();
    }

    private CardRuntimeSnapshot CreateProviderFailureSnapshot()
    {
        CardRuntimeSnapshot errorSnapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                _lifecycleState == CardLifecycleState.Disposed,
                this);
            errorSnapshot = CardRuntimeSnapshot.CreateError(
                _snapshot,
                CardRuntimeErrorCodes.SnapshotProviderFailed,
                DateTimeOffset.UtcNow);
        }

        return errorSnapshot;
    }

    private bool DisposeCore()
    {
        PropertyChangedEventHandler? propertyChanged;
        lock (_gate)
        {
            if (_lifecycleState == CardLifecycleState.Disposed)
            {
                return false;
            }

            _lifecycleState = CardLifecycleState.Disposed;
            propertyChanged = PropertyChanged;
            PropertyChanged = null;
            SnapshotChanged = null;
        }

        propertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LifecycleState)));
        return true;
    }

    private void PublishSnapshot(CardRuntimeSnapshot snapshot)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(Snapshot)));
        SnapshotChanged?.Invoke(
            this,
            new CardRuntimeSnapshotChangedEventArgs(snapshot));
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                _lifecycleState == CardLifecycleState.Disposed,
                this);
        }
    }

    private static void ValidateSnapshot(
        ICardDefinition definition,
        string instanceId,
        CardRuntimeSnapshot snapshot)
    {
        if (!string.Equals(
                instanceId,
                snapshot.InstanceId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Snapshot instance ID does not match the runtime instance.",
                nameof(snapshot));
        }

        if (!string.Equals(
                definition.CardTypeId,
                snapshot.CardTypeId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Snapshot card type does not match the runtime definition.",
                nameof(snapshot));
        }
    }

    private static bool IsValidTransition(
        CardLifecycleState currentState,
        CardLifecycleState targetState) =>
        currentState switch
        {
            CardLifecycleState.Created =>
                targetState == CardLifecycleState.Initialized,
            CardLifecycleState.Initialized =>
                targetState is CardLifecycleState.Visible
                    or CardLifecycleState.Hidden
                    or CardLifecycleState.Suspended,
            CardLifecycleState.Visible =>
                targetState is CardLifecycleState.Hidden
                    or CardLifecycleState.Suspended,
            CardLifecycleState.Hidden =>
                targetState is CardLifecycleState.Visible
                    or CardLifecycleState.Suspended,
            CardLifecycleState.Suspended =>
                targetState is CardLifecycleState.Visible
                    or CardLifecycleState.Hidden,
            _ => false,
        };

    private static bool IsRecoverableCardFailure(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
