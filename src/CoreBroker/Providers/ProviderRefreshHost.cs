namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// The single production owner of the scheduled-provider pump. Provider
/// sources never create their own timers; this host only invokes the explicit
/// scheduler pump and waits on the injected monotonic clock.
/// </summary>
public sealed class ProviderRefreshHost : IAsyncDisposable
{
    private static readonly TimeSpan DefaultIdlePollInterval =
        TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly ProviderRefreshScheduler _scheduler;
    private readonly IProviderRefreshClock _clock;
    private readonly TimeSpan _idlePollInterval;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<Guid, RegistrationEntry> _registrations = [];
    private bool _runStarted;
    private bool _disposed;
    private Task? _runTask;

    public ProviderRefreshHost(
        ProviderRefreshScheduler scheduler,
        IProviderRefreshClock? clock = null,
        TimeSpan? idlePollInterval = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _clock = clock ?? new TimeProviderRefreshClock();
        _idlePollInterval = ValidateIdlePollInterval(
            idlePollInterval ?? DefaultIdlePollInterval);
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

    public ProviderRefreshHostRegistration Register(
        ProviderRefreshSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ProviderRefreshRegistration registration = _scheduler.Register(subscription);
            var entry = new RegistrationEntry(
                subscription.Key,
                registration);
            _registrations.Add(registration.SubscriptionId, entry);
            return new ProviderRefreshHostRegistration(
                this,
                registration.SubscriptionId,
                registration);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task runTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runStarted)
            {
                throw new InvalidOperationException(
                    "Provider refresh host can only be run once.");
            }

            _runStarted = true;
            runTask = RunCoreAsync(cancellationToken);
            _runTask = runTask;
        }

        await runTask.ConfigureAwait(false);
    }

    internal void Unregister(
        Guid subscriptionId,
        ProviderRefreshRegistration registration)
    {
        lock (_gate)
        {
            _registrations.Remove(subscriptionId);
        }

        registration.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        ProviderRefreshRegistration[] registrations;
        Task? runTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();
            registrations = _registrations
                .Values
                .Select(entry => entry.Registration)
                .ToArray();
            _registrations.Clear();
            runTask = _runTask;
        }

        foreach (ProviderRefreshRegistration registration in registrations)
        {
            registration.Dispose();
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _stop.Token);
        CancellationToken token = linkedCancellation.Token;
        while (true)
        {
            try
            {
                await _scheduler.PumpDueAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }

            TimeSpan delay = GetNextDelay();
            try
            {
                await _clock.DelayAsync(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private TimeSpan GetNextDelay()
    {
        Guid[] subscriptionIds;
        lock (_gate)
        {
            subscriptionIds = _registrations.Keys.ToArray();
        }

        TimeSpan? nextDue = null;
        foreach (Guid subscriptionId in subscriptionIds)
        {
            RegistrationEntry? entry;
            lock (_gate)
            {
                _registrations.TryGetValue(subscriptionId, out entry);
            }

            if (entry is null ||
                !_scheduler.TryGetState(entry.Key, out ProviderRefreshSchedulerStateSnapshot? state) ||
                state?.NextDueIn is not { } due)
            {
                continue;
            }

            nextDue = nextDue is null || due < nextDue.Value
                ? due
                : nextDue;
        }

        if (nextDue is null)
        {
            return _idlePollInterval;
        }

        if (nextDue.Value <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(1);
        }

        return nextDue.Value <= _idlePollInterval
            ? nextDue.Value
            : _idlePollInterval;
    }

    private static TimeSpan ValidateIdlePollInterval(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Idle poll interval must be finite and positive.");
        }

        return value;
    }

    private sealed record RegistrationEntry(
        ProviderRequestKey Key,
        ProviderRefreshRegistration Registration);
}

public sealed class ProviderRefreshHostRegistration : IDisposable
{
    private readonly ProviderRefreshHost _host;
    private readonly Guid _subscriptionId;
    private ProviderRefreshRegistration? _registration;

    internal ProviderRefreshHostRegistration(
        ProviderRefreshHost host,
        Guid subscriptionId,
        ProviderRefreshRegistration registration)
    {
        _host = host;
        _subscriptionId = subscriptionId;
        _registration = registration;
    }

    public Guid SubscriptionId => _subscriptionId;

    public void Dispose()
    {
        ProviderRefreshRegistration? registration =
            Interlocked.Exchange(ref _registration, null);
        if (registration is not null)
        {
            _host.Unregister(_subscriptionId, registration);
        }
    }
}
