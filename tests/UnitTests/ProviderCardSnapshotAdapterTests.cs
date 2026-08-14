using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class ProviderCardSnapshotAdapterTests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName =
        "UT-CARD-099 [CRD-001/CRD-002] " +
        "provider success maps to a fresh or stale card snapshot")]
    public async Task SuccessMapsToFreshOrStaleSnapshot()
    {
        var publisher = new RecordingPublisher();
        var adapter = new ProviderCardSnapshotAdapter(
            "demo.weather",
            "builtin.weather",
            schemaVersion: 1,
            publisher,
            readyActions: [CardsContract.RefreshActionId],
            failureActions:
            [
                "retry",
                CardsContract.OpenDiagnosticsActionId,
            ],
            utcNow: () => NowUtc);
        ProviderRefreshRequest request = CreateRequest();

        await adapter.ApplyAsync(
            request,
            CreateResult(
                request.RequestId,
                ProviderRefreshResultKind.Success,
                NowUtc.AddMinutes(5)),
            CancellationToken.None);
        await adapter.ApplyAsync(
            request,
            CreateResult(
                request.RequestId,
                ProviderRefreshResultKind.Success,
                NowUtc.AddMinutes(-1)),
            CancellationToken.None);

        Assert.AreEqual(2, publisher.Snapshots.Count);
        Assert.AreEqual(CardSnapshotStatus.Ready, publisher.Snapshots[0].Status);
        Assert.AreEqual(
            CardSnapshotFreshness.Fresh,
            publisher.Snapshots[0].Freshness);
        Assert.AreEqual(
            CardSnapshotFreshness.Stale,
            publisher.Snapshots[1].Freshness);
        Assert.AreEqual(1L, publisher.Snapshots[0].Sequence);
        Assert.AreEqual(2L, publisher.Snapshots[1].Sequence);
        CollectionAssert.AreEqual(
            new[] { CardsContract.RefreshActionId },
            publisher.Snapshots[0].AllowedActions.ToArray());
    }

    [TestMethod(DisplayName =
        "UT-CARD-100 [CRD-001/CRD-002/CRD-005] " +
        "provider failure preserves diagnostic and bounded action mapping")]
    public async Task FailureMapsToErrorWithDiagnosticAndFailureActions()
    {
        var publisher = new RecordingPublisher();
        var adapter = new ProviderCardSnapshotAdapter(
            "demo.weather",
            "builtin.weather",
            schemaVersion: 1,
            publisher,
            readyActions: [CardsContract.RefreshActionId],
            failureActions:
            [
                "retry",
                CardsContract.OpenDiagnosticsActionId,
                CardsContract.DisableActionId,
            ],
            utcNow: () => NowUtc);
        ProviderRefreshRequest request = CreateRequest();

        using (JsonDocument payload = JsonDocument.Parse(
                   "{\"temperature\":31.2}"))
        {
            await adapter.ApplyAsync(
                request,
                new ProviderRefreshResult(
                    request.RequestId,
                    ProviderRefreshResultKind.TimedOut,
                    payload.RootElement,
                    NowUtc,
                    errorCode: "provider.timeout"),
                CancellationToken.None);
        }

        CardStateSnapshot snapshot = publisher.Snapshots.Single();
        Assert.AreEqual(CardSnapshotStatus.Error, snapshot.Status);
        Assert.AreEqual(CardSnapshotFreshness.Stale, snapshot.Freshness);
        Assert.AreEqual("provider.timeout", snapshot.DiagnosticCode);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "retry",
                CardsContract.OpenDiagnosticsActionId,
                CardsContract.DisableActionId,
            },
            snapshot.AllowedActions.ToArray());
        Assert.AreEqual(
            31.2,
            snapshot.Payload.GetProperty("temperature").GetDouble(),
            0.0001);
    }

    [TestMethod(DisplayName =
        "UT-CARD-101 [CRD-002/CRD-005/NFR-PERF-007] " +
        "snapshot event buffer coalesces by instance and marks overflow")]
    public async Task SnapshotBufferCoalescesAndMarksOverflow()
    {
        var buffer = new CardSnapshotEventBuffer(capacity: 2);
        await buffer.PublishAsync(CreateSnapshot("card-a", 1), CancellationToken.None);
        await buffer.PublishAsync(CreateSnapshot("card-a", 2), CancellationToken.None);
        await buffer.PublishAsync(CreateSnapshot("card-b", 1), CancellationToken.None);
        await buffer.PublishAsync(CreateSnapshot("card-c", 1), CancellationToken.None);

        Assert.IsTrue(buffer.IsOverflowed);
        Assert.AreEqual(2, buffer.PendingCount);
        IReadOnlyList<CardStateSnapshot> drained = buffer.Drain(10);
        Assert.AreEqual(2, drained.Count);
        Assert.AreEqual("card-a", drained[0].InstanceId);
        Assert.AreEqual(2L, drained[0].Sequence);
        Assert.AreEqual("card-b", drained[1].InstanceId);
        Assert.AreEqual(0, buffer.PendingCount);
    }

    private static ProviderRefreshRequest CreateRequest()
    {
        ProviderRequestKey key = new(
            "app.test.provider",
            "test.current",
            "weather:singapore",
            "sha256:arguments");
        return new ProviderRefreshRequest(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            key,
            NowUtc.AddSeconds(5),
            JsonSerializer.SerializeToElement(new { units = "metric" }),
            generation: 1);
    }

    private static ProviderRefreshResult CreateResult(
        Guid requestId,
        ProviderRefreshResultKind kind,
        DateTimeOffset? validUntilUtc,
        string? errorCode = null) =>
        new(
            requestId,
            kind,
            JsonSerializer.SerializeToElement(new { temperature = 31.2 }),
            NowUtc,
            validUntilUtc,
            errorCode);

    private static CardStateSnapshot CreateSnapshot(
        string instanceId,
        long sequence) =>
        new()
        {
            InstanceId = instanceId,
            CardTypeId = "builtin.test",
            SchemaVersion = 1,
            Sequence = sequence,
            GeneratedAtUtc = NowUtc,
            Status = CardSnapshotStatus.Ready,
            Freshness = CardSnapshotFreshness.Fresh,
            Payload = JsonSerializer.SerializeToElement(new { value = sequence }),
        };

    private sealed class RecordingPublisher : ICardSnapshotPublisher
    {
        public List<CardStateSnapshot> Snapshots { get; } = [];

        public ValueTask PublishAsync(
            CardStateSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshots.Add(snapshot);
            return ValueTask.CompletedTask;
        }
    }
}
