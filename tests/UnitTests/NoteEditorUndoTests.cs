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

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "first";
        viewModel.Body = "second";

        Assert.IsTrue(viewModel.CanUndo);
        Assert.IsTrue(viewModel.Undo());
        Assert.AreEqual("first", viewModel.Body);
        Assert.IsTrue(viewModel.CanRedo);

        Assert.IsTrue(viewModel.Redo());
        Assert.AreEqual("second", viewModel.Body);
        Assert.IsFalse(viewModel.CanRedo);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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
            autosaveDelay: TimeSpan.FromSeconds(10));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        for (int index = 0; index < 25; index++)
        {
            viewModel.Body = $"draft-{index}";
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

    private sealed class FakeNoteClient : INoteClient
    {
        public List<NoteSaveRequest> Saves { get; } = [];

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
            CancellationToken cancellationToken)
        {
            Saves.Add(request);
            return Task.FromResult(new NoteDto
            {
                NoteId = request.NoteId ?? string.Empty,
                Title = request.Title ?? string.Empty,
                Body = request.Body ?? string.Empty,
                BodyFormat = request.BodyFormat ?? NotesContract.PlainTextFormat,
                UpdatedAtUtc = $"2026-08-10T00:00:00.{Saves.Count:0000000}+00:00",
            });
        }
    }
}
