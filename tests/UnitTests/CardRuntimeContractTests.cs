using System.ComponentModel;
using System.Text.Json;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardRuntimeContractTests
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    [TestMethod(DisplayName = "UT-CARD-001 [LYT-002] Definition validates and owns its size contract")]
    public void DefinitionValidatesAndOwnsItsSizeContract()
    {
        var sourceSizes = new List<CardSize>
        {
            CardSize.M,
            CardSize.L,
        };
        var definition = new CardDefinition(
            "test.card",
            "TestCardTitle.Text",
            CardSize.M,
            sourceSizes);

        sourceSizes.Clear();

        Assert.AreEqual("test.card", definition.CardTypeId);
        Assert.IsTrue(definition.SupportsSize(CardSize.M));
        Assert.IsTrue(definition.SupportsSize(CardSize.L));
        Assert.IsFalse(definition.SupportsSize(CardSize.S));
        Assert.AreEqual(2, definition.SupportedSizes.Count);
        Assert.ThrowsExactly<ArgumentException>(() => new CardDefinition(
            "test.duplicate",
            "TestCardTitle.Text",
            CardSize.M,
            [CardSize.M, CardSize.M]));
        Assert.ThrowsExactly<ArgumentException>(() => new CardDefinition(
            "test.unsupported-default",
            "TestCardTitle.Text",
            CardSize.M,
            [CardSize.L]));
        Assert.ThrowsExactly<ArgumentException>(() => new CardDefinition(
            "invalid card",
            "TestCardTitle.Text",
            CardSize.M,
            [CardSize.M]));
    }

    [TestMethod(DisplayName = "UT-CARD-002 [CRD-001] Catalog resolves known and unknown instances safely")]
    public void CatalogResolvesKnownAndUnknownInstancesSafely()
    {
        Assert.AreSame(
            BuiltInCardCatalog.Notes,
            BuiltInCardCatalog.ResolveInstance(
                BuiltInCardCatalog.NotesInstanceId));
        Assert.AreSame(
            BuiltInCardCatalog.Unknown,
            BuiltInCardCatalog.ResolveInstance("custom.weather"));
        Assert.AreEqual(
            BuiltInCardCatalog.All.Count,
            BuiltInCardCatalog.All
                .Select(definition => definition.CardTypeId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.AreEqual(
            "UnknownCardTitle.Text",
            BuiltInCardCatalog.Unknown.TitleResourceKey);
    }

    [TestMethod(DisplayName = "UT-CARD-003 [CRD-001/002] Snapshot owns payload and action inputs")]
    public void SnapshotOwnsPayloadAndActionInputs()
    {
        var actions = new List<string>
        {
            CardRuntimeActionIds.Refresh,
        };
        CardRuntimeSnapshot snapshot;
        using (JsonDocument document = JsonDocument.Parse(
            """{"value":42}"""))
        {
            snapshot = CreateSnapshot(
                sequence: 3,
                payload: document.RootElement,
                actions: actions,
                timestamp: new DateTimeOffset(
                    2026,
                    8,
                    11,
                    16,
                    0,
                    0,
                    TimeSpan.FromHours(8)));
        }

        actions.Clear();

        Assert.AreEqual(42, snapshot.Payload.GetProperty("value").GetInt32());
        Assert.IsTrue(
            snapshot.AllowsAction(CardRuntimeActionIds.Refresh));
        Assert.AreEqual(1, snapshot.AllowedActionIds.Count);
        Assert.AreEqual(TimeSpan.Zero, snapshot.TimestampUtc.Offset);
        Assert.AreEqual(8, snapshot.TimestampUtc.Hour);
    }

    [TestMethod(DisplayName = "UT-CARD-004 [CRD-001] Unknown state removes inferred actions")]
    public void UnknownStateRemovesInferredActions()
    {
        CardRuntimeSnapshot snapshot = CreateSnapshot(
            status: (CardRuntimeStatus)999,
            freshness: (CardRuntimeFreshness)999,
            actions:
            [
                CardRuntimeActionIds.Edit,
                CardRuntimeActionIds.Refresh,
            ]);

        Assert.AreEqual(CardRuntimeStatus.Unknown, snapshot.Status);
        Assert.AreEqual(
            CardRuntimeFreshness.Unknown,
            snapshot.Freshness);
        Assert.AreEqual(0, snapshot.AllowedActionIds.Count);
        Assert.IsFalse(snapshot.AllowsAction(CardRuntimeActionIds.Edit));
        Assert.AreEqual(
            CardRuntimeErrorCodes.UnknownStatus,
            snapshot.ErrorCode);
    }

    [TestMethod(DisplayName = "UT-CARD-005 [CRD-002/005] Error snapshot preserves last payload")]
    public void ErrorSnapshotPreservesLastPayload()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new Dictionary<string, int>
            {
                ["value"] = 7,
            });
        CardRuntimeSnapshot ready = CreateSnapshot(
            sequence: 4,
            payload: payload);

        CardRuntimeSnapshot error = CardRuntimeSnapshot.CreateError(
            ready,
            "provider.timeout",
            FixedTimestamp.AddMinutes(1));

        Assert.AreEqual(5, error.Sequence);
        Assert.AreEqual(CardRuntimeStatus.Error, error.Status);
        Assert.AreEqual(CardRuntimeFreshness.Stale, error.Freshness);
        Assert.AreEqual(7, error.Payload.GetProperty("value").GetInt32());
        Assert.AreEqual("provider.timeout", error.ErrorCode);
        Assert.IsTrue(error.AllowsAction(CardRuntimeActionIds.Retry));
        Assert.IsTrue(
            error.AllowsAction(CardRuntimeActionIds.OpenDiagnostics));
        Assert.IsTrue(error.AllowsAction(CardRuntimeActionIds.Disable));
    }

    [TestMethod(DisplayName = "UT-CARD-006 [CRD-001] Lifecycle accepts only declared transitions")]
    public void LifecycleAcceptsOnlyDeclaredTransitions()
    {
        using CardRuntimeInstance invalidRuntime = CreateRuntime(
            "test.invalid-lifecycle");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            invalidRuntime.TransitionTo(CardLifecycleState.Visible));

        var runtime = CreateRuntime("test.lifecycle");
        Assert.IsTrue(runtime.Initialize());
        Assert.IsFalse(runtime.Initialize());
        Assert.IsTrue(runtime.TransitionTo(CardLifecycleState.Visible));
        Assert.IsTrue(runtime.TransitionTo(CardLifecycleState.Hidden));
        Assert.IsTrue(runtime.TransitionTo(CardLifecycleState.Suspended));
        Assert.IsTrue(runtime.TransitionTo(CardLifecycleState.Visible));

        runtime.Dispose();

        Assert.AreEqual(
            CardLifecycleState.Disposed,
            runtime.LifecycleState);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            runtime.TransitionTo(CardLifecycleState.Hidden));
    }

    [TestMethod(DisplayName = "UT-CARD-007 [CRD-001] Instance rejects stale and mismatched snapshots")]
    public void InstanceRejectsStaleAndMismatchedSnapshots()
    {
        using CardRuntimeInstance runtime = CreateRuntime("test.sequence");
        runtime.Initialize();
        CardRuntimeSnapshot next = CreateSnapshot(
            instanceId: runtime.InstanceId,
            sequence: 1);

        Assert.IsTrue(runtime.ApplySnapshot(next));
        Assert.IsFalse(runtime.ApplySnapshot(next));
        Assert.IsFalse(runtime.ApplySnapshot(CreateSnapshot(
            instanceId: runtime.InstanceId,
            sequence: 0)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            runtime.ApplySnapshot(CreateSnapshot(
                instanceId: "test.other-instance",
                sequence: 2)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            runtime.ApplySnapshot(new CardRuntimeSnapshot(
                runtime.InstanceId,
                "test.other-type",
                1,
                2,
                FixedTimestamp,
                CardRuntimeFreshness.Fresh,
                CardRuntimeStatus.Ready)));
    }

    [TestMethod(DisplayName = "UT-CARD-008 [CRD-001] Snapshot subscription publishes accepted updates")]
    public void SnapshotSubscriptionPublishesAcceptedUpdates()
    {
        using CardRuntimeInstance runtime = CreateRuntime(
            "test.subscription");
        runtime.Initialize();
        var changedProperties = new List<string>();
        CardRuntimeSnapshot? observedSnapshot = null;
        runtime.PropertyChanged += (_, args) =>
            changedProperties.Add(args.PropertyName!);
        runtime.SnapshotChanged += (_, args) =>
            observedSnapshot = args.Snapshot;
        CardRuntimeSnapshot next = CreateSnapshot(
            instanceId: runtime.InstanceId,
            sequence: 1);

        Assert.IsTrue(runtime.ApplySnapshot(next));

        Assert.AreSame(next, observedSnapshot);
        CollectionAssert.Contains(
            changedProperties,
            nameof(CardRuntimeInstance.Snapshot));
    }

    [TestMethod(DisplayName = "UT-CARD-009 [CRD-005] Provider failure is isolated to one instance")]
    public async Task ProviderFailureIsIsolatedToOneInstance()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(
            new Dictionary<string, int>
            {
                ["lastValue"] = 9,
            });
        using CardRuntimeInstance failing = CreateRuntime(
            "test.failing",
            payload);
        using CardRuntimeInstance healthy = CreateRuntime(
            "test.healthy");
        failing.Initialize();
        healthy.Initialize();

        bool refreshed = await failing.RefreshAsync(
            _ => ValueTask.FromException<CardRuntimeSnapshot>(
                new InvalidOperationException("provider failed")),
            CancellationToken.None);

        Assert.IsFalse(refreshed);
        Assert.AreEqual(
            CardRuntimeStatus.Error,
            failing.Snapshot.Status);
        Assert.AreEqual(
            CardRuntimeErrorCodes.SnapshotProviderFailed,
            failing.Snapshot.ErrorCode);
        Assert.AreEqual(
            9,
            failing.Snapshot.Payload
                .GetProperty("lastValue")
                .GetInt32());
        Assert.AreEqual(
            CardRuntimeStatus.Ready,
            healthy.Snapshot.Status);
        Assert.AreEqual(0, healthy.Snapshot.Sequence);
    }

    [TestMethod(DisplayName = "UT-CARD-010 [CRD-005] Caller cancellation is not converted to an error")]
    public async Task CallerCancellationIsNotConvertedToAnError()
    {
        using CardRuntimeInstance runtime = CreateRuntime(
            "test.cancelled");
        runtime.Initialize();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runtime.RefreshAsync(
                token => ValueTask.FromCanceled<CardRuntimeSnapshot>(
                    token),
                cancellation.Token));

        Assert.AreEqual(
            CardRuntimeStatus.Ready,
            runtime.Snapshot.Status);
        Assert.AreEqual(0, runtime.Snapshot.Sequence);
    }

    [TestMethod(DisplayName = "UT-CARD-011 [CRD-001] Note adapter covers every declared editor status")]
    public void NoteAdapterCoversEveryDeclaredEditorStatus()
    {
        Assert.AreEqual(
            CardRuntimeStatus.Unavailable,
            BuiltInCardRuntimeFactory.MapNoteStatus(
                NoteEditorStatus.Unavailable));
        Assert.AreEqual(
            CardRuntimeStatus.Loading,
            BuiltInCardRuntimeFactory.MapNoteStatus(
                NoteEditorStatus.Loading));
        Assert.AreEqual(
            CardRuntimeStatus.Ready,
            BuiltInCardRuntimeFactory.MapNoteStatus(
                NoteEditorStatus.Ready));
        Assert.AreEqual(
            CardRuntimeStatus.Ready,
            BuiltInCardRuntimeFactory.MapNoteStatus(
                NoteEditorStatus.PendingSave));
        Assert.AreEqual(
            CardRuntimeStatus.Ready,
            BuiltInCardRuntimeFactory.MapNoteStatus(
                NoteEditorStatus.Saving));
        Assert.AreEqual(
            CardRuntimeStatus.Ready,
            BuiltInCardRuntimeFactory.MapNoteStatus(
                NoteEditorStatus.Saved));
        Assert.AreEqual(
            CardRuntimeStatus.Error,
            BuiltInCardRuntimeFactory.MapNoteStatus(
                NoteEditorStatus.Error));
        Assert.AreEqual(
            CardRuntimeStatus.Unknown,
            BuiltInCardRuntimeFactory.MapNoteStatus(
                (NoteEditorStatus)999));
    }

    private static CardRuntimeInstance CreateRuntime(
        string instanceId,
        JsonElement payload = default)
    {
        var definition = new CardDefinition(
            "test.card",
            "TestCardTitle.Text",
            CardSize.M,
            [CardSize.M]);
        return new CardRuntimeInstance(
            definition,
            instanceId,
            CreateSnapshot(
                instanceId: instanceId,
                payload: payload));
    }

    private static CardRuntimeSnapshot CreateSnapshot(
        string instanceId = "test.instance",
        long sequence = 0,
        JsonElement payload = default,
        IEnumerable<string>? actions = null,
        DateTimeOffset? timestamp = null,
        CardRuntimeStatus status = CardRuntimeStatus.Ready,
        CardRuntimeFreshness freshness =
            CardRuntimeFreshness.Fresh) =>
        new(
            instanceId,
            "test.card",
            schemaVersion: 1,
            sequence,
            timestamp ?? FixedTimestamp,
            freshness,
            status,
            payload,
            actions);
}
