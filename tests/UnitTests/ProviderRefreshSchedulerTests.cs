using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class ProviderRefreshSchedulerTests
{
    private static readonly DateTimeOffset InitialUtc =
        new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-CARD-077 [NFR-PERF-007] Same key fetches once and fans out")]
    public async Task SameKeyFetchesOnceAndFansOut()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor());
        var first = new RecordingSink();
        var second = new RecordingSink();
        await using var scheduler = CreateScheduler(clock, () => 0.5);
        ProviderRequestKey key = CreateKey("args-a");

        scheduler.Register(CreateSubscription(
            key, source, first, Visible()));
        scheduler.Register(CreateSubscription(
            key, source, second, Visible()));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await first.FirstResult.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await second.FirstResult.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, source.CallCount);
        Assert.AreEqual(1, first.Results.Count);
        Assert.AreEqual(1, second.Results.Count);
        Assert.AreEqual(
            source.LastRequest!.RequestId,
            first.Results[0].RequestId);
        Assert.AreEqual(
            source.LastRequest.RequestId,
            second.Results[0].RequestId);
    }

    [TestMethod(DisplayName =
        "UT-CARD-078 [NFR-PERF-007] Different keys do not merge and incompatible sources are rejected")]
    public async Task DifferentKeysDoNotMergeAndIncompatibleSourcesAreRejected()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor());
        var differentSource = new ScriptedSource(CreateDescriptor());
        await using var scheduler = CreateScheduler(clock);

        scheduler.Register(CreateSubscription(
            CreateKey("args-a"), source, new RecordingSink(), Visible()));
        scheduler.Register(CreateSubscription(
            CreateKey("args-b"), source, new RecordingSink(), Visible()));
        Assert.AreEqual(2, await scheduler.PumpDueAsync());
        Assert.AreEqual(2, source.CallCount);

        ProviderRequestKey sameKey = CreateKey("args-c");
        scheduler.Register(CreateSubscription(
            sameKey, source, new RecordingSink(), Visible()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            scheduler.Register(CreateSubscription(
                sameKey, differentSource, new RecordingSink(), Visible())));

        using JsonDocument document = JsonDocument.Parse(
            "{\"city\":\"Singapore\"}");
        Assert.ThrowsExactly<ArgumentException>(() =>
            scheduler.Register(new ProviderRefreshSubscription(
                Guid.NewGuid(),
                sameKey,
                document.RootElement,
                source,
                new RecordingSink(),
                Visible())));
    }

    [TestMethod(DisplayName =
        "UT-CARD-079 [CRD-003/NFR-PERF-007] Visibility cadence and repeated pump are bounded")]
    public async Task VisibilityCadenceAndRepeatedPumpAreBounded()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(10),
            hiddenInterval: TimeSpan.FromSeconds(20)));
        var sink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock);
        Guid subscriptionId = Guid.NewGuid();
        ProviderRequestKey key = CreateKey("cadence");
        scheduler.Register(CreateSubscription(
            key, source, sink, Visible(), subscriptionId));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(1);
        Assert.AreEqual(0, await scheduler.PumpDueAsync());

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(2);

        scheduler.SetVisibility(subscriptionId, Hidden());
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(3);

        Assert.AreEqual(3, source.CallCount);
    }

    [TestMethod(DisplayName =
        "UT-CARD-080 [CRD-003] Network and power pauses are idempotent and resume once")]
    public async Task NetworkAndPowerPausesAreIdempotentAndResumeOnce()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            powerSaverInterval: TimeSpan.FromSeconds(30)));
        var sink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock);
        Guid subscriptionId = Guid.NewGuid();
        scheduler.Register(CreateSubscription(
            CreateKey("state"), source, sink, Visible(), subscriptionId));

        scheduler.SetNetworkState(ProviderNetworkState.Offline);
        scheduler.SetNetworkState(ProviderNetworkState.Offline);
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        Assert.AreEqual(0, source.CallCount);

        scheduler.SetNetworkState(ProviderNetworkState.Online);
        scheduler.SetNetworkState(ProviderNetworkState.Online);
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(1);

        scheduler.SetPowerState(ProviderPowerState.BatterySaver);
        scheduler.SetPowerState(ProviderPowerState.BatterySaver);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(2);

        scheduler.SetPowerState(ProviderPowerState.Critical);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        scheduler.SetPowerState(ProviderPowerState.Normal);
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(3);
    }

    [TestMethod(DisplayName =
        "UT-CARD-081 [CRD-002/CRD-005] Timeout is classified, cancellation is silent and last success remains")]
    public async Task TimeoutIsClassifiedCancellationIsSilentAndLastSuccessRemains()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(5),
            requestTimeout: TimeSpan.FromSeconds(3)));
        var sink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock);
        Guid subscriptionId = Guid.NewGuid();
        scheduler.Register(CreateSubscription(
            CreateKey("timeout"), source, sink, Visible(), subscriptionId));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(1);
        Assert.AreEqual(0, scheduler.GetState(CreateKey("timeout")).FailureCount);

        clock.Advance(TimeSpan.FromSeconds(5));
        source.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(3));
        await sink.WaitForCountAsync(2);
        Assert.AreEqual(1, scheduler.GetState(CreateKey("timeout")).FailureCount);

        Assert.AreEqual(ProviderRefreshResultKind.TimedOut, sink.Results[1].Kind);
        Assert.AreEqual("provider.timeout", sink.Results[1].ErrorCode);
        Assert.AreEqual(
            1,
            sink.Results[1].Payload.GetProperty("value").GetInt32());

        source.SetNextPending();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        scheduler.SetVisibility(subscriptionId, Hidden());
        source.ReleasePending(CreateResult(
            source.LastRequest!.RequestId,
            ProviderRefreshResultKind.Success,
            value: 3));
        await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, sink.Results.Count);
    }

    [TestMethod(DisplayName =
        "UT-CARD-082 [CRD-002] Failure backoff resets after success with deterministic jitter")]
    public async Task FailureBackoffResetsAfterSuccessWithDeterministicJitter()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(
            CreateDescriptor(
                visibleInterval: TimeSpan.FromSeconds(10),
                backoff: new ProviderBackoffOptions(
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(20),
                    0.2)));
        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Failed,
            errorCode: "provider.failed");
        var sink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock, () => 0.5);
        scheduler.Register(CreateSubscription(
            CreateKey("backoff"), source, sink, Visible()));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(1);
        ProviderRefreshSchedulerStateSnapshot failure =
            scheduler.GetState(CreateKey("backoff"));
        Assert.AreEqual(1, failure.FailureCount);
        Assert.AreEqual(TimeSpan.FromSeconds(2), failure.NextDueIn);

        clock.Advance(TimeSpan.FromSeconds(2));
        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Success,
            value: 8);
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(2);
        ProviderRefreshSchedulerStateSnapshot success =
            scheduler.GetState(CreateKey("backoff"));
        Assert.AreEqual(0, success.FailureCount);
        Assert.AreEqual(TimeSpan.FromSeconds(10), success.NextDueIn);
    }

    [TestMethod(DisplayName =
        "UT-CARD-091 [CRD-002] Retry-After blocks manual bypass for every failure kind")]
    public async Task RetryAfterBlocksManualBypassForEveryFailureKind()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            supportsManualRefresh: true,
            manualMinimumInterval: TimeSpan.FromSeconds(1),
            backoff: new ProviderBackoffOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(1),
                0.2)));
        var sink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock, () => 0.5);
        Guid subscriptionId = Guid.NewGuid();
        ProviderRequestKey key = CreateKey("retry-after");
        scheduler.Register(CreateSubscription(
            key,
            source,
            sink,
            Visible(),
            subscriptionId));

        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Failed,
            errorCode: "provider.failed",
            retryAfter: TimeSpan.FromSeconds(20));
        Assert.AreEqual(
            ProviderManualRefreshOutcome.Started,
            await scheduler.RequestManualRefreshAsync(subscriptionId));
        await sink.WaitForCountAsync(1);
        Assert.AreEqual(TimeSpan.FromSeconds(20),
            scheduler.GetState(key).NextDueIn);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(
            ProviderManualRefreshOutcome.RateLimited,
            await scheduler.RequestManualRefreshAsync(subscriptionId));

        clock.Advance(TimeSpan.FromSeconds(19));
        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Success,
            value: 41);
        Assert.AreEqual(
            ProviderManualRefreshOutcome.Started,
            await scheduler.RequestManualRefreshAsync(subscriptionId));
        await sink.WaitForCountAsync(2);
    }

    [TestMethod(DisplayName =
        "UT-CARD-083 [CRD-002/CRD-005] Manual refresh bypasses once, rate limits and coalesces")]
    public async Task ManualRefreshBypassesOnceRateLimitsAndCoalesces()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromMinutes(5),
            supportsManualRefresh: true,
            manualMinimumInterval: TimeSpan.FromSeconds(30)));
        var sink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock, () => 0.5);
        Guid subscriptionId = Guid.NewGuid();
        scheduler.Register(CreateSubscription(
            CreateKey("manual"), source, sink, Visible(), subscriptionId));

        Assert.AreEqual(
            ProviderManualRefreshOutcome.Started,
            await scheduler.RequestManualRefreshAsync(subscriptionId));
        await sink.WaitForCountAsync(1);
        Assert.AreEqual(
            ProviderManualRefreshOutcome.RateLimited,
            await scheduler.RequestManualRefreshAsync(subscriptionId));

        clock.Advance(TimeSpan.FromSeconds(30));
        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Failed,
            errorCode: "provider.failed");
        Assert.AreEqual(
            ProviderManualRefreshOutcome.Started,
            await scheduler.RequestManualRefreshAsync(subscriptionId));
        await sink.WaitForCountAsync(2);

        clock.Advance(TimeSpan.FromMinutes(1));
        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Success,
            value: 4);
        Assert.AreEqual(
            ProviderManualRefreshOutcome.Started,
            await scheduler.RequestManualRefreshAsync(subscriptionId));
        await sink.WaitForCountAsync(3);

        source.SetNextPending();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(
            ProviderManualRefreshOutcome.Coalesced,
            await scheduler.RequestManualRefreshAsync(subscriptionId));
        source.ReleasePending(CreateResult(
            source.LastRequest!.RequestId,
            ProviderRefreshResultKind.Success,
            value: 5));
        await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod(DisplayName =
        "UT-CARD-084 [CRD-005] Source and sink failures are isolated")]
    public async Task SourceAndSinkFailuresAreIsolated()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor())
        {
            NextResult = _ => throw new InvalidOperationException("boom")
        };
        var throwingSink = new RecordingSink { ThrowOnApply = true };
        var healthySink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock);
        ProviderRequestKey key = CreateKey("faults");
        scheduler.Register(CreateSubscription(
            key, source, throwingSink, Visible()));
        scheduler.Register(CreateSubscription(
            key, source, healthySink, Visible()));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await healthySink.WaitForCountAsync(1);
        Assert.AreEqual(ProviderRefreshResultKind.Failed, healthySink.Results[0].Kind);
        Assert.AreEqual("provider.failed", healthySink.Results[0].ErrorCode);
        Assert.AreEqual(1, scheduler.GetState(key).FailureCount);
    }

    [TestMethod(DisplayName =
        "UT-CARD-085 [CRD-003/CRD-005] Hide, unregister and ID reuse reject late results")]
    public async Task HideUnregisterAndIdReuseRejectLateResults()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(pauseHidden: true));
        var oldSink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock);
        Guid subscriptionId = Guid.NewGuid();
        ProviderRequestKey key = CreateKey("identity");
        scheduler.Register(CreateSubscription(
            key, source, oldSink, Visible(), subscriptionId));
        source.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        scheduler.SetVisibility(subscriptionId, Hidden());
        scheduler.Unregister(subscriptionId);
        var newSink = new RecordingSink();
        scheduler.Register(CreateSubscription(
            key, source, newSink, Hidden(), subscriptionId));
        source.ReleasePending(CreateResult(
            source.LastRequest!.RequestId,
            ProviderRefreshResultKind.Success,
            value: 9));
        await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, oldSink.Results.Count);
        Assert.AreEqual(0, newSink.Results.Count);

        var sharedSource = new ScriptedSource(CreateDescriptor(pauseHidden: true));
        var firstSharedSink = new RecordingSink();
        var secondSharedSink = new RecordingSink();
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        scheduler.Register(CreateSubscription(
            CreateKey("shared"), sharedSource, firstSharedSink, Visible(), firstId));
        scheduler.Register(CreateSubscription(
            CreateKey("shared"), sharedSource, secondSharedSink, Visible(), secondId));
        sharedSource.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sharedSource.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        scheduler.SetVisibility(firstId, Hidden());
        sharedSource.ReleasePending(CreateResult(
            sharedSource.LastRequest!.RequestId,
            ProviderRefreshResultKind.Success,
            value: 10));
        await sharedSource.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await secondSharedSink.WaitForCountAsync(1);
        Assert.AreEqual(0, firstSharedSink.Results.Count);
        Assert.AreEqual(1, secondSharedSink.Results.Count);
    }

    [TestMethod(DisplayName =
        "UT-CARD-086 [CRD-002] Wall-clock jumps do not change monotonic due")]
    public async Task WallClockJumpsDoNotChangeMonotonicDue()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(20)));
        var sink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock);
        scheduler.Register(CreateSubscription(
            CreateKey("clock"), source, sink, Visible()));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(1);
        clock.SetUtc(InitialUtc.AddDays(30));
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        clock.SetUtc(InitialUtc.AddDays(-30));
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(2);
    }

    [TestMethod(DisplayName =
        "UT-CARD-087 [NFR-PERF-007] Rapid signals keep active and pending work bounded")]
    public async Task RapidSignalsKeepActiveAndPendingWorkBounded()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(1),
            pauseHidden: true));
        var sink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock);
        Guid subscriptionId = Guid.NewGuid();
        scheduler.Register(CreateSubscription(
            CreateKey("bounded"), source, sink, Visible(), subscriptionId));

        source.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 100; i++)
        {
            scheduler.SetVisibility(subscriptionId, i % 2 == 0
                ? Hidden()
                : Visible());
            _ = await scheduler.PumpDueAsync();
        }

        ProviderRefreshSchedulerStateSnapshot state =
            scheduler.GetState(CreateKey("bounded"));
        Assert.IsTrue(state.InFlightCount <= 2);
        Assert.IsTrue(state.PendingCount <= 1);
        source.ReleasePending(CreateResult(
            source.LastRequest!.RequestId,
            ProviderRefreshResultKind.Success,
            value: 11));
        await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod(DisplayName =
        "UT-CARD-088 [CRD-005] Dispose is bounded, idempotent and observes late faults")]
    public async Task DisposeIsBoundedIdempotentAndObservesLateFaults()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor())
        {
            IgnoreCancellation = true
        };
        var sink = new RecordingSink();
        var scheduler = CreateScheduler(
            clock,
            disposeTimeout: TimeSpan.FromSeconds(1));
        scheduler.Register(CreateSubscription(
            CreateKey("dispose"), source, sink, Visible()));
        source.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        ValueTask dispose = scheduler.DisposeAsync();
        Assert.IsFalse(dispose.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForDisposeAsync(scheduler);

        source.ReleaseException(new InvalidOperationException("late"));
        Assert.AreEqual(0, sink.Results.Count);
    }

    [TestMethod(DisplayName =
        "UT-CARD-089 [CRD-002/NFR-PERF-007] Ignoring-cancellation predecessors stay bounded")]
    public async Task IgnoringCancellationPredecessorsStayBounded()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(1),
            requestTimeout: TimeSpan.FromSeconds(2),
            backoff: new ProviderBackoffOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(10),
                0.2)))
        {
            IgnoreCancellation = true
        };
        var sink = new RecordingSink();
        var scheduler = CreateScheduler(clock, () => 0.5);
        ProviderRequestKey key = CreateKey("predecessor");
        scheduler.Register(CreateSubscription(
            key,
            source,
            sink,
            Visible()));

        source.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        await sink.WaitForCountAsync(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        source.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        await sink.WaitForCountAsync(2);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        ProviderRefreshSchedulerStateSnapshot bounded =
            scheduler.GetState(key);
        Assert.IsTrue(bounded.InFlightCount <= 2);
        Assert.IsTrue(bounded.PendingCount <= 1);
        Assert.AreEqual(2, source.CallCount);

        // Release the original predecessor first. The second timed-out
        // execution still occupies the active slot until its provider task
        // completes, so no third source call can be started yet.
        source.ReleasePendingAt(
            0,
            CreateResult(
                source.Requests[0].RequestId,
                ProviderRefreshResultKind.Success,
                value: 20));
        await source.WaitForPendingCountAsync(1);
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        Assert.AreEqual(2, source.CallCount);

        source.ReleasePendingAt(
            0,
            CreateResult(
                source.Requests[1].RequestId,
                ProviderRefreshResultKind.Success,
                value: 21));
        await WaitForConditionAsync(
            () => scheduler.GetState(key).InFlightCount == 0);

        source.SetNextPending();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(3, source.CallCount);
        ProviderRefreshSchedulerStateSnapshot resumed =
            scheduler.GetState(key);
        Assert.IsTrue(resumed.InFlightCount <= 1);

        ValueTask dispose = scheduler.DisposeAsync();
        Assert.IsFalse(dispose.IsCompleted);
        source.ReleaseException(new InvalidOperationException("late"));
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForDisposeAsync(scheduler);
    }

    [TestMethod(DisplayName =
        "UT-CARD-090 [CRD-005/NFR-PERF-007] New consumer during fetch receives one bounded follow-up")]
    public async Task NewConsumerDuringFetchReceivesOneBoundedFollowUp()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(10)));
        var firstSink = new RecordingSink();
        var secondSink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock);
        ProviderRequestKey key = CreateKey("new-consumer");
        scheduler.Register(CreateSubscription(
            key,
            source,
            firstSink,
            Visible()));

        source.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Guid secondId = Guid.NewGuid();
        scheduler.Register(CreateSubscription(
            key,
            source,
            secondSink,
            Visible(),
            secondId));
        source.ReleasePending(CreateResult(
            source.LastRequest!.RequestId,
            ProviderRefreshResultKind.Success,
            value: 30));
        await firstSink.WaitForCountAsync(1);
        Assert.AreEqual(0, secondSink.Results.Count);

        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Success,
            value: 31);
        int starts = 0;
        for (int attempt = 0; attempt < 8 && starts == 0; attempt++)
        {
            starts = await scheduler.PumpDueAsync();
            if (starts == 0)
            {
                await Task.Yield();
            }
        }

        Assert.AreEqual(1, starts);
        await secondSink.WaitForCountAsync(1);
        Assert.AreEqual(2, source.CallCount);
        Assert.IsTrue(scheduler.GetState(key).InFlightCount <= 1);
    }

    [TestMethod(DisplayName =
        "UT-CARD-092 [CRD-005] Reused subscription IDs cannot accept late fan-out")]
    public async Task ReusedSubscriptionIdsCannotAcceptLateFanOut()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor());
        var oldSink = new RecordingSink();
        var retainedSink = new RecordingSink();
        var replacementSink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock);
        ProviderRequestKey key = CreateKey("identity-reuse");
        Guid subscriptionId = Guid.NewGuid();

        scheduler.Register(CreateSubscription(
            key,
            source,
            oldSink,
            Visible(),
            subscriptionId));
        scheduler.Register(CreateSubscription(
            key,
            source,
            retainedSink,
            Visible()));
        source.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(scheduler.Unregister(subscriptionId));
        scheduler.Register(CreateSubscription(
            key,
            source,
            replacementSink,
            Visible(),
            subscriptionId));
        source.ReleasePending(CreateResult(
            source.LastRequest!.RequestId,
            ProviderRefreshResultKind.Success,
            value: 50));
        await retainedSink.WaitForCountAsync(1);
        Assert.AreEqual(0, oldSink.Results.Count);
        Assert.AreEqual(0, replacementSink.Results.Count);

        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Success,
            value: 51);
        int starts = 0;
        for (int attempt = 0; attempt < 8 && starts == 0; attempt++)
        {
            starts = await scheduler.PumpDueAsync();
            if (starts == 0)
            {
                await Task.Yield();
            }
        }

        Assert.AreEqual(1, starts);
        await replacementSink.WaitForCountAsync(1);
        Assert.AreEqual(2, source.CallCount);
    }

    [TestMethod(DisplayName =
        "UT-CARD-093 [CRD-002/NFR-PERF-007] External clock jitter and descriptor seams reenter safely")]
    public async Task ExternalSeamsReenterSafely()
    {
        var baseClock = new ManualClock(InitialUtc);
        ProviderRefreshScheduler? scheduler = null;
        var clock = new ReentrantClock(
            baseClock,
            () =>
            {
                _ = scheduler!.GroupCount;
                _ = scheduler.SubscriptionCount;
            });
        scheduler = new ProviderRefreshScheduler(
            clock,
            () =>
            {
                _ = scheduler!.GroupCount;
                return 0.5;
            });
        scheduler.SetNetworkState(ProviderNetworkState.Online);
        scheduler.SetPowerState(ProviderPowerState.Normal);

        var source = new ScriptedSource(CreateDescriptor())
        {
            DescriptorReadCallback = () => _ = scheduler!.GroupCount,
            NextResult = request => CreateResult(
                request.RequestId,
                ProviderRefreshResultKind.Failed,
                errorCode: "provider.failed")
        };
        var sink = new RecordingSink();
        ProviderRequestKey key = CreateKey("reentrant-seams");
        scheduler.Register(CreateSubscription(
            key,
            source,
            sink,
            Visible()));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(1);
        Assert.IsTrue(clock.ReentryCount > 0);
        Assert.AreEqual(1, source.DescriptorReadCount);
        await WaitForDisposeAsync(scheduler);
    }

    [TestMethod(DisplayName =
        "UT-CARD-094 [CRD-005] Throwing cancellation callbacks are isolated")]
    public async Task ThrowingCancellationCallbacksAreIsolated()
    {
        var clock = new ManualClock(InitialUtc);
        var scheduler = CreateScheduler(
            clock,
            disposeTimeout: TimeSpan.FromSeconds(1));

        var successSource = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(3)))
        {
            ThrowOnCancellationRegistration = true
        };
        var successSink = new RecordingSink();
        ProviderRequestKey successKey = CreateKey("cancel-success");
        scheduler.Register(CreateSubscription(
            successKey,
            successSource,
            successSink,
            Visible()));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await successSink.WaitForCountAsync(1);
        Assert.AreEqual(
            ProviderRefreshResultKind.Success,
            successSink.Results[0].Kind);

        var timeoutSource = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(1),
            requestTimeout: TimeSpan.FromSeconds(2),
            backoff: new ProviderBackoffOptions(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1),
                0.2)))
        {
            IgnoreCancellation = true,
            ThrowOnCancellationRegistration = true
        };
        var timeoutSink = new RecordingSink();
        ProviderRequestKey timeoutKey = CreateKey("cancel-timeout");
        Guid timeoutId = Guid.NewGuid();
        scheduler.Register(CreateSubscription(
            timeoutKey,
            timeoutSource,
            timeoutSink,
            Visible(),
            timeoutId));
        timeoutSource.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await timeoutSource.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        await timeoutSink.WaitForCountAsync(1);
        Assert.AreEqual(
            ProviderRefreshResultKind.TimedOut,
            timeoutSink.Results[0].Kind);
        Assert.IsTrue(scheduler.Unregister(timeoutId));

        var cancelSource = new ScriptedSource(CreateDescriptor(
            pauseHidden: true))
        {
            ThrowOnCancellationRegistration = true
        };
        var cancelSink = new RecordingSink();
        Guid cancelId = Guid.NewGuid();
        ProviderRequestKey cancelKey = CreateKey("cancel-hide");
        scheduler.Register(CreateSubscription(
            cancelKey,
            cancelSource,
            cancelSink,
            Visible(),
            cancelId));
        cancelSource.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await cancelSource.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(scheduler.SetVisibility(cancelId, Hidden()));
        await WaitForConditionAsync(
            () => scheduler.GetState(cancelKey).InFlightCount == 0);
        Assert.IsTrue(scheduler.Unregister(cancelId));

        clock.Advance(TimeSpan.FromSeconds(1));
        successSource.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Success,
            value: 94);
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await successSink.WaitForCountAsync(2);

        var disposeSource = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(10)))
        {
            IgnoreCancellation = true,
            ThrowOnCancellationRegistration = true
        };
        var disposeSink = new RecordingSink();
        ProviderRequestKey disposeKey = CreateKey("cancel-dispose");
        scheduler.Register(CreateSubscription(
            disposeKey,
            disposeSource,
            disposeSink,
            Visible()));
        disposeSource.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await disposeSource.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(scheduler.GetState(disposeKey).InFlightCount >= 1);

        ValueTask dispose = scheduler.DisposeAsync();
        Assert.IsFalse(dispose.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForDisposeAsync(scheduler);
    }

    [TestMethod(DisplayName =
        "UT-CARD-095 [CRD-002] Pause and resume preserve failure backoff")]
    public async Task PauseAndResumePreserveFailureBackoff()
    {
        await AssertPauseResumeBackoffAsync(
            "pause-network",
            pauseHidden: false,
            pause: (scheduler, _) => scheduler.SetNetworkState(
                ProviderNetworkState.Offline),
            resume: (scheduler, _) => scheduler.SetNetworkState(
                ProviderNetworkState.Online));
        await AssertPauseResumeBackoffAsync(
            "pause-power",
            pauseHidden: false,
            pause: (scheduler, _) => scheduler.SetPowerState(
                ProviderPowerState.Critical),
            resume: (scheduler, _) => scheduler.SetPowerState(
                ProviderPowerState.Normal));
        await AssertPauseResumeBackoffAsync(
            "pause-visibility",
            pauseHidden: true,
            pause: (scheduler, subscriptionId) => scheduler.SetVisibility(
                subscriptionId,
                Hidden()),
            resume: (scheduler, subscriptionId) => scheduler.SetVisibility(
                subscriptionId,
                Visible()));
    }

    private static ProviderRefreshSubscription CreateSubscription(
        ProviderRequestKey key,
        ScriptedSource source,
        RecordingSink sink,
        ProviderRefreshVisibility visibility,
        Guid? subscriptionId = null)
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"city\":\"Singapore\",\"units\":\"metric\"}");
        return new ProviderRefreshSubscription(
            subscriptionId ?? Guid.NewGuid(),
            key,
            document.RootElement,
            source,
            sink,
            visibility);
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Yield();
        }

        Assert.Fail("The condition did not complete within the bounded wait.");
    }

    private static async Task AssertPauseResumeBackoffAsync(
        string keyFingerprint,
        bool pauseHidden,
        Func<ProviderRefreshScheduler, Guid, bool> pause,
        Func<ProviderRefreshScheduler, Guid, bool> resume)
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(10),
            pauseHidden: pauseHidden,
            backoff: new ProviderBackoffOptions(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
                0.2)))
        {
            NextResult = request => CreateResult(
                request.RequestId,
                ProviderRefreshResultKind.Failed,
                errorCode: "provider.failed")
        };
        var sink = new RecordingSink();
        await using var scheduler = CreateScheduler(clock, () => 0.5);
        Guid subscriptionId = Guid.NewGuid();
        ProviderRequestKey key = CreateKey(keyFingerprint);
        scheduler.Register(CreateSubscription(
            key,
            source,
            sink,
            Visible(),
            subscriptionId));

        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(1);
        TimeSpan failureDue = scheduler.GetState(key).NextDueIn!.Value;
        Assert.AreEqual(TimeSpan.FromSeconds(10), failureDue);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(pause(scheduler, subscriptionId));
        Assert.IsTrue(resume(scheduler, subscriptionId));
        TimeSpan resumedDue = scheduler.GetState(key).NextDueIn!.Value;
        Assert.AreEqual(TimeSpan.FromSeconds(9), resumedDue);
        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Success,
            value: 95);
        Assert.AreEqual(0, await scheduler.PumpDueAsync());
        clock.Advance(resumedDue);
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sink.WaitForCountAsync(2);
    }

    [TestMethod(DisplayName =
        "UT-CARD-096 [CRD-005/NFR-PERF-007] Tombstone groups bound unregister and re-register orphans")]
    public async Task TombstoneGroupsBoundUnregisterAndReregisterOrphans()
    {
        var clock = new ManualClock(InitialUtc);
        var source = new ScriptedSource(CreateDescriptor(
            visibleInterval: TimeSpan.FromSeconds(1),
            requestTimeout: TimeSpan.FromSeconds(2)))
        {
            IgnoreCancellation = true
        };
        var scheduler = CreateScheduler(clock);
        ProviderRequestKey key = CreateKey("tombstone");
        Guid subscriptionId = Guid.NewGuid();
        var sinks = new List<RecordingSink>();
        sinks.Add(new RecordingSink());
        scheduler.Register(CreateSubscription(
            key,
            source,
            sinks[0],
            Visible(),
            subscriptionId));

        source.SetNextPending();
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(scheduler.Unregister(subscriptionId));

        sinks.Add(new RecordingSink());
        scheduler.Register(CreateSubscription(
            key,
            source,
            sinks[1],
            Visible(),
            subscriptionId));
        await WaitForConditionAsync(
            () => scheduler.GetState(key).InFlightCount == 1);

        source.SetNextPending();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, source.CallCount);

        for (int iteration = 0; iteration < 3; iteration++)
        {
            Assert.IsTrue(scheduler.Unregister(subscriptionId));
            var sink = new RecordingSink();
            sinks.Add(sink);
            scheduler.Register(CreateSubscription(
                key,
                source,
                sink,
                Visible(),
                subscriptionId));
            Assert.AreEqual(0, await scheduler.PumpDueAsync());
            ProviderRefreshSchedulerStateSnapshot bounded =
                scheduler.GetState(key);
            Assert.IsTrue(bounded.InFlightCount <= 2);
            Assert.IsTrue(bounded.PendingCount <= 1);
            Assert.IsTrue(source.CallCount <= 2);
        }

        source.ReleasePendingAt(
            0,
            CreateResult(
                source.Requests[0].RequestId,
                ProviderRefreshResultKind.Success,
                value: 96));
        source.ReleasePendingAt(
            0,
            CreateResult(
                source.Requests[1].RequestId,
                ProviderRefreshResultKind.Success,
                value: 97));
        await WaitForConditionAsync(
            () => scheduler.GetState(key).InFlightCount == 0);
        foreach (RecordingSink sink in sinks)
        {
            Assert.AreEqual(0, sink.Results.Count);
        }

        source.NextResult = request => CreateResult(
            request.RequestId,
            ProviderRefreshResultKind.Success,
            value: 98);
        Assert.AreEqual(1, await scheduler.PumpDueAsync());
        await sinks[^1].WaitForCountAsync(1);
        Assert.AreEqual(3, source.CallCount);
        await WaitForDisposeAsync(scheduler);
    }

    [TestMethod(DisplayName =
        "UT-CARD-097 [CRD-005] Old registration handles cannot unregister reused IDs")]
    public async Task OldRegistrationHandlesCannotUnregisterReusedIds()
    {
        var clock = new ManualClock(InitialUtc);
        await using var scheduler = CreateScheduler(clock);
        var source = new ScriptedSource(CreateDescriptor());
        ProviderRequestKey key = CreateKey("registration-handle");
        Guid subscriptionId = Guid.NewGuid();
        var oldSink = new RecordingSink();
        var newSink = new RecordingSink();

        ProviderRefreshRegistration oldHandle = scheduler.Register(
            CreateSubscription(
                key,
                source,
                oldSink,
                Visible(),
                subscriptionId));
        Assert.IsTrue(scheduler.Unregister(subscriptionId));
        ProviderRefreshRegistration newHandle = scheduler.Register(
            CreateSubscription(
                key,
                source,
                newSink,
                Visible(),
                subscriptionId));

        oldHandle.Dispose();
        Assert.AreEqual(1, scheduler.SubscriptionCount);
        Assert.IsTrue(scheduler.SetVisibility(subscriptionId, Hidden()));
        newHandle.Dispose();
        Assert.AreEqual(0, scheduler.SubscriptionCount);
    }

    [TestMethod(DisplayName =
        "UT-CARD-098 [CRD-005] Process-fatal exception classification is not normalized")]
    public void ProcessFatalExceptionClassificationIsNotNormalized()
    {
        MethodInfo classifier = typeof(ProviderRefreshScheduler).GetMethod(
            "IsProcessFatal",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Exception fatal = (Exception)Activator.CreateInstance(
            typeof(AccessViolationException),
            "test")!;

        Assert.IsTrue((bool)classifier.Invoke(
            null,
            [fatal])!);
        Assert.IsFalse((bool)classifier.Invoke(
            null,
            [new InvalidOperationException()])!);
    }

    private static Task WaitForDisposeAsync(
        ProviderRefreshScheduler scheduler) =>
        scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

    private static ProviderRefreshScheduler CreateScheduler(
        IProviderRefreshClock clock,
        Func<double>? jitter = null,
        TimeSpan? disposeTimeout = null)
    {
        var scheduler = new ProviderRefreshScheduler(
            clock,
            jitter,
            disposeTimeout);
        scheduler.SetNetworkState(ProviderNetworkState.Online);
        scheduler.SetPowerState(ProviderPowerState.Normal);
        return scheduler;
    }

    private static ProviderRequestKey CreateKey(string fingerprint) => new(
        "app.test.provider",
        "test.current",
        "weather:singapore",
        fingerprint);

    private static ProviderRefreshVisibility Visible() => new(
        PanelVisible: true,
        InViewport: true,
        DisplayConnected: true);

    private static ProviderRefreshVisibility Hidden() => new(
        PanelVisible: false,
        InViewport: false,
        DisplayConnected: true);

    private static ScheduledProviderDescriptor CreateDescriptor(
        TimeSpan? visibleInterval = null,
        TimeSpan? hiddenInterval = null,
        bool pauseHidden = false,
        TimeSpan? powerSaverInterval = null,
        TimeSpan? requestTimeout = null,
        bool requiresNetwork = true,
        bool supportsManualRefresh = false,
        TimeSpan? manualMinimumInterval = null,
        ProviderBackoffOptions? backoff = null) =>
        new(
            "app.test.provider",
            "test.current",
            TimeSpan.FromSeconds(1),
            visibleInterval ?? TimeSpan.FromSeconds(10),
            pauseHidden ? null : hiddenInterval ?? TimeSpan.FromSeconds(20),
            powerSaverInterval ?? TimeSpan.FromSeconds(30),
            requestTimeout ?? TimeSpan.FromSeconds(5),
            requiresNetwork,
            supportsManualRefresh,
            manualMinimumInterval ?? TimeSpan.FromSeconds(30),
            backoff ?? new ProviderBackoffOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMinutes(5),
                0.2));

    private static ProviderRefreshResult CreateResult(
        Guid requestId,
        ProviderRefreshResultKind kind,
        int value = 1,
        string? errorCode = null,
        TimeSpan? retryAfter = null) =>
        new(
            requestId,
            kind,
            JsonSerializer.SerializeToElement(new { value }),
            InitialUtc,
            errorCode: errorCode,
            retryAfter: retryAfter);

    private sealed class ManualClock : IProviderRefreshClock
    {
        private readonly object _gate = new();
        private readonly List<(TimeSpan Due, TaskCompletionSource<object?> Completion)> _delays = [];
        private DateTimeOffset _utcNow;
        private TimeSpan _monotonicNow;

        public ManualClock(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_gate)
                {
                    return _utcNow;
                }
            }
        }

        public TimeSpan MonotonicNow
        {
            get
            {
                lock (_gate)
                {
                    return _monotonicNow;
                }
            }
        }

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero)
            {
                return ValueTask.CompletedTask;
            }

            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _delays.Add((_monotonicNow + delay, completion));
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(
                    static state => ((TaskCompletionSource<object?>)state!)
                        .TrySetCanceled(),
                    completion);
            }

            return new ValueTask(completion.Task);
        }

        public void Advance(TimeSpan delta)
        {
            List<TaskCompletionSource<object?>> ready;
            lock (_gate)
            {
                _monotonicNow += delta;
                _utcNow = _utcNow.Add(delta);
                ready = _delays
                    .Where(entry => entry.Due <= _monotonicNow)
                    .Select(entry => entry.Completion)
                    .ToList();
                _delays.RemoveAll(entry => entry.Due <= _monotonicNow);
            }

            foreach (TaskCompletionSource<object?> completion in ready)
            {
                completion.TrySetResult(null);
            }
        }

        public void SetUtc(DateTimeOffset value)
        {
            lock (_gate)
            {
                _utcNow = value;
            }
        }
    }

    private sealed class ReentrantClock : IProviderRefreshClock
    {
        private readonly ManualClock _inner;
        private readonly Action _onRead;
        private int _reentryCount;

        public ReentrantClock(ManualClock inner, Action onRead)
        {
            _inner = inner;
            _onRead = onRead;
        }

        public int ReentryCount => Volatile.Read(ref _reentryCount);

        public DateTimeOffset UtcNow
        {
            get
            {
                Reenter();
                return _inner.UtcNow;
            }
        }

        public TimeSpan MonotonicNow
        {
            get
            {
                Reenter();
                return _inner.MonotonicNow;
            }
        }

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            _inner.DelayAsync(delay, cancellationToken);

        private void Reenter()
        {
            if (Interlocked.Exchange(ref _reentryCount, 1) == 0)
            {
                _onRead();
            }
        }
    }

    private sealed class ScriptedSource : IProviderRefreshSource
    {
        private readonly object _gate = new();
        private readonly List<PendingRequest> _pendingRequests = [];
        private TaskCompletionSource<object?> _pendingChanged = NewSignal();
        private readonly ScheduledProviderDescriptor _descriptor;
        private TaskCompletionSource<ProviderRefreshResult>? _pending;
        private TaskCompletionSource<object?>? _completed;

        public ScriptedSource(ScheduledProviderDescriptor descriptor)
        {
            _descriptor = descriptor;
            Key = CreateKey("args-a");
            NextResult = request => CreateResult(
                request.RequestId,
                ProviderRefreshResultKind.Success,
                value: 1);
        }

        public ScheduledProviderDescriptor Descriptor
        {
            get
            {
                DescriptorReadCallback?.Invoke();
                DescriptorReadCount++;
                return _descriptor;
            }
        }

        public Action? DescriptorReadCallback { get; init; }

        public int DescriptorReadCount { get; private set; }

        public ProviderRequestKey Key { get; }

        public int CallCount { get; private set; }

        public ProviderRefreshRequest? LastRequest { get; private set; }

        public List<ProviderRefreshRequest> Requests { get; } = [];

        public bool IgnoreCancellation { get; init; }

        public bool ThrowOnCancellationRegistration { get; init; }

        public Func<ProviderRefreshRequest, ProviderRefreshResult>? NextResult
        {
            get;
            set;
        }

        public TaskCompletionSource<object?> Started { get; private set; } =
            NewSignal();

        public TaskCompletionSource<object?> Completed { get; private set; } =
            NewSignal();

        public ValueTask<ProviderRefreshResult> FetchAsync(
            ProviderRefreshRequest request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                CallCount++;
                LastRequest = request;
                Requests.Add(request);
                Started.TrySetResult(null);
                if (ThrowOnCancellationRegistration)
                {
                    cancellationToken.Register(
                        static _ => throw new InvalidOperationException(
                            "throwing cancellation callback"),
                        null);
                }

                if (NextResult is not null)
                {
                    ProviderRefreshResult result = NextResult(request);
                    Completed.TrySetResult(null);
                    return ValueTask.FromResult(result);
                }

                _completed = Completed;
                TaskCompletionSource<ProviderRefreshResult> pending =
                    _pending ??= NewResultSource();
                _pending = null;
                _pendingRequests.Add(new PendingRequest(pending, Completed));
                _pendingChanged.TrySetResult(null);

                if (IgnoreCancellation)
                {
                    return new ValueTask<ProviderRefreshResult>(
                        pending.Task);
                }

                return new ValueTask<ProviderRefreshResult>(
                    pending.Task.WaitAsync(cancellationToken));
            }
        }

        public void SetNextPending()
        {
            lock (_gate)
            {
                _pending = NewResultSource();
                Started = NewSignal();
                Completed = NewSignal();
                NextResult = null;
            }
        }

        public void ReleasePending(ProviderRefreshResult result)
        {
            ReleasePendingAt(-1, result);
        }

        public void ReleasePendingAt(
            int index,
            ProviderRefreshResult result)
        {
            lock (_gate)
            {
                if (_pendingRequests.Count == 0)
                {
                    throw new InvalidOperationException("No pending request.");
                }

                int actualIndex = index < 0
                    ? _pendingRequests.Count - 1
                    : index;
                PendingRequest pending = _pendingRequests[actualIndex];
                _pendingRequests.RemoveAt(actualIndex);
                pending.Result.TrySetResult(result);
                pending.Completed.TrySetResult(null);
                _pendingChanged.TrySetResult(null);
            }
        }

        public void ReleaseException(Exception exception)
        {
            lock (_gate)
            {
                if (_pendingRequests.Count == 0)
                {
                    throw new InvalidOperationException("No pending request.");
                }

                PendingRequest pending = _pendingRequests[^1];
                _pendingRequests.RemoveAt(_pendingRequests.Count - 1);
                pending.Result.TrySetException(exception);
                pending.Completed.TrySetResult(null);
                _pendingChanged.TrySetResult(null);
            }
        }

        public async Task WaitForPendingCountAsync(int count)
        {
            while (true)
            {
                Task waitTask;
                lock (_gate)
                {
                    if (_pendingRequests.Count <= count)
                    {
                        return;
                    }

                    waitTask = _pendingChanged.Task;
                }

                await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
                lock (_gate)
                {
                    if (_pendingChanged.Task.IsCompleted)
                    {
                        _pendingChanged = NewSignal();
                    }
                }
            }
        }

        private sealed record PendingRequest(
            TaskCompletionSource<ProviderRefreshResult> Result,
            TaskCompletionSource<object?> Completed);

        private static TaskCompletionSource<ProviderRefreshResult> NewResultSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource<object?> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingSink : IProviderRefreshSink
    {
        private readonly object _gate = new();

        public List<ProviderRefreshResult> Results { get; } = [];

        public TaskCompletionSource<ProviderRefreshResult> FirstResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ThrowOnApply { get; init; }

        public ValueTask ApplyAsync(
            ProviderRefreshRequest request,
            ProviderRefreshResult result,
            CancellationToken cancellationToken)
        {
            if (ThrowOnApply)
            {
                throw new InvalidOperationException("sink failed");
            }

            lock (_gate)
            {
                Results.Add(result);
                FirstResult.TrySetResult(result);
                _changed.TrySetResult(null);
            }

            return ValueTask.CompletedTask;
        }

        public async Task WaitForCountAsync(int count)
        {
            while (true)
            {
                Task waitTask;
                lock (_gate)
                {
                    if (Results.Count >= count)
                    {
                        return;
                    }

                    waitTask = _changed.Task;
                }

                await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
                lock (_gate)
                {
                    if (_changed.Task.IsCompleted)
                    {
                        _changed = NewSignal();
                    }
                }
            }
        }

        private TaskCompletionSource<object?> _changed = NewSignal();

        private static TaskCompletionSource<object?> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
