using System.Text.Json;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Supplies both wall-clock metadata and a deterministic monotonic timeline to
/// the scheduled-provider core. The explicit delay seam keeps timeout and
/// disposal tests independent from the machine clock and from thread sleeps.
/// </summary>
public interface IProviderRefreshClock
{
    DateTimeOffset UtcNow { get; }

    TimeSpan MonotonicNow { get; }

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Production clock adapter. TimeProvider supplies the monotonic timestamp and
/// UTC value; its timer factory is used instead of an unqualified Task.Delay.
/// </summary>
public sealed class TimeProviderRefreshClock : IProviderRefreshClock
{
    private readonly TimeProvider _timeProvider;
    private readonly long _originTimestamp;

    public TimeProviderRefreshClock(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _originTimestamp = _timeProvider.GetTimestamp();
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public TimeSpan MonotonicNow =>
        _timeProvider.GetElapsedTime(_originTimestamp);

    public ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(DelayCoreAsync(delay, cancellationToken));
    }

    private async Task DelayCoreAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using ITimer timer = _timeProvider.CreateTimer(
            static state =>
            {
                ((TaskCompletionSource<object?>)state!).TrySetResult(null);
            },
            completion,
            delay,
            Timeout.InfiniteTimeSpan);
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                ((TaskCompletionSource<object?>)state!).TrySetCanceled();
            },
            completion);

        await completion.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// One immutable consumer registration. The source descriptor is checked at
/// construction so a key can never be paired with a different provider or
/// capability. Json arguments are detached from the caller's document.
/// </summary>
public sealed class ProviderRefreshSubscription
{
    public ProviderRefreshSubscription(
        Guid subscriptionId,
        ProviderRequestKey key,
        JsonElement arguments,
        IProviderRefreshSource source,
        IProviderRefreshSink sink,
        ProviderRefreshVisibility initialVisibility)
    {
        if (subscriptionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Subscription ID must not be empty.",
                nameof(subscriptionId));
        }

        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ScheduledProviderDescriptor descriptor = source.Descriptor;
        if (!string.Equals(
                key.ProviderId,
                descriptor.ProviderId,
                StringComparison.Ordinal) ||
            !string.Equals(
                key.Capability,
                descriptor.Capability,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Provider request key does not match the source descriptor.",
                nameof(key));
        }

        SubscriptionId = subscriptionId;
        Key = key;
        Arguments = CloneJson(arguments);
        Source = source;
        Descriptor = descriptor;
        Sink = sink;
        InitialVisibility = initialVisibility;
    }

    public Guid SubscriptionId { get; }

    public ProviderRequestKey Key { get; }

    public JsonElement Arguments { get; }

    public IProviderRefreshSource Source { get; }

    public ScheduledProviderDescriptor Descriptor { get; }

    public IProviderRefreshSink Sink { get; }

    public ProviderRefreshVisibility InitialVisibility { get; }

    internal static JsonElement CloneJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            using JsonDocument document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

        return value.Clone();
    }
}

/// <summary>
/// A one-shot handle for a scheduler registration. Disposal is idempotent and
/// never owns the source or sink supplied by the caller.
/// </summary>
public sealed class ProviderRefreshRegistration : IDisposable
{
    private readonly Guid _subscriptionId;
    private readonly object _registrationIdentity;
    private ProviderRefreshScheduler? _scheduler;

    internal ProviderRefreshRegistration(
        ProviderRefreshScheduler scheduler,
        Guid subscriptionId,
        object registrationIdentity)
    {
        _scheduler = scheduler;
        _subscriptionId = subscriptionId;
        _registrationIdentity = registrationIdentity;
    }

    public Guid SubscriptionId => _subscriptionId;

    public void Dispose()
    {
        ProviderRefreshScheduler? scheduler =
            Interlocked.Exchange(ref _scheduler, null);
        scheduler?.Unregister(_subscriptionId, _registrationIdentity);
    }
}

/// <summary>
/// Small immutable diagnostic view of one source group. Results are cloned on
/// publication, so no scheduler-owned collection or JsonDocument is exposed.
/// </summary>
public sealed class ProviderRefreshSchedulerStateSnapshot
{
    internal ProviderRefreshSchedulerStateSnapshot(
        ProviderRequestKey key,
        int groupCount,
        int subscriptionCount,
        int failureCount,
        int inFlightCount,
        int pendingCount,
        ProviderRefreshResult? lastSuccessfulResult,
        TimeSpan? nextDueIn)
    {
        Key = key;
        GroupCount = groupCount;
        SubscriptionCount = subscriptionCount;
        FailureCount = failureCount;
        InFlightCount = inFlightCount;
        PendingCount = pendingCount;
        LastSuccessfulResult = lastSuccessfulResult is null
            ? null
            : CloneResult(lastSuccessfulResult);
        NextDueIn = nextDueIn;
    }

    public ProviderRequestKey Key { get; }

    public int GroupCount { get; }

    public int SubscriptionCount { get; }

    public int FailureCount { get; }

    public int InFlightCount { get; }

    public int PendingCount { get; }

    public ProviderRefreshResult? LastSuccessfulResult { get; }

    public TimeSpan? NextDueIn { get; }

    private static ProviderRefreshResult CloneResult(ProviderRefreshResult result) =>
        new(
            result.RequestId,
            result.Kind,
            result.Payload,
            result.ProducedAtUtc,
            result.ValidUntilUtc,
            result.ErrorCode,
            result.RetryAfter);
}

/// <summary>
/// Central, explicit-pump scheduler for scheduled providers. There is one
/// group per ProviderRequestKey and at most one active request plus one
/// cancellation-ignoring predecessor for each group.
/// </summary>
public sealed class ProviderRefreshScheduler : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan DefaultDisposeTimeout =
        TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Dictionary<ProviderRequestKey, Group> _groups = [];
    private readonly Dictionary<Guid, Consumer> _consumers = [];
    private readonly IProviderRefreshClock _clock;
    private readonly Func<double> _jitter;
    private readonly TimeSpan _disposeTimeout;
    private bool _disposed;
    private Task? _disposeTask;
    private ProviderNetworkState _networkState = ProviderNetworkState.Unknown;
    private ProviderPowerState _powerState = ProviderPowerState.Unknown;

    public ProviderRefreshScheduler(
        IProviderRefreshClock? clock = null,
        Func<double>? jitter = null,
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

        _clock = clock ?? new TimeProviderRefreshClock();
        _jitter = jitter ?? (static () => 0.5);
        _disposeTimeout = timeout;
    }

    public int GroupCount
    {
        get
        {
            lock (_gate)
            {
                return _groups.Count;
            }
        }
    }

    public int SubscriptionCount
    {
        get
        {
            lock (_gate)
            {
                return _consumers.Count;
            }
        }
    }

    public ProviderRefreshRegistration Register(
        ProviderRefreshSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        TimeSpan now = _clock.MonotonicNow;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_consumers.ContainsKey(subscription.SubscriptionId))
            {
                throw new ArgumentException(
                    "A subscription with this ID is already registered.",
                    nameof(subscription));
            }

            Group group;
            if (_groups.TryGetValue(subscription.Key, out Group? existing))
            {
                if (!ReferenceEquals(existing.Source, subscription.Source) ||
                    !Equals(existing.Descriptor, subscription.Descriptor) ||
                    !JsonElementsEqual(existing.Arguments, subscription.Arguments))
                {
                    throw new ArgumentException(
                        "The same request key must use the same source, descriptor and arguments.",
                        nameof(subscription));
                }

                group = existing;
            }
            else
            {
                group = new Group(
                    subscription.Key,
                    subscription.Source,
                    subscription.Descriptor,
                    subscription.Arguments,
                    now);
                _groups.Add(subscription.Key, group);
            }

            var consumer = new Consumer(subscription)
            {
                Group = group
            };
            group.Consumers.Add(subscription.SubscriptionId, consumer);
            _consumers.Add(subscription.SubscriptionId, consumer);
            if (IsConsumerEligibleLocked(group, consumer))
            {
                bool wasEligible = group.HadEligibleConsumer;
                group.HadEligibleConsumer = true;
                if (group.Active is not null || group.Predecessor is not null)
                {
                    // The execution's consumer-generation snapshot predates
                    // this registration.  Queue one bounded follow-up so the
                    // new consumer receives a fresh result without joining
                    // the in-flight fan-out.
                    group.Pending = true;
                }
                else if (!wasEligible)
                {
                    group.NextDue = now;
                }
            }

            return new ProviderRefreshRegistration(
                this,
                subscription.SubscriptionId,
                consumer);
        }
    }

    public bool Unregister(Guid subscriptionId)
    {
        return UnregisterCore(subscriptionId, registrationIdentity: null);
    }

    internal bool Unregister(
        Guid subscriptionId,
        object registrationIdentity)
    {
        return UnregisterCore(subscriptionId, registrationIdentity);
    }

    private bool UnregisterCore(
        Guid subscriptionId,
        object? registrationIdentity)
    {
        TimeSpan now = _clock.MonotonicNow;
        List<Execution> cancellations = [];
        lock (_gate)
        {
            if (_disposed ||
                !_consumers.TryGetValue(subscriptionId, out Consumer? consumer) ||
                (registrationIdentity is not null &&
                    !ReferenceEquals(consumer, registrationIdentity)))
            {
                return false;
            }

            _consumers.Remove(subscriptionId);

            consumer.Registered = false;
            consumer.Generation++;
            Group group = consumer.Group!;
            group.Consumers.Remove(subscriptionId);
            if (group.Consumers.Count == 0)
            {
                group.Generation++;
                group.Pending = false;
                CollectCancellationLocked(group.Active, cancellations);
                CollectCancellationLocked(group.Predecessor, cancellations);
                RemoveIdleTombstoneLocked(group);
            }
            else
            {
                ReevaluateGroupLocked(group, cancellations, false, now);
            }
        }

        CancelExecutions(cancellations);
        return true;
    }

    public bool SetVisibility(
        Guid subscriptionId,
        ProviderRefreshVisibility visibility)
    {
        TimeSpan now = _clock.MonotonicNow;
        List<Execution> cancellations = [];
        bool changed;
        lock (_gate)
        {
            if (_disposed || !_consumers.TryGetValue(subscriptionId, out Consumer? consumer))
            {
                return false;
            }

            changed = consumer.Visibility != visibility;
            if (!changed)
            {
                return false;
            }

            bool wasVisible = consumer.Visibility.IsVisible;
            consumer.Visibility = visibility;
            consumer.Generation++;
            Group group = consumer.Group!;
            bool nowVisible = visibility.IsVisible;
            ReevaluateGroupLocked(group, cancellations, false, now);

            if (nowVisible && !wasVisible &&
                group.FailureCount == 0 &&
                IsEnvironmentEligibleLocked(group) &&
                GetCadenceLocked(group) is not null)
            {
                group.NextDue = now;
            }
            else if (!nowVisible && wasVisible &&
                     group.FailureCount == 0 &&
                     GetCadenceLocked(group) is { } hiddenCadence)
            {
                TimeSpan currentDue = group.NextDue ?? now;
                group.NextDue = Max(
                    currentDue,
                    Add(now, hiddenCadence));
            }
        }

        CancelExecutions(cancellations);
        return changed;
    }

    public bool SetNetworkState(ProviderNetworkState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown network state.");
        }

        TimeSpan now = _clock.MonotonicNow;
        List<Execution> cancellations = [];
        bool changed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            changed = _networkState != state;
            if (!changed)
            {
                return false;
            }

            bool wasRunnable = IsNetworkRunnable(_networkState);
            _networkState = state;
            bool isRunnable = IsNetworkRunnable(state);
            foreach (Group group in _groups.Values.ToArray())
            {
                ReevaluateGroupLocked(
                    group,
                    cancellations,
                    forceImmediate: !wasRunnable && isRunnable,
                    now: now);
            }
        }

        CancelExecutions(cancellations);
        return changed;
    }

    public bool SetPowerState(ProviderPowerState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown power state.");
        }

        TimeSpan now = _clock.MonotonicNow;
        List<Execution> cancellations = [];
        bool changed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            changed = _powerState != state;
            if (!changed)
            {
                return false;
            }

            bool wasRunnable = IsPowerRunnable(_powerState);
            _powerState = state;
            bool isRunnable = IsPowerRunnable(state);
            foreach (Group group in _groups.Values.ToArray())
            {
                ReevaluateGroupLocked(
                    group,
                    cancellations,
                    forceImmediate: !wasRunnable && isRunnable,
                    now: now);
                if (state == ProviderPowerState.BatterySaver &&
                    group.FailureCount == 0 &&
                    GetCadenceLocked(group) is { } powerCadence)
                {
                    TimeSpan currentDue = group.NextDue ?? now;
                    group.NextDue = Max(
                        currentDue,
                        Add(now, powerCadence));
                }
            }
        }

        CancelExecutions(cancellations);
        return changed;
    }

    public ValueTask<int> PumpDueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan now = _clock.MonotonicNow;
        DateTimeOffset utcNow = _clock.UtcNow;
        List<Execution> starts = [];
        List<Execution> cancellations = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (Group group in _groups.Values.ToArray())
            {
                ReevaluateGroupLocked(group, cancellations, false, now);
                if (GetCadenceLocked(group) is null ||
                    group.NextDue is not { } nextDue ||
                    nextDue > now)
                {
                    continue;
                }

                if (group.Active is not null)
                {
                    group.Pending = true;
                    continue;
                }

                Execution execution = CreateExecutionLocked(group, now, utcNow);
                starts.Add(execution);
            }
        }

        CancelExecutions(cancellations);
        foreach (Execution execution in starts)
        {
            StartExecution(execution);
        }

        return ValueTask.FromResult(starts.Count);
    }

    public async ValueTask<ProviderManualRefreshOutcome> RequestManualRefreshAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan now = _clock.MonotonicNow;
        DateTimeOffset utcNow = _clock.UtcNow;
        Execution? execution = null;
        List<Execution> cancellations = [];
        ProviderManualRefreshOutcome outcome;
        lock (_gate)
        {
            if (_disposed)
            {
                return ProviderManualRefreshOutcome.Disposed;
            }

            if (!_consumers.TryGetValue(subscriptionId, out Consumer? consumer))
            {
                return ProviderManualRefreshOutcome.NotRegistered;
            }

            Group group = consumer.Group!;
            ScheduledProviderDescriptor descriptor = group.Descriptor;
            if (!descriptor.SupportsManualRefresh)
            {
                return ProviderManualRefreshOutcome.Paused;
            }

            if (!consumer.Visibility.IsVisible)
            {
                return ProviderManualRefreshOutcome.NotVisible;
            }

            if (!IsEnvironmentEligibleLocked(group))
            {
                outcome = descriptor.RequiresNetwork &&
                    !IsNetworkRunnable(_networkState)
                    ? ProviderManualRefreshOutcome.Offline
                    : ProviderManualRefreshOutcome.Paused;
                return outcome;
            }

            if (group.Active is not null)
            {
                return ProviderManualRefreshOutcome.Coalesced;
            }

            if (group.LastManualAt is { } lastManual &&
                now - lastManual < descriptor.ManualRefreshMinimumInterval)
            {
                return ProviderManualRefreshOutcome.RateLimited;
            }

            if (group.LastFailureKind == ProviderRefreshResultKind.RateLimited &&
                group.NextDue is { } rateLimitedDue && rateLimitedDue > now)
            {
                return ProviderManualRefreshOutcome.RateLimited;
            }

            if (group.FailureCount > 0 &&
                group.NextDue is { } backoffDue &&
                backoffDue > now &&
                !group.ManualBypassAvailable)
            {
                return ProviderManualRefreshOutcome.RateLimited;
            }

            if (group.FailureCount > 0 && group.NextDue is { } due && due > now)
            {
                group.ManualBypassAvailable = false;
            }

            group.LastManualAt = now;
            group.Pending = false;
            ReevaluateGroupLocked(group, cancellations, false, now);
            execution = CreateExecutionLocked(group, now, utcNow);
            outcome = ProviderManualRefreshOutcome.Started;
        }

        CancelExecutions(cancellations);
        StartExecution(execution!);
        await Task.CompletedTask.ConfigureAwait(false);
        return outcome;
    }

    public ProviderRefreshSchedulerStateSnapshot GetState(
        ProviderRequestKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        TimeSpan now = _clock.MonotonicNow;
        lock (_gate)
        {
            if (!_groups.TryGetValue(key, out Group? group))
            {
                throw new KeyNotFoundException(
                    "The provider request key is not registered.");
            }

            return CreateStateSnapshotLocked(group, now);
        }
    }

    public bool TryGetState(
        ProviderRequestKey key,
        out ProviderRefreshSchedulerStateSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(key);
        TimeSpan now = _clock.MonotonicNow;
        lock (_gate)
        {
            if (!_groups.TryGetValue(key, out Group? group))
            {
                snapshot = null;
                return false;
            }

            snapshot = CreateStateSnapshotLocked(group, now);
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        List<Execution> executions = [];
        Task[] executionTasks;
        TaskCompletionSource<object?>? completionToStart = null;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            foreach (Group group in _groups.Values)
            {
                group.Generation++;
                group.Pending = false;
                CollectCancellationLocked(group.Active, executions);
                CollectCancellationLocked(group.Predecessor, executions);
                group.Active = null;
                group.Predecessor = null;
            }

            _groups.Clear();
            _consumers.Clear();
            executionTasks = executions
                .SelectMany(execution => new[]
                {
                    execution.Completion.Task,
                    execution.ProviderCompletion.Task
                })
                .Distinct()
                .ToArray();

            completionToStart = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completionToStart.Task;
        }

        _ = RunDisposeAsync(
            executions,
            executionTasks,
            completionToStart!);
        return new ValueTask(completionToStart!.Task);
    }

    private async Task RunDisposeAsync(
        IReadOnlyList<Execution> executions,
        Task[] executionTasks,
        TaskCompletionSource<object?> completion)
    {
        try
        {
            CancelExecutions(executions);
            if (executionTasks.Length == 0)
            {
                return;
            }

            Task all = Task.WhenAll(executionTasks);
            using var timeoutCancellation = new CancellationTokenSource();
            Task timeout = _clock.DelayAsync(
                _disposeTimeout,
                timeoutCancellation.Token).AsTask();
            Task completed = await Task.WhenAny(all, timeout).ConfigureAwait(false);
            if (ReferenceEquals(completed, all))
            {
                SafeCancel(timeoutCancellation);
                try
                {
                    await all.ConfigureAwait(false);
                }
                catch
                {
                    ObserveLateFault(all);
                }

                return;
            }

            ObserveLateFault(all);
        }
        finally
        {
            completion.TrySetResult(null);
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async Task ExecuteAsync(Execution execution)
    {
        ProviderRefreshResult? result = null;
        try
        {
            ProviderRefreshRequest request = execution.Request;
            using var timeoutCancellation = new CancellationTokenSource();
            using CancellationTokenRegistration cancellationRegistration =
                execution.Cancellation.Token.Register(
                    static state =>
                    {
                        ((TaskCompletionSource<object?>)state!).TrySetResult(null);
                    },
                    execution.CancellationSignal);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                execution.Cancellation.Token,
                timeoutCancellation.Token);
            Task<ProviderRefreshResult> sourceTask;
            if (!execution.TryClaimSourceStart())
            {
                execution.ProviderCompletion.TrySetResult(null);
                return;
            }

            if (execution.Cancellation.IsCancellationRequested)
            {
                execution.ProviderCompletion.TrySetResult(null);
                return;
            }

            try
            {
                sourceTask = execution.Group.Source.FetchAsync(
                    request,
                    linkedCancellation.Token).AsTask();
            }
            catch (Exception exception)
                when (!IsProcessFatal(exception))
            {
                sourceTask = Task.FromException<ProviderRefreshResult>(exception);
            }

            execution.ProviderTask = sourceTask;
            _ = sourceTask.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    execution.ProviderCompletion.TrySetResult(null);
                    ProviderTaskFinished(execution);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            Task timeoutTask = _clock.DelayAsync(
                execution.Group.Descriptor.RequestTimeout,
                linkedCancellation.Token).AsTask();
            Task completed = await Task.WhenAny(
                sourceTask,
                timeoutTask,
                execution.CancellationSignal.Task).ConfigureAwait(false);

            if (ReferenceEquals(completed, execution.CancellationSignal.Task) ||
                execution.Cancellation.IsCancellationRequested)
            {
                return;
            }

            if (ReferenceEquals(completed, timeoutTask))
            {
                if (execution.Cancellation.IsCancellationRequested)
                {
                    return;
                }

                SafeCancel(timeoutCancellation);
                result = new ProviderRefreshResult(
                    request.RequestId,
                    ProviderRefreshResultKind.TimedOut,
                    EmptyPayload(),
                    _clock.UtcNow,
                    errorCode: "provider.timeout");
            }
            else
            {
                SafeCancel(timeoutCancellation);
                try
                {
                    ProviderRefreshResult? sourceResult = await sourceTask
                        .ConfigureAwait(false);
                    result = NormalizeResult(request, sourceResult);
                }
                catch (OperationCanceledException)
                    when (execution.Cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                    when (!IsProcessFatal(exception))
                {
                    result = new ProviderRefreshResult(
                        request.RequestId,
                        ProviderRefreshResultKind.Failed,
                        EmptyPayload(),
                        _clock.UtcNow,
                        errorCode: "provider.failed");
                }
            }

            if (result is not null)
            {
                await CommitResultAsync(execution, request, result)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (execution.Cancellation.IsCancellationRequested)
        {
            // Cancellation caused by visibility, unregister or disposal is not
            // a provider result and must not increment failure state.
        }
        catch (Exception exception)
            when (!IsProcessFatal(exception))
        {
            // Source and sink faults are isolated. The source path above
            // normalizes provider exceptions; this final guard protects the
            // scheduler task itself from an unexpected callback failure.
        }
        finally
        {
            CompleteExecution(execution);
            execution.Completion.TrySetResult(null);
        }
    }

    private async Task CommitResultAsync(
        Execution execution,
        ProviderRefreshRequest request,
        ProviderRefreshResult result)
    {
        TimeSpan now = _clock.MonotonicNow;
        bool success = result.Kind is
            ProviderRefreshResultKind.Success or
            ProviderRefreshResultKind.Empty;
        double jitter = success ? 0 : NormalizeJitter(_jitter);
        ConsumerTarget[] targets;
        List<Execution> cancellations = [];
        lock (_gate)
        {
            if (_disposed ||
                !_groups.TryGetValue(execution.Group.Key, out Group? group) ||
                !ReferenceEquals(group, execution.Group) ||
                group.Generation != execution.Generation ||
                execution.Cancellation.IsCancellationRequested)
            {
                return;
            }

            if (result.Kind is not ProviderRefreshResultKind.Success and
                not ProviderRefreshResultKind.Empty &&
                group.LastSuccessfulResult is { } lastSuccessful)
            {
                result = new ProviderRefreshResult(
                    result.RequestId,
                    result.Kind,
                    lastSuccessful.Payload,
                    result.ProducedAtUtc,
                    result.ValidUntilUtc,
                    result.ErrorCode,
                    result.RetryAfter);
            }

            ApplyResultStateLocked(group, result, now, jitter);
            targets = group.Consumers.Values
                .Where(consumer =>
                    consumer.Registered &&
                    execution.ConsumerSnapshots.TryGetValue(
                        consumer.Subscription.SubscriptionId,
                        out ConsumerSnapshot snapshot) &&
                    ReferenceEquals(snapshot.Consumer, consumer) &&
                    snapshot.Generation == consumer.Generation &&
                    IsConsumerEligibleLocked(group, consumer))
                .Select(consumer => new ConsumerTarget(
                    consumer,
                    consumer.Subscription.Sink))
                .ToArray();
            ReevaluateGroupLocked(group, cancellations, false, now);
        }

        CancelExecutions(cancellations);
        foreach (ConsumerTarget target in targets)
        {
            if (!CanDeliver(target.Consumer, execution))
            {
                continue;
            }

            try
            {
                await target.Sink.ApplyAsync(
                    request,
                    result,
                    execution.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (execution.Cancellation.IsCancellationRequested)
            {
                // Consumer cancellation is not a provider failure.
            }
            catch (Exception exception)
                when (!IsProcessFatal(exception))
            {
                // One sink is not allowed to block fan-out to other consumers.
            }
        }
    }

    private void CompleteExecution(Execution execution)
    {
        TimeSpan now = _clock.MonotonicNow;
        List<Execution> cancellations = [];
        lock (_gate)
        {
            Group group = execution.Group;
            execution.SchedulerCompleted = true;
            bool providerStillRunning = execution.ProviderTask is
                { IsCompleted: false };
            if (ReferenceEquals(group.Active, execution))
            {
                if (providerStillRunning && group.Predecessor is null)
                {
                    group.Predecessor = execution;
                    group.Active = null;
                }
                else if (!providerStillRunning)
                {
                    group.Active = null;
                }
            }

            if (!providerStillRunning &&
                ReferenceEquals(group.Predecessor, execution))
            {
                group.Predecessor = null;
            }

            RemoveIdleTombstoneLocked(group);

            if (_disposed ||
                !_groups.TryGetValue(group.Key, out Group? current) ||
                !ReferenceEquals(current, group))
            {
                return;
            }

            if (group.Pending && group.Active is null)
            {
                group.Pending = false;
                if (GetCadenceLocked(group) is null)
                {
                    ReevaluateGroupLocked(group, cancellations, false, now);
                }
                else
                {
                    group.NextDue = now;
                }
            }
        }

        CancelExecutions(cancellations);
    }

    private void ProviderTaskFinished(Execution execution)
    {
        TimeSpan now = _clock.MonotonicNow;
        List<Execution> cancellations = [];
        lock (_gate)
        {
            Group group = execution.Group;
            bool removed = false;
            if (ReferenceEquals(group.Predecessor, execution))
            {
                group.Predecessor = null;
                removed = true;
            }

            if (execution.SchedulerCompleted &&
                ReferenceEquals(group.Active, execution))
            {
                group.Active = null;
                removed = true;
            }

            RemoveIdleTombstoneLocked(group);

            if (!removed ||
                _disposed ||
                !_groups.TryGetValue(group.Key, out Group? current) ||
                !ReferenceEquals(current, group) ||
                group.Pending ||
                group.Active is not null ||
                group.Predecessor is not null)
            {
                return;
            }

            if (GetCadenceLocked(group) is null)
            {
                ReevaluateGroupLocked(group, cancellations, false, now);
            }
            else if (group.NextDue is not { } due ||
                     due <= now)
            {
                group.NextDue = now;
            }
        }

        CancelExecutions(cancellations);
    }

    private void RemoveIdleTombstoneLocked(Group group)
    {
        if (group.Consumers.Count == 0 &&
            group.Active is null &&
            group.Predecessor is null &&
            _groups.TryGetValue(group.Key, out Group? current) &&
            ReferenceEquals(current, group))
        {
            _groups.Remove(group.Key);
        }
    }

    private static Execution CreateExecutionLocked(
        Group group,
        TimeSpan now,
        DateTimeOffset utcNow)
    {
        group.Generation++;
        var request = new ProviderRefreshRequest(
            Guid.NewGuid(),
            group.Key,
            utcNow.Add(group.Descriptor.RequestTimeout),
            group.Arguments,
            group.Generation);
        var execution = new Execution(
            group,
            request,
            group.Generation,
            group.Consumers.Values.ToDictionary(
                consumer => consumer.Subscription.SubscriptionId,
                consumer => new ConsumerSnapshot(
                    consumer,
                    consumer.Generation)));
        group.Active = execution;
        group.Pending = false;
        group.NextDue = null;
        return execution;
    }

    private void ReevaluateGroupLocked(
        Group group,
        List<Execution> cancellations,
        bool forceImmediate,
        TimeSpan now)
    {
        TimeSpan? cadence = GetCadenceLocked(group);
        if (cadence is null)
        {
            group.Pending = false;
            if (group.Active is not null)
            {
                group.Generation++;
                MoveOrCancelActiveLocked(group, cancellations);
            }

            group.HadEligibleConsumer = false;
            if (group.FailureCount == 0)
            {
                group.NextDue = null;
            }
            return;
        }

        bool newlyEligible = !group.HadEligibleConsumer;
        group.HadEligibleConsumer = true;
        if (forceImmediate || newlyEligible || group.NextDue is null)
        {
            if (group.FailureCount == 0 ||
                group.NextDue is null ||
                group.NextDue <= now)
            {
                group.NextDue = now;
            }
        }
    }

    private static void MoveOrCancelActiveLocked(
        Group group,
        List<Execution> cancellations)
    {
        Execution? active = group.Active;
        if (active is null)
        {
            return;
        }

        if (group.Predecessor is null)
        {
            group.Predecessor = active;
            group.Active = null;
        }

        CollectCancellationLocked(active, cancellations);
    }

    private void ApplyResultStateLocked(
        Group group,
        ProviderRefreshResult result,
        TimeSpan now,
        double jitter)
    {
        bool success = result.Kind is
            ProviderRefreshResultKind.Success or
            ProviderRefreshResultKind.Empty;
        if (success)
        {
            group.FailureCount = 0;
            group.LastFailureKind = null;
            group.LastRetryAfter = null;
            group.ManualBypassAvailable = false;
            group.LastSuccessfulResult = CloneResult(result);
            group.NextDue = Add(
                now,
                GetCadenceLocked(group) ?? group.Descriptor.MinimumInterval);
            return;
        }

        group.FailureCount = checked(group.FailureCount + 1);
        group.LastFailureKind = result.Kind;
        group.LastRetryAfter = result.RetryAfter;
        group.ManualBypassAvailable =
            result.Kind != ProviderRefreshResultKind.RateLimited &&
            result.RetryAfter is null;
        TimeSpan delay = ProviderBackoffPolicy.CalculateDelay(
            group.Descriptor.BackoffOptions,
            group.FailureCount,
            jitter,
            result.RetryAfter);
        if (delay < group.Descriptor.MinimumInterval)
        {
            delay = group.Descriptor.MinimumInterval;
        }

        group.NextDue = Add(now, delay);
    }

    private ProviderRefreshSchedulerStateSnapshot CreateStateSnapshotLocked(
        Group group,
        TimeSpan now)
    {
        int inFlight = (group.Active is null ? 0 : 1) +
            (group.Predecessor is null ? 0 : 1);
        return new ProviderRefreshSchedulerStateSnapshot(
            group.Key,
            _groups.Count,
            _consumers.Count,
            group.FailureCount,
            inFlight,
            group.Pending ? 1 : 0,
            group.LastSuccessfulResult,
            group.NextDue is { } nextDue
                ? Max(TimeSpan.Zero, nextDue - now)
                : null);
    }

    private TimeSpan? GetCadenceLocked(Group group)
    {
        if (!IsEnvironmentEligibleLocked(group))
        {
            return null;
        }

        if (group.Consumers.Values.All(
                consumer => !consumer.Registered ||
                    !consumer.Visibility.DisplayConnected))
        {
            return null;
        }

        return _powerState switch
        {
            ProviderPowerState.Normal =>
                group.Consumers.Values.Any(
                    consumer => IsConsumerVisible(consumer))
                    ? group.Descriptor.VisibleInterval
                    : group.Descriptor.HiddenInterval,
            ProviderPowerState.BatterySaver =>
                group.Descriptor.PowerSaverInterval,
            _ => null,
        };
    }

    private bool IsEnvironmentEligibleLocked(Group group) =>
        (_powerState is ProviderPowerState.Normal or ProviderPowerState.BatterySaver) &&
        (!group.Descriptor.RequiresNetwork ||
            IsNetworkRunnable(_networkState));

    private bool IsConsumerEligibleLocked(Group group, Consumer consumer) =>
        consumer.Registered &&
        consumer.Visibility.DisplayConnected &&
        GetCadenceLocked(group) is not null;

    private static bool IsConsumerVisible(Consumer consumer) =>
        consumer.Registered && consumer.Visibility.IsVisible;

    private static bool IsNetworkRunnable(ProviderNetworkState state) =>
        state is ProviderNetworkState.Online or ProviderNetworkState.Constrained;

    private static bool IsPowerRunnable(ProviderPowerState state) =>
        state is ProviderPowerState.Normal or ProviderPowerState.BatterySaver;

    private static void CollectCancellationLocked(
        Execution? execution,
        List<Execution> cancellations)
    {
        if (execution is not null && !execution.Cancellation.IsCancellationRequested)
        {
            cancellations.Add(execution);
        }
    }

    private static void CancelExecutions(IEnumerable<Execution> executions)
    {
        foreach (Execution execution in executions.Distinct())
        {
            execution.Cancel();
        }
    }

    private void StartExecution(Execution execution)
    {
        ObserveLateFault(ExecuteAsync(execution));
    }

    private static void SafeCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException exception)
        {
            AggregateException flattened = exception.Flatten();
            foreach (Exception inner in flattened.InnerExceptions)
            {
                ThrowIfProcessFatal(inner);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void ThrowIfProcessFatal(Exception exception)
    {
        if (IsProcessFatal(exception))
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(exception)
                .Throw();
        }
    }

    private static bool IsProcessFatal(Exception exception) =>
        exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException;

    private bool CanDeliver(Consumer consumer, Execution execution)
    {
        lock (_gate)
        {
            return !_disposed &&
                consumer.Registered &&
                execution.ConsumerSnapshots.TryGetValue(
                    consumer.Subscription.SubscriptionId,
                    out ConsumerSnapshot snapshot) &&
                ReferenceEquals(snapshot.Consumer, consumer) &&
                snapshot.Generation == consumer.Generation &&
                IsConsumerEligibleLocked(execution.Group, consumer) &&
                !execution.Cancellation.IsCancellationRequested;
        }
    }

    private static ProviderRefreshResult NormalizeResult(
        ProviderRefreshRequest request,
        ProviderRefreshResult? result)
    {
        if (result is not null && result.RequestId == request.RequestId)
        {
            return result;
        }

        return new ProviderRefreshResult(
            request.RequestId,
            ProviderRefreshResultKind.Failed,
            EmptyPayload(),
            request.DeadlineUtc,
            errorCode: "provider.invalid-result");
    }

    private static JsonElement EmptyPayload()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static ProviderRefreshResult CloneResult(ProviderRefreshResult result) =>
        new(
            result.RequestId,
            result.Kind,
            result.Payload,
            result.ProducedAtUtc,
            result.ValidUntilUtc,
            result.ErrorCode,
            result.RetryAfter);

    private static double NormalizeJitter(Func<double> jitter)
    {
        double value;
        try
        {
            value = jitter();
        }
        catch (Exception exception)
            when (!IsProcessFatal(exception))
        {
            return 0.5;
        }

        if (!double.IsFinite(value) || value < 0)
        {
            return 0;
        }

        return value >= 1 ? 0.9999999999999999 : value;
    }

    private static TimeSpan Add(TimeSpan value, TimeSpan duration)
    {
        try
        {
            return checked(value + duration);
        }
        catch (OverflowException)
        {
            return TimeSpan.MaxValue;
        }
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) =>
        first >= second ? first : second;

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                JsonProperty[] leftProperties = left.EnumerateObject().ToArray();
                JsonProperty[] rightProperties = right.EnumerateObject().ToArray();
                if (leftProperties.Length != rightProperties.Length)
                {
                    return false;
                }

                foreach (JsonProperty property in leftProperties)
                {
                    JsonProperty? match = rightProperties.FirstOrDefault(
                        candidate => string.Equals(
                            candidate.Name,
                            property.Name,
                            StringComparison.Ordinal));
                    if (match is null ||
                        !JsonElementsEqual(property.Value, match.Value.Value))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                JsonElement[] leftItems = left.EnumerateArray().ToArray();
                JsonElement[] rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length &&
                    leftItems.Zip(rightItems).All(pair =>
                        JsonElementsEqual(pair.First, pair.Second));
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            case JsonValueKind.Number:
                return left.GetRawText() == right.GetRawText() ||
                    (left.TryGetDecimal(out decimal leftDecimal) &&
                     right.TryGetDecimal(out decimal rightDecimal) &&
                     leftDecimal == rightDecimal);
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return left.GetRawText() == right.GetRawText();
        }
    }

    private sealed class Group
    {
        public Group(
            ProviderRequestKey key,
            IProviderRefreshSource source,
            ScheduledProviderDescriptor descriptor,
            JsonElement arguments,
            TimeSpan now)
        {
            Key = key;
            Source = source;
            Descriptor = descriptor;
            Arguments = ProviderRefreshSubscription.CloneJson(arguments);
            NextDue = now;
        }

        public ProviderRequestKey Key { get; }

        public IProviderRefreshSource Source { get; }

        public ScheduledProviderDescriptor Descriptor { get; }

        public JsonElement Arguments { get; }

        public Dictionary<Guid, Consumer> Consumers { get; } = [];

        public Execution? Active { get; set; }

        public Execution? Predecessor { get; set; }

        public long Generation { get; set; }

        public bool Pending { get; set; }

        public bool HadEligibleConsumer { get; set; }

        public TimeSpan? NextDue { get; set; }

        public int FailureCount { get; set; }

        public ProviderRefreshResultKind? LastFailureKind { get; set; }

        public TimeSpan? LastRetryAfter { get; set; }

        public bool ManualBypassAvailable { get; set; }

        public TimeSpan? LastManualAt { get; set; }

        public ProviderRefreshResult? LastSuccessfulResult { get; set; }
    }

    private sealed class Consumer
    {
        public Consumer(ProviderRefreshSubscription subscription)
        {
            Subscription = subscription;
            Visibility = subscription.InitialVisibility;
        }

        public ProviderRefreshSubscription Subscription { get; }

        public ProviderRefreshVisibility Visibility { get; set; }

        public long Generation { get; set; }

        public bool Registered { get; set; } = true;

        public Group? Group { get; set; }
    }

    private sealed class Execution
    {
        public Execution(
            Group group,
            ProviderRefreshRequest request,
            long generation,
            Dictionary<Guid, ConsumerSnapshot> consumerSnapshots)
        {
            Group = group;
            Request = request;
            Generation = generation;
            ConsumerSnapshots = consumerSnapshots;
        }

        public Group Group { get; }

        public ProviderRefreshRequest Request { get; }

        public long Generation { get; }

        public Dictionary<Guid, ConsumerSnapshot> ConsumerSnapshots { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource<object?> CancellationSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> ProviderCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? ProviderTask { get; set; }

        public bool SchedulerCompleted { get; set; }

        private int _sourceStartState;

        public bool TryClaimSourceStart() =>
            Interlocked.CompareExchange(ref _sourceStartState, 2, 0) == 0;

        public void Cancel()
        {
            Interlocked.CompareExchange(ref _sourceStartState, 1, 0);
            try
            {
                SafeCancel(Cancellation);
            }
            finally
            {
                CancellationSignal.TrySetResult(null);
            }
        }

    }

    private readonly record struct ConsumerSnapshot(
        Consumer Consumer,
        long Generation);

    private readonly record struct ConsumerTarget(
        Consumer Consumer,
        IProviderRefreshSink Sink);
}
