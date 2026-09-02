using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteEditorNoteSwitchTests
{
    [TestMethod(DisplayName = "UT-NOTE-017 [NTE-001] Editor opens a selected saved note")]
    public async Task EditorOpensSelectedSavedNote()
    {
        var fake = new FakeNoteClient();
        fake.Notes[NoteEditorViewModel.DefaultNoteId] = CreateNote(
            NoteEditorViewModel.DefaultNoteId,
            "Primary",
            "Primary body");
        fake.Notes["secondary-note"] = CreateNote(
            "secondary-note",
            "Secondary",
            "Secondary body");
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromSeconds(1));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsTrue(viewModel.CanLoadNote);
        Assert.IsTrue(await viewModel.LoadNoteAsync("secondary-note"));

        Assert.AreEqual("secondary-note", viewModel.NoteId);
        Assert.AreEqual("Secondary", viewModel.Title);
        Assert.AreEqual("Secondary body", viewModel.Body);
        Assert.AreEqual(NoteEditorStatus.Ready, viewModel.Status);
        Assert.IsTrue(viewModel.CanLoadNote);
    }

    [TestMethod(DisplayName = "UT-NOTE-018 [NTE-001] Editor refuses to replace an unsaved draft")]
    public async Task EditorRefusesToReplaceUnsavedDraft()
    {
        var fake = new FakeNoteClient();
        fake.Notes[NoteEditorViewModel.DefaultNoteId] = CreateNote(
            NoteEditorViewModel.DefaultNoteId,
            "Primary",
            "Original body");
        fake.Notes["secondary-note"] = CreateNote(
            "secondary-note",
            "Secondary",
            "Secondary body");
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromSeconds(1));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "Unsaved draft";

        Assert.IsFalse(viewModel.CanLoadNote);
        Assert.IsFalse(await viewModel.LoadNoteAsync("secondary-note"));
        Assert.AreEqual(NoteEditorViewModel.DefaultNoteId, viewModel.NoteId);
        Assert.AreEqual("Unsaved draft", viewModel.Body);
    }

    [TestMethod(DisplayName = "UT-NOTE-019 [NTE-001] Editor keeps the current note when a selected note is missing")]
    public async Task EditorKeepsCurrentNoteWhenSelectedNoteIsMissing()
    {
        var fake = new FakeNoteClient();
        fake.Notes[NoteEditorViewModel.DefaultNoteId] = CreateNote(
            NoteEditorViewModel.DefaultNoteId,
            "Primary",
            "Primary body");
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(20));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsFalse(await viewModel.LoadNoteAsync("missing-note"));

        Assert.AreEqual(NoteEditorViewModel.DefaultNoteId, viewModel.NoteId);
        Assert.AreEqual("Primary", viewModel.Title);
        Assert.AreEqual("Primary body", viewModel.Body);
        Assert.AreEqual(NoteEditorStatus.Ready, viewModel.Status);
        Assert.AreEqual("resource.not-found", viewModel.ErrorCode);
        Assert.IsTrue(viewModel.CanLoadNote);
    }

    private static NoteDto CreateNote(string noteId, string title, string body) =>
        new()
        {
            NoteId = noteId,
            Title = title,
            Body = body,
            BodyFormat = NotesContract.PlainTextFormat,
            CreatedAtUtc = "2026-08-10T00:00:00.0000000+00:00",
            UpdatedAtUtc = "2026-08-10T00:00:01.0000000+00:00",
        };

    [TestMethod(DisplayName = "UT-NOTE-053 [NTE-001] Edits arriving while a note switch is in flight are not recorded against the note being left")]
    public async Task EditsDuringNoteSwitchAreNotRecordedAgainstTheNoteBeingLeft()
    {
        var fake = new FakeNoteClient();
        fake.Notes[NoteEditorViewModel.DefaultNoteId] = CreateNote(
            NoteEditorViewModel.DefaultNoteId,
            "Primary",
            "Primary body");
        fake.Notes["secondary-note"] = CreateNote(
            "secondary-note",
            "Secondary",
            "Secondary body");
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(10));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        // Gated only now: the initial load above has to complete on its own.
        fake.LoadGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> switching = viewModel.LoadNoteAsync("secondary-note");
        Assert.IsTrue(viewModel.IsLoading);

        // A keystroke that lands between the pick and the arrival of the picked note. It
        // used to be recorded on the primary note, scheduled for saving, and then dropped
        // when the secondary note arrived and moved the draft version on.
        viewModel.Body = "typed during the switch";
        Assert.IsFalse(viewModel.HasUnsavedChanges);

        fake.LoadGate.SetResult();
        Assert.IsTrue(await switching);
        Assert.AreEqual("Secondary body", viewModel.Body);
        Assert.IsFalse(viewModel.HasUnsavedChanges);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await viewModel.WaitForIdleAsync(timeout.Token);
        Assert.AreEqual(0, fake.Saves.Count);
    }

    private sealed class FakeNoteClient : INoteClient
    {
        public Dictionary<string, NoteDto> Notes { get; } = [];

        public List<NoteSaveRequest> Saves { get; } = [];

        public TaskCompletionSource? LoadGate { get; set; }

        public async Task<NoteDto?> GetNoteAsync(
            string noteId,
            CancellationToken cancellationToken)
        {
            if (LoadGate is { Task.IsCompleted: false } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return Notes.TryGetValue(noteId, out NoteDto? note)
                ? note
                : null;
        }

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
            return Task.FromResult(CreateNote(
                request.NoteId ?? string.Empty,
                request.Title ?? string.Empty,
                request.Body ?? string.Empty));
        }
    }
}
