using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteEditorMarkdownTests
{
    [TestMethod(DisplayName = "UT-NOTE-025 [NTE-001] Editor previews a loaded Markdown note without saving")]
    public async Task EditorPreviewsLoadedMarkdownNoteWithoutSaving()
    {
        var fake = new FakeNoteClient
        {
            Existing = new NoteDto
            {
                NoteId = NoteEditorViewModel.DefaultNoteId,
                Title = "Markdown note",
                Body = "# Heading\n- item",
                BodyFormat = NotesContract.MarkdownFormat,
                UpdatedAtUtc = "2026-08-10T00:00:00.0000000+00:00",
            },
        };
        await using var viewModel = new NoteEditorViewModel(fake);

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsTrue(viewModel.IsMarkdown);
        Assert.IsFalse(viewModel.IsMarkdownPreviewVisible);
        Assert.AreEqual(NoteMarkdownPreviewBlockKind.Heading1, viewModel.MarkdownPreviewBlocks[0].Kind);
        Assert.IsTrue(viewModel.ToggleMarkdownPreview());
        Assert.IsTrue(viewModel.IsMarkdownPreviewVisible);
        Assert.IsTrue(viewModel.ToggleMarkdownPreview());
        Assert.IsFalse(viewModel.IsMarkdownPreviewVisible);
        Assert.AreEqual(0, fake.Saves.Count);
    }

    [TestMethod(DisplayName = "UT-NOTE-026 [NTE-001] Editor persists the selected Markdown body format")]
    public async Task EditorPersistsSelectedMarkdownBodyFormat()
    {
        var fake = new FakeNoteClient();
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(20));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsTrue(viewModel.SetMarkdownMode(enabled: true));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await viewModel.WaitForIdleAsync(timeout.Token);

        Assert.AreEqual(NotesContract.MarkdownFormat, fake.Saves[^1].BodyFormat);
        Assert.IsTrue(viewModel.IsMarkdown);
        Assert.IsFalse(viewModel.HasUnsavedChanges);

        Assert.IsTrue(viewModel.SetMarkdownMode(enabled: false));
        await viewModel.WaitForIdleAsync(timeout.Token);
        Assert.AreEqual(NotesContract.PlainTextFormat, fake.Saves[^1].BodyFormat);
        Assert.IsFalse(viewModel.IsMarkdown);
    }

    private sealed class FakeNoteClient : INoteClient
    {
        public NoteDto? Existing { get; init; }

        public List<NoteSaveRequest> Saves { get; } = [];

        public Task<NoteDto?> GetNoteAsync(
            string noteId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Existing);

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
