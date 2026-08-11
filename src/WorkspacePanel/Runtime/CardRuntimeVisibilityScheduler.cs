namespace WinWidgetBoard.WorkspacePanel.Runtime;

/// <summary>
/// Coordinates panel/viewport visibility and fresh snapshot work for all
/// registered card runtimes. There is at most one active refresh plus one
/// cancellation-ignoring predecessor per registration; no per-card timer or
/// permanent loop is created.
/// </summary>
public sealed class CardRuntimeVisibilityScheduler : IAsyncDisposable, IDisposable
{
    private const int MaxRefreshesPerRegistration = 2;
    private static readonly TimeSpan DefaultDisposeTimeout =
        TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Dictionary<string, Registration> _registrations =
        new(StringComparer.Ordinal);
    private readonly TimeSpan _disposeTimeout;
    private bool _panelVisible;
    private bool _disposed;
    private bool _disposeStarted;

    public CardRuntimeVisibilityScheduler(
        bool panelVisible = false,
        TimeSpan? disposeTimeout = null)
    {
        TimeSpan timeout = disposeTimeout ?? DefaultDisposeTimeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposeTimeout),
                timeout,
                "Dispose timeout must be finite and positive.");
        }

        _panelVisible = panelVisible;
        _disposeTimeout = timeout;
    }

    public bool PanelVisible
    {
        get
        {
            lock (_gate)
            {
                return _panelVisible;
            }
        }
    }

    public int RegistrationCount
    {
        get
        {
            lock (_gate)
            {
                return _registrations.Count;
            }
        }
    }

    public CardRuntimeRegistration Register(
        CardRuntimeInstance runtime,
        Func<CancellationToken, ValueTask<CardRuntimeSnapshot>> snapshotProvider,
        bool isInViewport = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(snapshotProvider);

        var registration = new Registration(
            runtime,
            snapshotProvider,
            isInViewport);
        if (runtime.LifecycleState == CardLifecycleState.Disposed)
        {
            throw new ObjectDisposedException(
                runtime.GetType().Name,
                "A disposed runtime cannot be registered.");
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_registrations.TryAdd(runtime.InstanceId, registration))
            {
                throw new ArgumentException(
                    $"A runtime with instance ID '{runtime.InstanceId}' " +
                    "is already registered.",
                    nameof(runtime));
            }
        }

        ReconcileRegistration(registration);
        return new CardRuntimeRegistration(
            () => UnregisterRegistration(registration),
            runtime.InstanceId,
            runtime);
    }

    public bool SetPanelVisibility(bool panelVisible)
    {
        Registration[] registrations;
        bool changed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            changed = _panelVisible != panelVisible;
            _panelVisible = panelVisible;
            registrations = _registrations.Values.ToArray();
        }

        foreach (Registration registration in registrations)
        {
            ReconcileRegistration(registration);
        }

        return changed;
    }

    public bool SetViewportVisibility(
        CardRuntimeInstance runtime,
        bool isInViewport)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Registration? registration;
        bool changed;
        lock (_gate)
        {
            if (_disposed ||
                !_registrations.TryGetValue(
                    runtime.InstanceId,
                    out registration) ||
                !ReferenceEquals(registration.Runtime, runtime))
            {
                return false;
            }

            changed = registration.IsInViewport != isInViewport;
            if (changed)
            {
                registration.IsInViewport = isInViewport;
            }
        }

        ReconcileRegistration(registration);
        return changed;
    }

    public bool SetViewportVisibility(
        string instanceId,
        bool isInViewport)
    {
        CardRuntimeContractGuards.RequireIdentifier(
            instanceId,
            nameof(instanceId));
        Registration? registration;
        lock (_gate)
        {
            if (_disposed ||
                !_registrations.TryGetValue(instanceId, out registration))
            {
                return false;
            }
        }

        return SetViewportVisibility(registration.Runtime, isInViewport);
    }

    public bool ClearViewportVisibility()
    {
        Registration[] registrations;
        bool changed = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            registrations = _registrations.Values.ToArray();
            foreach (Registration registration in registrations)
            {
                if (registration.IsInViewport)
                {
                    registration.IsInViewport = false;
                    changed = true;
                }
            }
        }

        foreach (Registration registration in registrations)
        {
            ReconcileRegistration(registration);
        }

        return changed;
    }

    public bool Unregister(CardRuntimeInstance runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Registration? registration;
        lock (_gate)
        {
            if (_disposed ||
                !_registrations.TryGetValue(
                    runtime.InstanceId,
                    out registration) ||
                !ReferenceEquals(registration.Runtime, runtime))
            {
                return false;
            }

        }

        return UnregisterRegistration(registration);
    }

    private bool UnregisterRegistration(Registration registration)
    {
        RefreshExecution[] refreshes;
        lock (_gate)
        {
            if (_disposed ||
                !_registrations.TryGetValue(
                    registration.Runtime.InstanceId,
                    out Registration? current) ||
                !ReferenceEquals(current, registration))
            {
                return false;
            }

            _registrations.Remove(registration.Runtime.InstanceId);
            registration.IsRegistered = false;
            registration.Generation++;
            registration.RefreshPending = false;
            refreshes = registration.Refreshes.ToArray();
        }

        CancelRefreshes(refreshes);
        bool entered = Monitor.TryEnter(
            registration.CommitGate,
            _disposeTimeout);
        try
        {
            registration.Runtime.Dispose();
        }
        finally
        {
            if (entered)
            {
                Monitor.Exit(registration.CommitGate);
            }
        }

        return true;
    }

    public bool Unregister(string instanceId)
    {
        CardRuntimeContractGuards.RequireIdentifier(
            instanceId,
            nameof(instanceId));
        Registration? registration;
        lock (_gate)
        {
            if (_disposed ||
                !_registrations.TryGetValue(instanceId, out registration))
            {
                return false;
            }
        }

        return Unregister(registration.Runtime);
    }

    public async ValueTask DisposeAsync()
    {
        long disposeStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        Registration[] registrations;
        Dictionary<Registration, RefreshExecution[]> refreshesByRegistration;
        Task[] refreshTasks;
        lock (_gate)
        {
            if (_disposeStarted)
            {
                return;
            }

            _disposeStarted = true;
            _disposed = true;
            registrations = _registrations.Values.ToArray();
            _registrations.Clear();
            foreach (Registration registration in registrations)
            {
                registration.IsRegistered = false;
                registration.Generation++;
                registration.RefreshPending = false;
            }

            refreshesByRegistration = registrations.ToDictionary(
                registration => registration,
                registration => registration.Refreshes.ToArray());
            refreshTasks = refreshesByRegistration.Values
                .SelectMany(refreshes => refreshes)
                .Select(refresh => refresh.Task)
                .ToArray();
        }

        foreach (Registration registration in registrations)
        {
            CancelRefreshes(refreshesByRegistration[registration]);
            TimeSpan remainingTimeout = GetRemainingDisposeTimeout(
                disposeStartedTimestamp);
            bool entered = remainingTimeout > TimeSpan.Zero
                ? Monitor.TryEnter(
                    registration.CommitGate,
                    remainingTimeout)
                : Monitor.TryEnter(registration.CommitGate);
            try
            {
                // If a provider callback is still inside ApplySnapshot, the
                // generation/registration gate has already been invalidated;
                // disposing the runtime then fails closed for any late result.
                registration.Runtime.Dispose();
            }
            finally
            {
                if (entered)
                {
                    Monitor.Exit(registration.CommitGate);
                }
            }
        }

        if (refreshTasks.Length == 0)
        {
            return;
        }

        Task allRefreshes = Task.WhenAll(refreshTasks);
        TimeSpan remainingWait = GetRemainingDisposeTimeout(
            disposeStartedTimestamp);
        if (remainingWait <= TimeSpan.Zero)
        {
            ObserveLateFault(allRefreshes);
            return;
        }

        try
        {
            await allRefreshes.WaitAsync(remainingWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLateFault(allRefreshes);
        }
        catch (OperationCanceledException)
        {
            // A provider may expose a cancelled task after disposal. Its
            // result remains unusable because registration generation is dead.
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private void ReconcileRegistration(Registration registration)
    {
        try
        {
            TransitionRegistration(registration);
        }
        catch (ObjectDisposedException)
        {
            MarkRegistrationInactive(registration);
        }
        catch (InvalidOperationException)
        {
            // A runtime disposed or transitioned by an external owner is
            // isolated from the remaining cards in a batch signal.
            MarkRegistrationInactive(registration);
        }
    }

    private void TransitionRegistration(Registration registration)
    {
        lock (registration.CommitGate)
        {
            CardLifecycleState currentState =
                registration.Runtime.LifecycleState;
            if (currentState == CardLifecycleState.Created)
            {
                // Initialization is a local state transition and does not
                // perform provider work. It is intentionally outside the
                // scheduler dictionary lock.
                registration.Runtime.Initialize();
                currentState = CardLifecycleState.Initialized;
            }

            bool shouldBeVisible;
            bool stateChanged;
            lock (_gate)
            {
                if (_disposed ||
                    !registration.IsRegistered ||
                    !_registrations.TryGetValue(
                        registration.Runtime.InstanceId,
                        out Registration? current) ||
                    !ReferenceEquals(current, registration))
                {
                    return;
                }

                shouldBeVisible = _panelVisible &&
                    registration.IsInViewport;
                stateChanged = shouldBeVisible
                    ? currentState != CardLifecycleState.Visible
                    : currentState != CardLifecycleState.Hidden;
                if (stateChanged)
                {
                    registration.Generation++;
                    if (!shouldBeVisible)
                    {
                        registration.RefreshPending = false;
                    }
                }
            }

            if (stateChanged)
            {
                registration.Runtime.TransitionTo(
                    shouldBeVisible
                        ? CardLifecycleState.Visible
                        : CardLifecycleState.Hidden);
            }

            if (!shouldBeVisible)
            {
                CancelRegistrationRefreshes(registration);
                return;
            }

            if (stateChanged)
            {
                RequestRefresh(registration);
            }
        }
    }

    private void MarkRegistrationInactive(Registration registration)
    {
        RefreshExecution[] refreshes = [];
        lock (_gate)
        {
            if (_registrations.TryGetValue(
                    registration.Runtime.InstanceId,
                    out Registration? current) &&
                ReferenceEquals(current, registration))
            {
                _registrations.Remove(registration.Runtime.InstanceId);
                registration.IsRegistered = false;
                registration.Generation++;
                registration.RefreshPending = false;
                refreshes = registration.Refreshes.ToArray();
            }
        }

        CancelRefreshes(refreshes);
    }

    private void RequestRefresh(Registration registration)
    {
        if (registration.Runtime.LifecycleState != CardLifecycleState.Visible)
        {
            return;
        }

        RefreshExecution? refresh = null;
        lock (_gate)
        {
            if (_disposed ||
                !registration.IsRegistered ||
                !(_panelVisible && registration.IsInViewport))
            {
                return;
            }

            if (registration.Refreshes.Count >= MaxRefreshesPerRegistration)
            {
                registration.RefreshPending = true;
                return;
            }

            registration.RefreshPending = false;
            refresh = new RefreshExecution(
                registration,
                registration.Generation,
                new CancellationTokenSource());
            registration.Refreshes.Add(refresh);
        }

        // Provider/runtime work starts only after the scheduler dictionary lock
        // is released. This also keeps synchronous local providers from
        // re-entering the scheduler while its registry is locked.
        _ = RefreshAsync(refresh);
    }

    private async Task RefreshAsync(RefreshExecution refresh)
    {
        try
        {
            await refresh.Registration.Runtime.RefreshAsync(
                refresh.Registration.SnapshotProvider,
                snapshot => TryCommitSnapshot(refresh, snapshot),
                refresh.Cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (refresh.Cancellation.IsCancellationRequested)
        {
            // Caller cancellation is intentionally not converted to a card
            // error state.
        }
        catch (Exception)
        {
            // CardRuntimeInstance owns recoverable provider error snapshots.
            // A provider that throws a non-recoverable exception must not fault
            // the scheduler task or affect another registered card.
        }
        finally
        {
            try
            {
                CompleteRefresh(refresh);
            }
            finally
            {
                refresh.Complete();
            }
        }
    }

    private bool TryCommitSnapshot(
        RefreshExecution refresh,
        CardRuntimeSnapshot snapshot)
    {
        Registration registration = refresh.Registration;
        lock (registration.CommitGate)
        {
            bool runtimeVisible = registration.Runtime.LifecycleState ==
                CardLifecycleState.Visible;
            bool current;
            lock (_gate)
            {
                current = !_disposed &&
                    registration.IsRegistered &&
                    registration.Generation == refresh.Generation &&
                    _panelVisible &&
                    registration.IsInViewport &&
                    runtimeVisible &&
                    !refresh.Cancellation.IsCancellationRequested;
            }

            if (!current)
            {
                return false;
            }

            // The scheduler gate is released before the runtime is called.
            // The per-registration commit gate serializes this call with a
            // hide/unregister transition without executing external callbacks
            // while the scheduler dictionary lock is held.
            return registration.Runtime.ApplySnapshot(snapshot);
        }
    }

    private void CompleteRefresh(RefreshExecution refresh)
    {
        Registration registration = refresh.Registration;
        lock (registration.CommitGate)
        {
            bool runtimeVisible = registration.Runtime.LifecycleState ==
                CardLifecycleState.Visible;
            bool startPending = false;
            lock (_gate)
            {
                registration.Refreshes.Remove(refresh);
                bool hasCurrentGeneration = registration.Refreshes.Any(
                    active => active.Generation == registration.Generation);
                startPending = registration.RefreshPending &&
                    !_disposed &&
                    registration.IsRegistered &&
                    _panelVisible &&
                    registration.IsInViewport &&
                    runtimeVisible &&
                    !hasCurrentGeneration &&
                    registration.Refreshes.Count <
                        MaxRefreshesPerRegistration;
                if (startPending || hasCurrentGeneration)
                {
                    registration.RefreshPending = false;
                }
            }

            refresh.Cancellation.Dispose();
            if (startPending)
            {
                RequestRefresh(registration);
            }
        }
    }

    private static void CancelRegistrationRefreshes(Registration registration)
    {
        CancelRefreshes(registration.Refreshes.ToArray());
    }

    private static void CancelRefreshes(
        IEnumerable<RefreshExecution> refreshes)
    {
        foreach (RefreshExecution refresh in refreshes)
        {
            try
            {
                refresh.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion can dispose a token source between snapshots of
                // the bounded refresh list and cancellation.
            }
        }
    }

    private TimeSpan GetRemainingDisposeTimeout(long startedTimestamp)
    {
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(
            startedTimestamp);
        return elapsed >= _disposeTimeout
            ? TimeSpan.Zero
            : _disposeTimeout - elapsed;
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class Registration
    {
        public Registration(
            CardRuntimeInstance runtime,
            Func<CancellationToken, ValueTask<CardRuntimeSnapshot>>
                snapshotProvider,
            bool isInViewport)
        {
            Runtime = runtime;
            SnapshotProvider = snapshotProvider;
            IsInViewport = isInViewport;
        }

        public CardRuntimeInstance Runtime { get; }

        public Func<CancellationToken, ValueTask<CardRuntimeSnapshot>>
            SnapshotProvider { get; }

        public object CommitGate { get; } = new();

        public List<RefreshExecution> Refreshes { get; } = [];

        public bool IsRegistered { get; set; } = true;

        public bool IsInViewport { get; set; }

        public long Generation { get; set; }

        public bool RefreshPending { get; set; }
    }

    private sealed class RefreshExecution
    {
        public RefreshExecution(
            Registration registration,
            long generation,
            CancellationTokenSource cancellation)
        {
            Registration = registration;
            Generation = generation;
            Cancellation = cancellation;
        }

        public Registration Registration { get; }

        public long Generation { get; }

        public CancellationTokenSource Cancellation { get; }

        public Task Task => _completion.Task;

        public void Complete()
        {
            _completion.TrySetResult(null);
        }

        private readonly TaskCompletionSource<object?> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

}

public sealed class CardRuntimeRegistration : IDisposable
{
    private Action? _dispose;
    private readonly string _instanceId;
    private CardRuntimeInstance? _runtime;

    internal CardRuntimeRegistration(
        Action dispose,
        string instanceId,
        CardRuntimeInstance runtime)
    {
        _dispose = dispose;
        _instanceId = instanceId;
        _runtime = runtime;
    }

    public string InstanceId => _instanceId;

    public CardRuntimeInstance? Runtime => _runtime;

    public void Dispose()
    {
        Action? dispose = Interlocked.Exchange(
            ref _dispose,
            null);
        Interlocked.Exchange(ref _runtime, null);
        if (dispose is not null)
        {
            dispose();
        }
    }
}
