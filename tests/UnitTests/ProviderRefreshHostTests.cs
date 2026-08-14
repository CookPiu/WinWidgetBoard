using System.Text.Json;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class ProviderRefreshHostTests
{
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-CARD-102 [CRD-003/NFR-PERF-007] " +
        "production provider host owns one explicit pump loop")]
    public async Task HostOwnsExplicitPumpLoop()
    {
        var clock = new ManualHostClock(InitialUtc);
        await using var scheduler = new ProviderRefreshScheduler(clock);
        scheduler.SetNetworkState(ProviderNetworkState.Online);
        scheduler.SetPowerState(ProviderPowerState.Normal);
        await using var host = new ProviderRefreshHost(
            scheduler,
            clock,
            idlePollInterval: TimeSpan.FromSeconds(5));
        var source = new ImmediateSource(CreateDescriptor());
        var sink = new RecordingSink();
        Guid subscriptionId = Guid.NewGuid();
        using ProviderRefreshHostRegistration registration = host.Register(
            new ProviderRefreshSubscription(
                subscriptionId,
                CreateKey(),
                JsonSerializer.SerializeToElement(new { city = "Singapore" }),
                source,
                sink,
                new ProviderRefreshVisibility(true, true, true)));

        using var cancellation = new CancellationTokenSource();
        Task hostTask = host.RunAsync(cancellation.Token);
        await sink.FirstResult.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, source.CallCount);
        Assert.AreEqual(1, sink.Results.Count);
        Assert.IsTrue(clock.DelayCount > 0);
        Assert.AreEqual(1, host.RegistrationCount);

        cancellation.Cancel();
        await hostTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod(DisplayName =
        "UT-CARD-103 [CRD-003/CRD-005] " +
        "host registration disposal removes the scheduler consumer")]
    public async Task HostRegistrationDisposalRemovesConsumer()
    {
        var clock = new ManualHostClock(InitialUtc);
        await using var scheduler = new ProviderRefreshScheduler(clock);
        await using var host = new ProviderRefreshHost(scheduler, clock);
        using ProviderRefreshHostRegistration registration = host.Register(
            new ProviderRefreshSubscription(
                Guid.NewGuid(),
                CreateKey(),
                JsonSerializer.SerializeToElement(new { city = "Singapore" }),
                new ImmediateSource(CreateDescriptor()),
                new RecordingSink(),
                new ProviderRefreshVisibility(true, true, true)));

        Assert.AreEqual(1, host.RegistrationCount);
        Assert.AreEqual(1, scheduler.SubscriptionCount);
        registration.Dispose();
        Assert.AreEqual(0, host.RegistrationCount);
        Assert.AreEqual(0, scheduler.SubscriptionCount);
    }

    [TestMethod(DisplayName =
        "UT-CARD-104 [CRD-005] " +
        "fatal provider faults reach the process supervisor")]
    public async Task FatalProviderFaultReachesSupervisor()
    {
        var clock = new ManualHostClock(InitialUtc);
        var observed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new ProviderProcessFatalSupervisor(
            exception => observed.TrySetResult(exception));
        await using var scheduler = new ProviderRefreshScheduler(
            clock,
            processFatalFaultHandler: supervisor.Observe);
        scheduler.SetNetworkState(ProviderNetworkState.Online);
        scheduler.SetPowerState(ProviderPowerState.Normal);
        scheduler.Register(new ProviderRefreshSubscription(
            Guid.NewGuid(),
            CreateKey(),
            JsonSerializer.SerializeToElement(new { city = "Singapore" }),
            new FatalSource(CreateDescriptor()),
            new RecordingSink(),
            new ProviderRefreshVisibility(true, true, true)));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        Exception exception = await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsInstanceOfType<OutOfMemoryException>(exception);
        Assert.IsTrue(supervisor.HasFatalFault);
        Assert.AreSame(exception, supervisor.FatalFault);
    }

    private static ProviderRequestKey CreateKey() => new(
        "app.test.host-provider",
        "test.current",
        "weather:singapore",
        "sha256:arguments");

    private static ScheduledProviderDescriptor CreateDescriptor() =>
        new(
            "app.test.host-provider",
            "test.current",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(1),
            requiresNetwork: true,
            supportsManualRefresh: false,
            TimeSpan.FromSeconds(1),
            new ProviderBackoffOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(10),
                jitterRatio: 0));

    private sealed class ImmediateSource : IProviderRefreshSource
    {
        public ImmediateSource(ScheduledProviderDescriptor descriptor)
        {
            Descriptor = descriptor;
        }

        public ScheduledProviderDescriptor Descriptor { get; }

        public int CallCount { get; private set; }

        public ValueTask<ProviderRefreshResult> FetchAsync(
            ProviderRefreshRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(new ProviderRefreshResult(
                request.RequestId,
                ProviderRefreshResultKind.Success,
                JsonSerializer.SerializeToElement(new { value = 1 }),
                InitialUtc,
                InitialUtc.AddMinutes(1)));
        }
    }

    private sealed class FatalSource : IProviderRefreshSource
    {
        public FatalSource(ScheduledProviderDescriptor descriptor)
        {
            Descriptor = descriptor;
        }

        public ScheduledProviderDescriptor Descriptor { get; }

        public ValueTask<ProviderRefreshResult> FetchAsync(
            ProviderRefreshRequest request,
            CancellationToken cancellationToken) =>
            throw CreateProcessFatalException();
    }

    private static OutOfMemoryException CreateProcessFatalException() =>
        Activator.CreateInstance<OutOfMemoryException>();

    private sealed class RecordingSink : IProviderRefreshSink
    {
        public List<ProviderRefreshResult> Results { get; } = [];

        public TaskCompletionSource<ProviderRefreshResult> FirstResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ApplyAsync(
            ProviderRefreshRequest request,
            ProviderRefreshResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Results.Add(result);
            FirstResult.TrySetResult(result);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualHostClock : IProviderRefreshClock
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<object?>> _delays = [];

        public ManualHostClock(DateTimeOffset initialUtc)
        {
            UtcNow = initialUtc;
        }

        public DateTimeOffset UtcNow { get; }

        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public int DelayCount { get; private set; }

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                DelayCount++;
                _delays.Add(completion);
            }

            cancellationToken.Register(
                static state =>
                {
                    ((TaskCompletionSource<object?>)state!).TrySetCanceled();
                },
                completion);
            return new ValueTask(completion.Task);
        }
    }
}
