using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteListCoordinatorTests
{
    [TestMethod(DisplayName = "UT-NOTE-040 [NTE-001] list coordinator returns current search state")]
    public async Task SearchReturnsCurrentState()
    {
        var client = new FakeNoteClient
        {
            ResultsByQuery =
            {
                ["work"] =
                [
                    new NoteDto
                    {
                        NoteId = "note-work",
                        Title = "Work",
                        Body = "body",
                    },
                ],
            },
        };
        await using var editor = new NoteEditorViewModel(null);
        await using var search = new NoteSearchViewModel(
            client,
            searchDebounce: TimeSpan.FromMilliseconds(10));
        var coordinator = new NoteListCoordinator(editor, search);

        NoteListOperationResult result = await coordinator.SearchAsync(
            " work ",
            CancellationToken.None);

        Assert.IsTrue(result.IsCurrentQuery);
        Assert.IsTrue(result.HasResults);
        Assert.AreEqual(NoteSearchStatus.Ready, result.Status);
        Assert.AreEqual(1, result.ResultCount);
        Assert.AreEqual(1, client.Queries.Count);
        Assert.AreEqual("work", client.Queries[0]);
    }

    [TestMethod(DisplayName = "UT-NOTE-041 [NTE-001] delete refresh preserves the active query")]
    public async Task RefreshAfterDeletePreservesActiveQuery()
    {
        var client = new FakeNoteClient
        {
            ResultsByQuery =
            {
                ["work"] =
                [
                    new NoteDto
                    {
                        NoteId = "note-work",
                        Title = "Work",
                        Body = "body",
                    },
                ],
            },
        };
        await using var editor = new NoteEditorViewModel(null);
        await using var search = new NoteSearchViewModel(
            client,
            searchDebounce: TimeSpan.FromMilliseconds(10));
        var coordinator = new NoteListCoordinator(editor, search);

        await coordinator.SearchAsync("work", CancellationToken.None);
        client.ResultsByQuery["work"] = [];

        NoteListOperationResult result = await coordinator
            .RefreshAfterDeleteAsync(CancellationToken.None);

        Assert.IsTrue(result.IsCurrentQuery);
        Assert.IsFalse(result.HasResults);
        Assert.AreEqual(NoteSearchStatus.Empty, result.Status);
        Assert.AreEqual("work", search.Query);
    }

    [TestMethod(DisplayName = "UT-NOTE-042 [NTE-001] list coordinator opens a selected note")]
    public async Task OpensSelectedNote()
    {
        var client = new FakeNoteClient
        {
            Notes =
            {
                [NoteEditorViewModel.DefaultNoteId] = CreateNote(
                    NoteEditorViewModel.DefaultNoteId,
                    "Primary"),
                ["note-secondary"] = CreateNote(
                    "note-secondary",
                    "Secondary"),
            },
        };
        await using var editor = new NoteEditorViewModel(client);
        await using var search = new NoteSearchViewModel(
            client,
            searchDebounce: TimeSpan.FromMilliseconds(10));
        var coordinator = new NoteListCoordinator(editor, search);

        Assert.IsTrue(await editor.LoadAsync(CancellationToken.None));
        Assert.IsTrue(await coordinator.LoadNoteAsync(
            "note-secondary",
            CancellationToken.None));
        Assert.AreEqual("note-secondary", editor.NoteId);
        Assert.AreEqual("Secondary", editor.Title);
    }

    private static NoteDto CreateNote(string noteId, string title) =>
        new()
        {
            NoteId = noteId,
            Title = title,
            Body = "body",
            UpdatedAtUtc = "2026-08-17T00:00:00.0000000+00:00",
        };

    private sealed class FakeNoteClient : INoteClient
    {
        public Dictionary<string, NoteDto> Notes { get; init; } = [];

        public Dictionary<string, IReadOnlyList<NoteDto>> ResultsByQuery { get; init; } = [];

        public List<string> Queries { get; } = [];

        public Task<NoteDto?> GetNoteAsync(
            string noteId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Notes.TryGetValue(noteId, out NoteDto? note)
                    ? note
                    : null);

        public Task<IReadOnlyList<NoteDto>> SearchNotesAsync(
            string query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<NoteDto>>(
                ResultsByQuery.TryGetValue(query, out IReadOnlyList<NoteDto>? results)
                    ? results
                    : []);
        }

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
                NoteId = request.NoteId ?? string.Empty,
                Title = request.Title ?? string.Empty,
                Body = request.Body ?? string.Empty,
                BodyFormat = request.BodyFormat ?? NotesContract.PlainTextFormat,
            });
    }
}
