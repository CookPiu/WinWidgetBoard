using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardLayoutSurfaceViewModelTests
{
    private static readonly string[] ExpectedIds = [
        "demo.notes",
        "demo.timer",
    ];

    [TestMethod(DisplayName = "UT-GRID-012 [LYT-001] Surface items follow placement snapshots")]
    public async Task SurfaceItemsFollowPlacementSnapshots()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("demo.notes", CardSize.L),
                new CardLayoutItem("demo.timer", CardSize.M),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);
        using var surface = new CardLayoutSurfaceViewModel(
            editMode,
            noteEditor,
            status => status.ToString());
        CardSurfaceItem[] originalItems = surface.Items.ToArray();
        CardRuntimeInstance[] originalRuntimes = surface.Items
            .Select(item => item.Runtime)
            .ToArray();
        var changed = new List<string>();
        surface.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        Assert.AreEqual(2, surface.Items.Count);
        CollectionAssert.AreEqual(
            ExpectedIds,
            surface.Items.Select(item => item.InstanceId).ToArray());
        Assert.AreEqual(2, surface.Items[0].Placement.ColumnSpan);
        Assert.IsFalse(surface.Items[0].IsEditing);

        editMode.BeginEdit();
        Assert.IsTrue(surface.Items[0].IsEditing);
        editMode.CancelEdit();
        Assert.IsFalse(surface.Items[0].IsEditing);

        layout.SetColumnCount(2);

        Assert.AreEqual(2, surface.Items.Count);
        Assert.AreEqual(2, surface.Items[0].Placement.ColumnSpan);
        CollectionAssert.AreEqual(originalItems, surface.Items.ToArray());
        CollectionAssert.AreEqual(
            originalRuntimes,
            surface.Items.Select(item => item.Runtime).ToArray());
        CollectionAssert.DoesNotContain(
            changed,
            nameof(CardLayoutSurfaceViewModel.Items));
    }

    [TestMethod(DisplayName = "UT-GRID-013 [LYT-005] Note status reaches the surface item")]
    public async Task NoteStatusReachesSurfaceItem()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("demo.notes", CardSize.L)]);
        var editMode = new CardLayoutEditViewModel(layout);
        using var surface = new CardLayoutSurfaceViewModel(
            editMode,
            noteEditor,
            status => $"{status}:{noteEditor.ErrorCode}");
        var changed = new List<string>();
        surface.Items[0].PropertyChanged += (_, args) =>
            changed.Add(args.PropertyName!);

        noteEditor.MarkUnavailable("transport.timeout");

        Assert.AreEqual(
            "Unavailable:transport.timeout",
            surface.Items[0].NoteStatusText);
        CollectionAssert.Contains(changed, nameof(CardSurfaceItem.NoteStatusText));
    }

    [TestMethod(DisplayName = "UT-GRID-014 [NTE-001] Loaded note status replaces the initial loading text")]
    public async Task LoadedNoteStatusReplacesInitialLoadingText()
    {
        await using var noteEditor = new NoteEditorViewModel(new ReadyNoteClient());
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("demo.notes", CardSize.L)]);
        var editMode = new CardLayoutEditViewModel(layout);
        using var surface = new CardLayoutSurfaceViewModel(
            editMode,
            noteEditor,
            status => status.ToString());
        surface.SetPanelVisibility(true);
        surface.Items[0].SetViewportVisibility(true);

        Assert.AreEqual("Loading", surface.Items[0].NoteStatusText);
        Assert.AreEqual(
            CardRuntimeStatus.Loading,
            surface.Items[0].RuntimeStatus);
        Assert.IsTrue(await noteEditor.LoadAsync(CancellationToken.None));
        Assert.AreEqual("Ready", surface.Items[0].NoteStatusText);
        Assert.AreEqual(
            CardRuntimeStatus.Ready,
            surface.Items[0].RuntimeStatus);
        Assert.AreEqual(
            NoteEditorViewModel.DefaultNoteId,
            surface.Items[0].RuntimeSnapshot.Payload
                .GetProperty("noteId")
                .GetString());

        noteEditor.MarkUnavailable("transport.timeout");

        Assert.AreEqual(
            CardRuntimeStatus.Unavailable,
            surface.Items[0].RuntimeStatus);
        Assert.AreEqual(
            "transport.timeout",
            surface.Items[0].RuntimeSnapshot.ErrorCode);
    }

    [TestMethod(DisplayName = "UT-CARD-031 [CRD-003] Hidden note status changes defer runtime snapshots until visible")]
    public async Task HiddenNoteStatusChangesDeferRuntimeSnapshotsUntilVisible()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem("demo.notes", CardSize.L)]);
        var editMode = new CardLayoutEditViewModel(layout);
        using var surface = new CardLayoutSurfaceViewModel(
            editMode,
            noteEditor);
        CardSurfaceItem item = surface.Items[0];
        long hiddenSequence = item.RuntimeSnapshot.Sequence;
        string? hiddenError = item.RuntimeSnapshot.ErrorCode;

        noteEditor.MarkUnavailable("transport.timeout");

        Assert.AreEqual(hiddenSequence, item.RuntimeSnapshot.Sequence);
        Assert.AreEqual(hiddenError, item.RuntimeSnapshot.ErrorCode);
        Assert.AreEqual(CardLifecycleState.Hidden, item.Runtime.LifecycleState);

        surface.SetPanelVisibility(true);
        item.SetViewportVisibility(true);

        Assert.AreEqual(hiddenSequence + 1, item.RuntimeSnapshot.Sequence);
        Assert.AreEqual(
            "transport.timeout",
            item.RuntimeSnapshot.ErrorCode);
        Assert.AreEqual(CardLifecycleState.Visible, item.Runtime.LifecycleState);
    }

    [TestMethod(DisplayName = "UT-GRID-027 [LYT-004] Drop preview reflows in place without moving the dragged base")]
    public async Task DropPreviewReflowsInPlaceWithoutMovingDraggedBase()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("demo.notes", CardSize.L),
                new CardLayoutItem("demo.timer", CardSize.M),
                new CardLayoutItem("demo.todo", CardSize.M),
                new CardLayoutItem("demo.calendar", CardSize.M),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);
        using var surface = new CardLayoutSurfaceViewModel(
            editMode,
            noteEditor,
            status => status.ToString());
        CardSurfaceItem[] originalItems = surface.Items.ToArray();
        CardPlacement timerStart = layout.Placements.Single(
            placement => placement.InstanceId == "demo.timer");
        CardPlacement calendarStart = layout.Placements.Single(
            placement => placement.InstanceId == "demo.calendar");
        editMode.BeginEdit();
        Assert.IsTrue(editMode.TryPreviewDrop(
            "demo.timer",
            new GridCell(calendarStart.Column, calendarStart.Row),
            out IReadOnlyList<CardPlacement> projected));

        Assert.IsTrue(surface.ApplyDropPreview("demo.timer", projected));

        CollectionAssert.AreEqual(originalItems, surface.Items.ToArray());
        Assert.AreEqual(
            timerStart,
            surface.Items.Single(item => item.InstanceId == "demo.timer").Placement);
        CardPlacement projectedCalendar = projected.Single(
            placement => placement.InstanceId == "demo.calendar");
        Assert.AreEqual(
            projectedCalendar,
            surface.Items.Single(item => item.InstanceId == "demo.calendar").Placement);
        Assert.IsFalse(surface.ApplyDropPreview("demo.timer", projected));

        Assert.IsTrue(surface.ResetDropPreview());
        Assert.AreEqual(
            calendarStart,
            surface.Items.Single(item => item.InstanceId == "demo.calendar").Placement);
    }

    [TestMethod(DisplayName = "UT-GRID-032 [LYT-004] Persisted reorder moves stable surface identities")]
    public async Task PersistedReorderMovesStableSurfaceIdentities()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("demo.notes", CardSize.L),
                new CardLayoutItem("demo.timer", CardSize.M),
                new CardLayoutItem("demo.todo", CardSize.M),
                new CardLayoutItem("demo.calendar", CardSize.M),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);
        using var surface = new CardLayoutSurfaceViewModel(
            editMode,
            noteEditor,
            status => status.ToString());
        Dictionary<string, CardSurfaceItem> originalItems = surface.Items
            .ToDictionary(item => item.InstanceId, StringComparer.Ordinal);
        Dictionary<string, CardRuntimeInstance> originalRuntimes =
            surface.Items.ToDictionary(
                item => item.InstanceId,
                item => item.Runtime,
                StringComparer.Ordinal);
        var changed = new List<string>();
        surface.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        layout.ReplaceItems(
        [
            new CardLayoutItem("demo.notes", CardSize.L, 0, 0),
            new CardLayoutItem("demo.calendar", CardSize.M, 2, 0),
            new CardLayoutItem("demo.todo", CardSize.M, 2, 1),
            new CardLayoutItem("demo.timer", CardSize.M, 0, 2),
        ]);

        string[] expectedIds =
        [
            "demo.notes",
            "demo.calendar",
            "demo.todo",
            "demo.timer",
        ];
        CollectionAssert.AreEqual(
            expectedIds,
            surface.Items.Select(item => item.InstanceId).ToArray());
        foreach (CardSurfaceItem item in surface.Items)
        {
            Assert.AreSame(originalItems[item.InstanceId], item);
            Assert.AreSame(originalRuntimes[item.InstanceId], item.Runtime);
        }
        CollectionAssert.Contains(
            changed,
            nameof(CardLayoutSurfaceViewModel.Items));
    }

    [TestMethod(DisplayName = "UT-CARD-012 [CRD-001] Built-in surfaces resolve stable runtime types")]
    public async Task BuiltInSurfacesResolveStableRuntimeTypes()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("demo.notes", CardSize.L),
                new CardLayoutItem("demo.timer", CardSize.M),
                new CardLayoutItem("demo.todo", CardSize.M),
                new CardLayoutItem("demo.calendar", CardSize.M),
                new CardLayoutItem("custom.unknown", CardSize.M),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);
        using var surface = new CardLayoutSurfaceViewModel(
            editMode,
            noteEditor);

        string[] expectedTypeIds =
        [
            BuiltInCardCatalog.NotesCardTypeId,
            BuiltInCardCatalog.TimerCardTypeId,
            BuiltInCardCatalog.TodoCardTypeId,
            BuiltInCardCatalog.CalendarCardTypeId,
            BuiltInCardCatalog.UnknownCardTypeId,
        ];
        CollectionAssert.AreEqual(
            expectedTypeIds,
            surface.Items.Select(item => item.CardTypeId).ToArray());
        Assert.IsTrue(surface.Items.All(
            item => item.Runtime.LifecycleState ==
                CardLifecycleState.Hidden));
        Assert.IsTrue(surface.Items.All(
            item => item.RuntimeStatus is
                CardRuntimeStatus.Unavailable or
                CardRuntimeStatus.Unknown));
        Assert.AreEqual(
            CardRuntimeErrorCodes.UnknownCard,
            surface.Items[^1].RuntimeSnapshot.ErrorCode);
        Assert.AreEqual(
            0,
            surface.Items[^1].RuntimeSnapshot.AllowedActionIds.Count);
    }

    [TestMethod(DisplayName = "UT-CARD-027 [CRD-003] Surface registrations follow removal and runtime identity reuse")]
    public async Task SurfaceRegistrationsFollowRemovalAndRuntimeIdentityReuse()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        var layout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem("demo.notes", CardSize.L),
                new CardLayoutItem("demo.timer", CardSize.M),
            ]);
        var editMode = new CardLayoutEditViewModel(layout);
        var scheduler = new CardRuntimeVisibilityScheduler(panelVisible: true);
        using var surface = new CardLayoutSurfaceViewModel(
            editMode,
            noteEditor,
            visibilityScheduler: scheduler);

        Assert.AreEqual(2, scheduler.RegistrationCount);
        CardRuntimeInstance oldNotesRuntime = surface.Items[0].Runtime;
        layout.ReplaceItems(
        [
            new CardLayoutItem("demo.timer", CardSize.M),
        ]);

        Assert.AreEqual(1, scheduler.RegistrationCount);
        Assert.AreEqual(
            CardLifecycleState.Disposed,
            oldNotesRuntime.LifecycleState);

        layout.ReplaceItems(
        [
            new CardLayoutItem("demo.notes", CardSize.L),
        ]);
        CardRuntimeInstance newNotesRuntime = surface.Items[0].Runtime;
        Assert.AreNotSame(oldNotesRuntime, newNotesRuntime);
        Assert.IsFalse(surface.SetViewportVisibility(oldNotesRuntime, true));
        Assert.IsTrue(surface.SetViewportVisibility(newNotesRuntime, true));
        Assert.AreEqual(CardLifecycleState.Visible, newNotesRuntime.LifecycleState);
    }

    private sealed class ReadyNoteClient : INoteClient
    {
        public Task<NoteDto?> GetNoteAsync(
            string noteId,
            CancellationToken cancellationToken) =>
            Task.FromResult<NoteDto?>(null);

        public Task<IReadOnlyList<NoteDto>> SearchNotesAsync(
            string query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NoteDto>>([]);

        public Task<NoteDeleteResponse> DeleteNoteAsync(
            NoteDeleteRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NoteDeleteResponse
            {
                ClientOperationId = request.ClientOperationId,
                Deleted = true,
            });

        public Task<NoteDto> SaveNoteAsync(
            NoteSaveRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NoteDto
            {
                NoteId = request.NoteId!,
                Title = request.Title!,
                Body = request.Body!,
                BodyFormat = request.BodyFormat!,
                CreatedAtUtc = "2026-08-08T00:00:00.0000000+00:00",
                UpdatedAtUtc = "2026-08-08T00:00:01.0000000+00:00",
            });
    }
}
