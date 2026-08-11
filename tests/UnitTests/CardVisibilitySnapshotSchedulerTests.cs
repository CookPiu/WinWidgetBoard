using System.Text.Json;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardVisibilitySnapshotSchedulerTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName = "UT-CARD-014 [CRD-003/NFR-PERF-007] Panel hidden registers without refresh work")]
    public async Task PanelHiddenRegistersWithoutRefreshWork()
    {
        using CardRuntimeInstance runtime = CreateRuntime("hidden-panel");
        int calls = 0;
        await using var scheduler = new CardRuntimeVisibilityScheduler();

        scheduler.Register(
            runtime,
            _ =>
            {
                calls++;
                return ValueTask.FromResult(CreateSnapshot(runtime, 1));
            },
            isInViewport: true);

        Assert.AreEqual(CardLifecycleState.Hidden, runtime.LifecycleState);
        Assert.AreEqual(0, calls);
    }

    [TestMethod(DisplayName = "UT-CARD-015 [CRD-003] Viewport hidden does not refresh while panel is visible")]
    public async Task ViewportHiddenDoesNotRefreshWhilePanelIsVisible()
    {
        using CardRuntimeInstance runtime = CreateRuntime("hidden-viewport");
        int calls = 0;
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);

        scheduler.Register(
            runtime,
            _ =>
            {
                calls++;
                return ValueTask.FromResult(CreateSnapshot(runtime, 1));
            },
            isInViewport: false);

        Assert.AreEqual(CardLifecycleState.Hidden, runtime.LifecycleState);
        Assert.AreEqual(0, calls);
    }

    [TestMethod(DisplayName = "UT-CARD-016 [CRD-003] First visible transition requests one immediate snapshot")]
    public async Task FirstVisibleTransitionRequestsOneImmediateSnapshot()
    {
        using CardRuntimeInstance runtime = CreateRuntime("first-visible");
        int calls = 0;
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);

        scheduler.Register(
            runtime,
            _ =>
            {
                calls++;
                return ValueTask.FromResult(CreateSnapshot(runtime, 1));
            },
            isInViewport: true);

        await WaitForAsync(() => runtime.Snapshot.Sequence == 1);

        Assert.AreEqual(CardLifecycleState.Visible, runtime.LifecycleState);
        Assert.AreEqual(1, calls);
    }

    [TestMethod(DisplayName = "UT-CARD-017 [CRD-003] Repeated visible signals do not duplicate refresh")]
    public async Task RepeatedVisibleSignalsDoNotDuplicateRefresh()
    {
        using CardRuntimeInstance runtime = CreateRuntime("visible-repeat");
        int calls = 0;
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);
        scheduler.Register(
            runtime,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(CreateSnapshot(
                    runtime,
                    runtime.Snapshot.Sequence + 1));
            },
            isInViewport: false);

        scheduler.SetViewportVisibility(runtime, true);
        scheduler.SetViewportVisibility(runtime, true);
        scheduler.SetViewportVisibility(runtime, true);
        await WaitForAsync(() => Volatile.Read(ref calls) == 1);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, runtime.Snapshot.Sequence);
    }

    [TestMethod(DisplayName = "UT-CARD-018 [CRD-003] Hidden then visible refreshes exactly once again")]
    public async Task HiddenThenVisibleRefreshesExactlyOnceAgain()
    {
        using CardRuntimeInstance runtime = CreateRuntime("hide-show");
        int calls = 0;
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);
        scheduler.Register(
            runtime,
            _ =>
            {
                int sequence = Interlocked.Increment(ref calls);
                return ValueTask.FromResult(CreateSnapshot(runtime, sequence));
            },
            isInViewport: true);
        await WaitForAsync(() => Volatile.Read(ref calls) == 1);

        scheduler.SetPanelVisibility(false);
        scheduler.SetPanelVisibility(true);
        await WaitForAsync(() => Volatile.Read(ref calls) == 2);

        scheduler.SetPanelVisibility(true);
        scheduler.SetViewportVisibility(runtime, true);
        await Task.Delay(25);

        Assert.AreEqual(2, calls);
        Assert.AreEqual(2, runtime.Snapshot.Sequence);
    }

    [TestMethod(DisplayName = "UT-CARD-019 [CRD-003/CRD-005] Hidden cancels in-flight refresh and rejects a late snapshot")]
    public async Task HiddenCancelsInFlightRefreshAndRejectsLateSnapshot()
    {
        using CardRuntimeInstance runtime = CreateRuntime("late-snapshot");
        var providerStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerRelease = new TaskCompletionSource<CardRuntimeSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);

        scheduler.Register(
            runtime,
            token =>
            {
                providerToken = token;
                providerStarted.SetResult(null);
                return new ValueTask<CardRuntimeSnapshot>(
                    providerRelease.Task.WaitAsync(token));
            },
            isInViewport: true);

        await providerStarted.Task;
        scheduler.SetPanelVisibility(false);
        Assert.IsTrue(providerToken.IsCancellationRequested);

        providerRelease.SetResult(CreateSnapshot(runtime, 1));
        await Task.Delay(25);

        Assert.AreEqual(0, runtime.Snapshot.Sequence);
        Assert.AreEqual(CardRuntimeStatus.Ready, runtime.Snapshot.Status);
        Assert.AreEqual(CardLifecycleState.Hidden, runtime.LifecycleState);
    }

    [TestMethod(DisplayName = "UT-CARD-020 [CRD-003/CRD-005] Generation gate rejects a result racing with hide")]
    public async Task GenerationGateRejectsResultRacingWithHide()
    {
        using CardRuntimeInstance runtime = CreateRuntime("generation-race");
        var providerStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerRelease = new TaskCompletionSource<CardRuntimeSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);
        scheduler.Register(
            runtime,
            _ =>
            {
                providerStarted.SetResult(null);
                return new ValueTask<CardRuntimeSnapshot>(providerRelease.Task);
            },
            isInViewport: true);

        await providerStarted.Task;
        scheduler.SetViewportVisibility(runtime, false);
        providerRelease.SetResult(CreateSnapshot(runtime, 1));
        await Task.Delay(25);

        Assert.AreEqual(0, runtime.Snapshot.Sequence);
        Assert.AreEqual(CardRuntimeStatus.Ready, runtime.Snapshot.Status);
    }

    [TestMethod(DisplayName = "UT-CARD-021 [CRD-005] Provider failure is isolated by the scheduler")]
    public async Task ProviderFailureIsIsolatedByTheScheduler()
    {
        using CardRuntimeInstance failing = CreateRuntime("scheduler-failing");
        using CardRuntimeInstance healthy = CreateRuntime("scheduler-healthy");
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);
        scheduler.Register(
            failing,
            _ => ValueTask.FromException<CardRuntimeSnapshot>(
                new InvalidOperationException("provider failed")),
            isInViewport: true);
        scheduler.Register(
            healthy,
            _ => ValueTask.FromResult(CreateSnapshot(
                healthy,
                healthy.Snapshot.Sequence + 1)),
            isInViewport: true);

        await WaitForAsync(() =>
            failing.Snapshot.Status == CardRuntimeStatus.Error &&
            healthy.Snapshot.Sequence == 1);

        Assert.AreEqual(CardRuntimeStatus.Error, failing.Snapshot.Status);
        Assert.AreEqual(CardRuntimeStatus.Ready, healthy.Snapshot.Status);
        Assert.AreEqual(1, healthy.Snapshot.Sequence);
    }

    [TestMethod(DisplayName = "UT-CARD-022 [CRD-003] Dynamic unregister and instance reuse reject stale runtime signals")]
    public async Task DynamicUnregisterAndInstanceReuseRejectStaleRuntimeSignals()
    {
        using CardRuntimeInstance oldRuntime = CreateRuntime("reused-card");
        using CardRuntimeInstance newRuntime = CreateRuntime("reused-card");
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);
        CardRuntimeRegistration oldRegistration = scheduler.Register(
            oldRuntime,
            _ => ValueTask.FromResult(CreateSnapshot(oldRuntime, 1)),
            isInViewport: true);
        oldRegistration.Dispose();

        scheduler.Register(
            newRuntime,
            _ => ValueTask.FromResult(CreateSnapshot(newRuntime, 1)),
            isInViewport: false);

        Assert.IsFalse(scheduler.SetViewportVisibility(oldRuntime, true));
        Assert.AreEqual(CardLifecycleState.Hidden, newRuntime.LifecycleState);
        Assert.AreEqual(0, newRuntime.Snapshot.Sequence);

        scheduler.SetViewportVisibility(newRuntime, true);
        await WaitForAsync(() => newRuntime.Snapshot.Sequence == 1);
        Assert.AreEqual(CardLifecycleState.Visible, newRuntime.LifecycleState);
    }

    [TestMethod(DisplayName = "UT-CARD-023 [CRD-003] Dispose is bounded and cannot apply late work")]
    public async Task DisposeIsBoundedAndCannotApplyLateWork()
    {
        using CardRuntimeInstance runtime = CreateRuntime("bounded-dispose");
        var providerRelease = new TaskCompletionSource<CardRuntimeSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true,
            disposeTimeout: TimeSpan.FromMilliseconds(30));
        scheduler.Register(
            runtime,
            _ => new ValueTask<CardRuntimeSnapshot>(providerRelease.Task),
            isInViewport: true);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await scheduler.DisposeAsync();
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        providerRelease.SetResult(CreateSnapshot(runtime, 1));
        await Task.Delay(25);
        Assert.AreEqual(0, runtime.Snapshot.Sequence);
        Assert.AreEqual(CardLifecycleState.Disposed, runtime.LifecycleState);
    }

    [TestMethod(DisplayName = "UT-CARD-028 [CRD-003/CRD-005] Late provider error after hide is rejected by the same generation gate")]
    public async Task LateProviderErrorAfterHideIsRejectedByTheSameGenerationGate()
    {
        using CardRuntimeInstance runtime = CreateRuntime("late-error");
        var providerStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerRelease = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);
        scheduler.Register(
            runtime,
            _ =>
            {
                providerStarted.SetResult(null);
                return new ValueTask<CardRuntimeSnapshot>(
                    providerRelease.Task.ContinueWith<CardRuntimeSnapshot>(
                        _ => throw new InvalidOperationException("late failure"),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default));
            },
            isInViewport: true);

        await providerStarted.Task;
        scheduler.SetPanelVisibility(false);
        providerRelease.SetResult(null);
        await Task.Delay(25);

        Assert.AreEqual(CardRuntimeStatus.Ready, runtime.Snapshot.Status);
        Assert.AreEqual(0, runtime.Snapshot.Sequence);
    }

    [TestMethod(DisplayName = "UT-CARD-029 [CRD-003] Hide-show with an uncooperative provider does not enqueue duplicate visible refreshes")]
    public async Task HideShowWithUncooperativeProviderDoesNotEnqueueDuplicateVisibleRefreshes()
    {
        using CardRuntimeInstance runtime = CreateRuntime("bounded-refreshes");
        var releases = new List<TaskCompletionSource<CardRuntimeSnapshot>>();
        int calls = 0;
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);
        scheduler.Register(
            runtime,
            _ =>
            {
                int call = Interlocked.Increment(ref calls);
                var release = new TaskCompletionSource<CardRuntimeSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (releases)
                {
                    releases.Add(release);
                }

                return new ValueTask<CardRuntimeSnapshot>(release.Task);
            },
            isInViewport: true);

        await WaitForAsync(() => Volatile.Read(ref calls) == 1);
        scheduler.SetPanelVisibility(false);
        scheduler.SetPanelVisibility(true);
        await WaitForAsync(() => Volatile.Read(ref calls) == 2);

        TaskCompletionSource<CardRuntimeSnapshot> first;
        TaskCompletionSource<CardRuntimeSnapshot> second;
        lock (releases)
        {
            first = releases[0];
            second = releases[1];
        }

        first.SetResult(CreateSnapshot(runtime, 1));
        await Task.Delay(25);
        Assert.AreEqual(2, calls);

        second.SetResult(CreateSnapshot(runtime, 1));
        await WaitForAsync(() => runtime.Snapshot.Sequence == 1);
        await Task.Delay(25);
        Assert.AreEqual(2, calls);
    }

    [TestMethod(DisplayName = "UT-CARD-030 [CRD-003] External runtime disposal removes registration and cancels provider work")]
    public async Task ExternalRuntimeDisposalRemovesRegistrationAndCancelsProviderWork()
    {
        using CardRuntimeInstance runtime = CreateRuntime("external-dispose");
        var providerStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerRelease = new TaskCompletionSource<CardRuntimeSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        await using var scheduler = new CardRuntimeVisibilityScheduler(
            panelVisible: true);
        scheduler.Register(
            runtime,
            token =>
            {
                providerToken = token;
                providerStarted.SetResult(null);
                return new ValueTask<CardRuntimeSnapshot>(
                    providerRelease.Task);
            },
            isInViewport: true);

        await providerStarted.Task;
        runtime.Dispose();
        scheduler.SetViewportVisibility(runtime, false);

        await WaitForAsync(() => providerToken.IsCancellationRequested);
        Assert.AreEqual(0, scheduler.RegistrationCount);

        providerRelease.SetResult(CreateSnapshot(runtime, 1));
        await Task.Delay(25);
        Assert.AreEqual(0, runtime.Snapshot.Sequence);
    }

    private static CardRuntimeInstance CreateRuntime(string instanceId)
    {
        var definition = new CardDefinition(
            "test.scheduler-card",
            "TestSchedulerCardTitle.Text",
            CardSize.M,
            [CardSize.M]);
        return new CardRuntimeInstance(
            definition,
            instanceId,
            CreateSnapshot(instanceId, 0));
    }

    private static CardRuntimeSnapshot CreateSnapshot(
        CardRuntimeInstance runtime,
        long sequence) =>
        CreateSnapshot(runtime.InstanceId, sequence);

    private static CardRuntimeSnapshot CreateSnapshot(
        string instanceId,
        long sequence) =>
        new(
            instanceId,
            "test.scheduler-card",
            schemaVersion: 1,
            sequence,
            FixedTimestamp.AddTicks(sequence),
            CardRuntimeFreshness.Fresh,
            CardRuntimeStatus.Ready,
            JsonSerializer.SerializeToElement(
                new Dictionary<string, long>
                {
                    ["sequence"] = sequence,
                }));

    private static async Task WaitForAsync(
        Func<bool> condition,
        int timeoutMilliseconds = 1000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(
            timeoutMilliseconds);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for scheduler work.");
            }

            await Task.Delay(10);
        }
    }
}
