using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteDeleteViewModelTests
{
    [TestMethod(DisplayName = "UT-NOTE-032 [NTE-001] Editor deletes the current note with its revision")]
    public async Task EditorDeletesCurrentNoteWithItsRevision()
    {
        var fake = new FakeNoteClient();
        fake.Notes[NoteEditorViewModel.DefaultNoteId] = CreateNote(
            NoteEditorViewModel.DefaultNoteId,
            "Primary",
            "Primary body",
            "2026-08-10T00:00:01.0000000+00:00");
        await using var viewModel = new NoteEditorViewModel(fake);

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsTrue(viewModel.CanDelete);
        Assert.IsTrue(await viewModel.DeleteCurrentNoteAsync());

        Assert.AreEqual(1, fake.DeleteRequests.Count);
        Assert.AreEqual(NoteEditorViewModel.DefaultNoteId, fake.DeleteRequests[0].NoteId);
        Assert.AreEqual(
            "2026-08-10T00:00:01.0000000+00:00",
            fake.DeleteRequests[0].ExpectedUpdatedAtUtc);
        Assert.IsFalse(fake.Notes.ContainsKey(NoteEditorViewModel.DefaultNoteId));
        Assert.AreEqual(NoteEditorViewModel.DefaultNoteId, viewModel.NoteId);
        Assert.AreEqual(string.Empty, viewModel.Title);
        Assert.AreEqual(string.Empty, viewModel.Body);
        Assert.IsFalse(viewModel.HasUnsavedChanges);
        Assert.AreEqual(NoteEditorStatus.Ready, viewModel.Status);
        Assert.IsFalse(viewModel.CanDelete);
    }

    [TestMethod(DisplayName = "UT-NOTE-033 [NTE-001] Editor refuses to delete an unsaved draft")]
    public async Task EditorRefusesToDeleteUnsavedDraft()
    {
        var fake = new FakeNoteClient();
        fake.Notes[NoteEditorViewModel.DefaultNoteId] = CreateNote(
            NoteEditorViewModel.DefaultNoteId,
            "Primary",
            "Primary body",
            "2026-08-10T00:00:01.0000000+00:00");
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromSeconds(1));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "unsaved draft";

        Assert.IsFalse(viewModel.CanDelete);
        Assert.IsFalse(await viewModel.DeleteCurrentNoteAsync());
        Assert.AreEqual(0, fake.DeleteRequests.Count);
        Assert.AreEqual("unsaved draft", viewModel.Body);
        Assert.IsTrue(fake.Notes.ContainsKey(NoteEditorViewModel.DefaultNoteId));
    }

    [TestMethod(DisplayName = "UT-NOTE-034 [NTE-001] Editor retains the current note when deletion fails")]
    public async Task EditorRetainsCurrentNoteWhenDeletionFails()
    {
        var fake = new FakeNoteClient
        {
            DeleteException = new CoreBrokerClientException(
                NotesContract.DeleteMethod,
                "conflict.notes-revision",
                "stale revision"),
        };
        fake.Notes[NoteEditorViewModel.DefaultNoteId] = CreateNote(
            NoteEditorViewModel.DefaultNoteId,
            "Current title",
            "Current body",
            "2026-08-10T00:00:01.0000000+00:00");
        await using var viewModel = new NoteEditorViewModel(fake);

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsFalse(await viewModel.DeleteCurrentNoteAsync());

        Assert.AreEqual(NoteEditorViewModel.DefaultNoteId, viewModel.NoteId);
        Assert.AreEqual("Current title", viewModel.Title);
        Assert.AreEqual("Current body", viewModel.Body);
        Assert.AreEqual(NoteEditorStatus.Error, viewModel.Status);
        Assert.AreEqual("conflict.notes-revision", viewModel.ErrorCode);
        Assert.IsTrue(viewModel.CanLoadNote);
        Assert.IsTrue(fake.Notes.ContainsKey(NoteEditorViewModel.DefaultNoteId));
    }

    [TestMethod(DisplayName = "UT-NOTE-035 [NTE-001] Note list deletes a saved result and removes it from the list")]
    public async Task NoteListDeletesSavedResultAndRemovesItFromList()
    {
        var fake = new FakeNoteClient();
        fake.Notes["primary-note"] = CreateNote(
            "primary-note",
            "Primary",
            "Primary body",
            "2026-08-10T00:00:01.0000000+00:00");
        fake.Notes["secondary-note"] = CreateNote(
            "secondary-note",
            "Secondary",
            "Secondary body",
            "2026-08-10T00:00:02.0000000+00:00");
        await using var viewModel = new NoteSearchViewModel(
            fake,
            searchDebounce: TimeSpan.FromMilliseconds(10));

        Assert.IsTrue(await viewModel.LoadAllAsync());
        NoteSearchResult result = viewModel.Results.Single(
            item => item.NoteId == "secondary-note");

        Assert.IsTrue(await viewModel.DeleteNoteAsync(result));

        Assert.AreEqual(1, viewModel.Results.Count);
        Assert.AreEqual("primary-note", viewModel.Results[0].NoteId);
        Assert.AreEqual(NoteSearchStatus.Ready, viewModel.Status);
        Assert.AreEqual(1, fake.DeleteRequests.Count);
        Assert.AreEqual("secondary-note", fake.DeleteRequests[0].NoteId);
        Assert.AreEqual(
            "2026-08-10T00:00:02.0000000+00:00",
            fake.DeleteRequests[0].ExpectedUpdatedAtUtc);
    }

    [TestMethod(DisplayName = "UT-NOTE-036 [NTE-001] Note list keeps a result when deletion fails")]
    public async Task NoteListKeepsResultWhenDeletionFails()
    {
        var fake = new FakeNoteClient
        {
            DeleteException = new CoreBrokerClientException(
                NotesContract.DeleteMethod,
                "transport.unavailable",
                "delete failure"),
        };
        fake.Notes["primary-note"] = CreateNote(
            "primary-note",
            "Primary",
            "Primary body",
            "2026-08-10T00:00:01.0000000+00:00");
        await using var viewModel = new NoteSearchViewModel(
            fake,
            searchDebounce: TimeSpan.FromMilliseconds(10));

        Assert.IsTrue(await viewModel.LoadAllAsync());
        NoteSearchResult result = viewModel.Results[0];

        Assert.IsFalse(await viewModel.DeleteNoteAsync(result));

        Assert.AreEqual(1, viewModel.Results.Count);
        Assert.AreEqual("primary-note", viewModel.Results[0].NoteId);
        Assert.AreEqual(NoteSearchStatus.Error, viewModel.Status);
        Assert.AreEqual("transport.unavailable", viewModel.ErrorCode);
    }

    [TestMethod(DisplayName = "UT-NOTE-038 [NTE-001] Editor reloads the default note after deleting another note")]
    public async Task EditorReloadsDefaultNoteAfterDeletingAnotherNote()
    {
        var fake = new FakeNoteClient();
        fake.Notes[NoteEditorViewModel.DefaultNoteId] = CreateNote(
            NoteEditorViewModel.DefaultNoteId,
            "Primary",
            "Primary body",
            "2026-08-10T00:00:01.0000000+00:00");
        fake.Notes["secondary-note"] = CreateNote(
            "secondary-note",
            "Secondary",
            "Secondary body",
            "2026-08-10T00:00:02.0000000+00:00");
        await using var viewModel = new NoteEditorViewModel(fake);

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsTrue(await viewModel.LoadNoteAsync("secondary-note"));
        Assert.IsTrue(await viewModel.DeleteCurrentNoteAsync());

        Assert.IsTrue(await viewModel.LoadNoteAsync(NoteEditorViewModel.DefaultNoteId));
        Assert.AreEqual(NoteEditorViewModel.DefaultNoteId, viewModel.NoteId);
        Assert.AreEqual("Primary", viewModel.Title);
        Assert.AreEqual("Primary body", viewModel.Body);
        Assert.IsTrue(viewModel.CanDelete);
    }

    private static NoteDto CreateNote(
        string noteId,
        string title,
        string body,
        string updatedAtUtc) =>
        new()
        {
            NoteId = noteId,
            Title = title,
            Body = body,
            BodyFormat = NotesContract.PlainTextFormat,
            CreatedAtUtc = "2026-08-10T00:00:00.0000000+00:00",
            UpdatedAtUtc = updatedAtUtc,
        };

    private sealed class FakeNoteClient : INoteClient
    {
        public Dictionary<string, NoteDto> Notes { get; } = [];

        public List<NoteDeleteRequest> DeleteRequests { get; } = [];

        public Exception? DeleteException { get; init; }

        public Task<NoteDto?> GetNoteAsync(
            string noteId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Notes.TryGetValue(noteId, out NoteDto? note)
                    ? note
                    : null);

        public Task<IReadOnlyList<NoteDto>> SearchNotesAsync(
            string query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NoteDto>>(
                Notes.Values
                    .Where(note =>
                        query.Length == 0 ||
                        note.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        note.Body.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToArray());

        public Task<NoteDeleteResponse> DeleteNoteAsync(
            NoteDeleteRequest request,
            CancellationToken cancellationToken)
        {
            DeleteRequests.Add(request);
            if (DeleteException is not null)
            {
                return Task.FromException<NoteDeleteResponse>(DeleteException);
            }

            bool deleted = request.NoteId is not null &&
                Notes.Remove(request.NoteId);
            return Task.FromResult(new NoteDeleteResponse
            {
                ClientOperationId = request.ClientOperationId,
                Deleted = deleted,
            });
        }

        public Task<NoteDto> SaveNoteAsync(
            NoteSaveRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new NoteDto
            {
                NoteId = request.NoteId ?? string.Empty,
                Title = request.Title ?? string.Empty,
                Body = request.Body ?? string.Empty,
                BodyFormat = request.BodyFormat ?? NotesContract.PlainTextFormat,
                UpdatedAtUtc = "2026-08-10T00:00:03.0000000+00:00",
            });
    }
}
