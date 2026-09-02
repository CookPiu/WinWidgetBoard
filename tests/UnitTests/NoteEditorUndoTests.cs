using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteEditorUndoTests
{
    [TestMethod(DisplayName = "UT-NOTE-020 [NTE-001] Editor undoes and redoes draft edits")]
    public async Task EditorUndoesAndRedoesDraftEdits()
    {
        var fake = new FakeNoteClient();
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(40));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "first";
        // A save closes the undo step; without it the two edits would be one step.
        await viewModel.WaitForIdleAsync(timeout.Token);
        viewModel.Body = "second";

        Assert.IsTrue(viewModel.CanUndo);
        Assert.IsTrue(viewModel.Undo());
        Assert.AreEqual("first", viewModel.Body);
        Assert.IsTrue(viewModel.CanRedo);

        Assert.IsTrue(viewModel.Redo());
        Assert.AreEqual("second", viewModel.Body);
        Assert.IsFalse(viewModel.CanRedo);

        await viewModel.WaitForIdleAsync(timeout.Token);
        Assert.AreEqual("second", fake.Saves[^1].Body);
        Assert.IsFalse(viewModel.HasUnsavedChanges);
    }

    [TestMethod(DisplayName = "UT-NOTE-021 [NTE-001] New draft input clears note redo history")]
    public async Task NewDraftInputClearsNoteRedoHistory()
    {
        await using var viewModel = new NoteEditorViewModel(
            new FakeNoteClient(),
            autosaveDelay: TimeSpan.FromSeconds(10));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "first";
        viewModel.Body = "second";
        Assert.IsTrue(viewModel.Undo());
        Assert.IsTrue(viewModel.CanRedo);

        viewModel.Body = "third";

        Assert.IsFalse(viewModel.CanRedo);
        Assert.IsTrue(viewModel.CanUndo);
        Assert.AreEqual("third", viewModel.Body);
    }

    [TestMethod(DisplayName = "UT-NOTE-022 [NTE-001] Note draft history is limited to 20 undo steps")]
    public async Task NoteDraftHistoryIsLimitedToTwentyUndoSteps()
    {
        await using var viewModel = new NoteEditorViewModel(
            new FakeNoteClient(),
            autosaveDelay: TimeSpan.FromMilliseconds(1));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        for (int index = 0; index < 25; index++)
        {
            viewModel.Body = $"draft-{index}";
            await viewModel.WaitForIdleAsync(timeout.Token);
        }

        int undoCount = 0;
        while (viewModel.Undo())
        {
            undoCount++;
        }

        Assert.AreEqual(20, undoCount);
        Assert.AreEqual("draft-4", viewModel.Body);
        Assert.IsFalse(viewModel.CanUndo);
        Assert.IsTrue(viewModel.CanRedo);
    }

    [TestMethod(DisplayName = "UT-NOTE-045 [NTE-001] Edits between two saves form one undo step per field")]
    public async Task EditsBetweenTwoSavesFormOneUndoStepPerField()
    {
        var fake = new FakeNoteClient();
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(20));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "s";
        viewModel.Body = "so";
        viewModel.Body = "some";
        // Touching the other field opens a step of its own.
        viewModel.Title = "T";
        viewModel.Title = "Ti";
        await viewModel.WaitForIdleAsync(timeout.Token);
        // A save closes the run: the next edit is a new step.
        viewModel.Body = "some more";

        Assert.IsTrue(viewModel.Undo());
        Assert.AreEqual("some", viewModel.Body);
        Assert.AreEqual("Ti", viewModel.Title);
        Assert.IsTrue(viewModel.Undo());
        Assert.AreEqual("some", viewModel.Body);
        Assert.AreEqual(string.Empty, viewModel.Title);
        Assert.IsTrue(viewModel.Undo());
        Assert.AreEqual(string.Empty, viewModel.Body);
        Assert.IsFalse(viewModel.CanUndo);
    }

    [TestMethod(DisplayName = "UT-NOTE-047 [NTE-001] Undo stays available while a save is in flight")]
    public async Task UndoStaysAvailableWhileSaveIsInFlight()
    {
        var fake = new FakeNoteClient
        {
            SaveGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(10));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "typed";
        while (!viewModel.IsSaving)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, timeout.Token);
        }

        // The buttons used to unload here: CanUndo went false for the length of every save.
        Assert.IsTrue(viewModel.CanUndo);
        Assert.IsTrue(viewModel.Undo());
        Assert.AreEqual(string.Empty, viewModel.Body);

        fake.SaveGate.SetResult();
        await viewModel.WaitForIdleAsync(timeout.Token);
        Assert.AreEqual(string.Empty, fake.Saves[^1].Body);
        Assert.IsFalse(viewModel.HasUnsavedChanges);
    }

    private sealed class FakeNoteClient : INoteClient
    {
        public List<NoteSaveRequest> Saves { get; } = [];

        public TaskCompletionSource? SaveGate { get; init; }

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

        public async Task<NoteDto> SaveNoteAsync(
            NoteSaveRequest request,
            CancellationToken cancellationToken)
        {
            Saves.Add(request);
            if (SaveGate is not null)
            {
                await SaveGate.Task.WaitAsync(cancellationToken);
            }

            return new NoteDto
            {
                NoteId = request.NoteId ?? string.Empty,
                Title = request.Title ?? string.Empty,
                Body = request.Body ?? string.Empty,
                BodyFormat = request.BodyFormat ?? NotesContract.PlainTextFormat,
                UpdatedAtUtc = $"2026-08-10T00:00:00.{Saves.Count:0000000}+00:00",
            };
        }
    }
}
