using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class ProviderRefreshVisibilityRegistryTests
{
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-WEA-005 [WEA-001/CRD-003/NFR-PERF-007] cards.subscribe visibility gates weather refresh")]
    public async Task SubscriptionVisibilityGatesScheduler()
    {
        var clock = new FixedClock(InitialUtc);
        await using var scheduler = new ProviderRefreshScheduler(clock);
        scheduler.SetNetworkState(ProviderNetworkState.Online);
        scheduler.SetPowerState(ProviderPowerState.Normal);
        var source = new ImmediateSource();
        var sink = new RecordingSink();
        Guid subscriptionId = Guid.NewGuid();
        using ProviderRefreshRegistration registration = scheduler.Register(
            new ProviderRefreshSubscription(
                subscriptionId,
                source.Key,
                source.Arguments,
                source,
                sink,
                new ProviderRefreshVisibility(false, false, false)));
        using var registry = new ProviderRefreshVisibilityRegistry(scheduler);
        registry.Register("demo.weather", subscriptionId);
        Guid connectionId = Guid.NewGuid();

        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        Assert.IsTrue(registry.Apply(
            connectionId,
            new CardsSubscribeRequest
            {
                InstanceIds = ["demo.weather"],
                Visibility = new CardSubscriptionVisibility
                {
                    PanelVisible = true,
                    VisibleInstanceIds = ["demo.weather"],
                },
            }));
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.FirstResult.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(registry.Apply(
            connectionId,
            new CardsSubscribeRequest
            {
                InstanceIds = ["demo.weather"],
                Visibility = new CardSubscriptionVisibility
                {
                    PanelVisible = false,
                    VisibleInstanceIds = [],
                },
            }));
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
    }

    [TestMethod(DisplayName =
        "UT-WEA-006 [CRD-003/NFR-REL-004] disconnect clears provider visibility")]
    public async Task DisconnectStopsProviderRefresh()
    {
        var clock = new FixedClock(InitialUtc);
        await using var scheduler = new ProviderRefreshScheduler(clock);
        scheduler.SetNetworkState(ProviderNetworkState.Online);
        scheduler.SetPowerState(ProviderPowerState.Normal);
        var source = new ImmediateSource();
        Guid subscriptionId = Guid.NewGuid();
        using ProviderRefreshRegistration registration = scheduler.Register(
            new ProviderRefreshSubscription(
                subscriptionId,
                source.Key,
                source.Arguments,
                source,
                new RecordingSink(),
                new ProviderRefreshVisibility(false, false, false)));
        using var registry = new ProviderRefreshVisibilityRegistry(scheduler);
        registry.Register("demo.weather", subscriptionId);
        Guid connectionId = Guid.NewGuid();
        registry.Apply(
            connectionId,
            new CardsSubscribeRequest
            {
                InstanceIds = ["demo.weather"],
                Visibility = new CardSubscriptionVisibility
                {
                    PanelVisible = true,
                    VisibleInstanceIds = ["demo.weather"],
                },
            });
        Assert.IsTrue(registry.Remove(connectionId));

        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        Assert.IsFalse(registry.Remove(connectionId));
    }

    private sealed class ImmediateSource : IProviderRefreshSource
    {
        public ProviderRequestKey Key { get; } = new(
            "test.weather",
            "weather.current",
            "api.open-meteo.com",
            "sha256:weather");

        public JsonElement Arguments { get; } =
            JsonSerializer.SerializeToElement(new
            {
                label = "Singapore",
                latitude = 1.3521,
                longitude = 103.8198,
            });

        public ScheduledProviderDescriptor Descriptor { get; } = new(
            "test.weather",
            "weather.current",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            hiddenInterval: null,
            powerSaverInterval: null,
            requestTimeout: TimeSpan.FromSeconds(1),
            requiresNetwork: true,
            supportsManualRefresh: false,
            manualRefreshMinimumInterval: TimeSpan.FromSeconds(1),
            new ProviderBackoffOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(10),
                jitterRatio: 0));

        public ValueTask<ProviderRefreshResult> FetchAsync(
            ProviderRefreshRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ProviderRefreshResult(
                request.RequestId,
                ProviderRefreshResultKind.Success,
                JsonSerializer.SerializeToElement(new { temperatureC = 31.2 }),
                InitialUtc,
                InitialUtc.AddMinutes(15)));
        }
    }

    private sealed class RecordingSink : IProviderRefreshSink
    {
        public TaskCompletionSource<object?> FirstResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ApplyAsync(
            ProviderRefreshRequest request,
            ProviderRefreshResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FirstResult.TrySetResult(null);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock : IProviderRefreshClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }

        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
