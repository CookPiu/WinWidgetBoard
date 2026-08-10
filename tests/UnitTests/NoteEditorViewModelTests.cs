using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteEditorViewModelTests
{
    [TestMethod(DisplayName = "UT-NOTE-006 [NTE-001] Editor loads an existing note without writing")]
    public async Task EditorLoadsExistingNoteWithoutWriting()
    {
        var fake = new FakeNoteClient
        {
            Existing = new NoteDto
            {
                NoteId = NoteEditorViewModel.DefaultNoteId,
                Title = "Existing title",
                Body = "Existing body",
                BodyFormat = NotesContract.PlainTextFormat,
                CreatedAtUtc = "2026-08-08T00:00:00.0000000+00:00",
                UpdatedAtUtc = "2026-08-08T00:00:01.0000000+00:00",
            },
        };
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(20));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.AreEqual("Existing title", viewModel.Title);
        Assert.AreEqual("Existing body", viewModel.Body);
        Assert.AreEqual(NoteEditorStatus.Ready, viewModel.Status);
        Assert.AreEqual(0, fake.Saves.Count);
    }

    [TestMethod(DisplayName = "UT-NOTE-007 [NTE-001] Editor debounces input and saves the latest draft")]
    public async Task EditorDebouncesInputAndSavesLatestDraft()
    {
        var fake = new FakeNoteClient();
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(40));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Title = "Latest title";
        viewModel.Body = "first";
        await Task.Delay(5);
        viewModel.Body = "latest";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await viewModel.WaitForIdleAsync(timeout.Token);

        Assert.AreEqual(1, fake.Saves.Count);
        Assert.AreEqual("Latest title", fake.Saves[0].Title);
        Assert.AreEqual("latest", fake.Saves[0].Body);
        Assert.AreEqual(NoteEditorStatus.Saved, viewModel.Status);
        Assert.IsFalse(viewModel.HasUnsavedChanges);
    }

    [TestMethod(DisplayName = "UT-NOTE-008 [NTE-001] Editor keeps the draft visible after a save failure")]
    public async Task EditorKeepsDraftAfterSaveFailure()
    {
        var fake = new FakeNoteClient
        {
            SaveException = new CoreBrokerClientException(
                NotesContract.SaveMethod,
                "transport.unavailable",
                "test failure"),
        };
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(20));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "draft must stay visible";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await viewModel.WaitForIdleAsync(timeout.Token);

        Assert.AreEqual("draft must stay visible", viewModel.Body);
        Assert.IsTrue(viewModel.HasUnsavedChanges);
        Assert.AreEqual(NoteEditorStatus.Error, viewModel.Status);
        Assert.AreEqual("transport.unavailable", viewModel.ErrorCode);
        Assert.IsTrue(viewModel.CanRetrySave);
    }

    [TestMethod(DisplayName = "UT-NOTE-012 [NTE-001] Editor retries a failed save with the retained draft")]
    public async Task EditorRetriesFailedSaveWithRetainedDraft()
    {
        var fake = new FakeNoteClient();
        fake.SaveFailures.Enqueue(new CoreBrokerClientException(
            NotesContract.SaveMethod,
            "transport.unavailable",
            "transient test failure"));
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(20));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Title = "Retry title";
        viewModel.Body = "Retry body";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await viewModel.WaitForIdleAsync(timeout.Token);

        Assert.AreEqual(NoteEditorStatus.Error, viewModel.Status);
        Assert.IsTrue(viewModel.CanRetrySave);
        Assert.IsTrue(await viewModel.RetrySaveAsync(timeout.Token));

        await viewModel.WaitForIdleAsync(timeout.Token);
        Assert.AreEqual(2, fake.Saves.Count);
        Assert.AreEqual("Retry title", fake.Saves[1].Title);
        Assert.AreEqual("Retry body", fake.Saves[1].Body);
        Assert.AreEqual(NoteEditorStatus.Saved, viewModel.Status);
        Assert.IsFalse(viewModel.HasUnsavedChanges);
        Assert.IsFalse(viewModel.CanRetrySave);
    }

    [TestMethod(DisplayName = "UT-NOTE-029 [NTE-001] Editor creates and switches to a new saved note")]
    public async Task EditorCreatesAndSwitchesToNewSavedNote()
    {
        var fake = new FakeNoteClient
        {
            Existing = new NoteDto
            {
                NoteId = NoteEditorViewModel.DefaultNoteId,
                Title = "Primary",
                Body = "Existing body",
                BodyFormat = NotesContract.PlainTextFormat,
                CreatedAtUtc = "2026-08-10T00:00:00.0000000+00:00",
                UpdatedAtUtc = "2026-08-10T00:00:01.0000000+00:00",
            },
        };
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromSeconds(1));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsTrue(await viewModel.CreateNoteAsync(CancellationToken.None));

        Assert.IsTrue(viewModel.NoteId.StartsWith("note-", StringComparison.Ordinal));
        Assert.AreNotEqual(NoteEditorViewModel.DefaultNoteId, viewModel.NoteId);
        Assert.AreEqual(string.Empty, viewModel.Title);
        Assert.AreEqual(string.Empty, viewModel.Body);
        Assert.AreEqual(NoteEditorStatus.Saved, viewModel.Status);
        Assert.IsFalse(viewModel.HasUnsavedChanges);
        Assert.AreEqual(1, fake.Saves.Count);
        Assert.AreEqual(viewModel.NoteId, fake.Saves[0].NoteId);
        Assert.IsNull(fake.Saves[0].ExpectedUpdatedAtUtc);
        Assert.AreEqual(NotesContract.PlainTextFormat, fake.Saves[0].BodyFormat);
    }

    [TestMethod(DisplayName = "UT-NOTE-030 [NTE-001] Editor refuses to create a note while a draft is unsaved")]
    public async Task EditorRefusesToCreateNoteWhileDraftIsUnsaved()
    {
        var fake = new FakeNoteClient
        {
            Existing = new NoteDto
            {
                NoteId = NoteEditorViewModel.DefaultNoteId,
                Title = "Primary",
                Body = "Existing body",
                BodyFormat = NotesContract.PlainTextFormat,
                CreatedAtUtc = "2026-08-10T00:00:00.0000000+00:00",
                UpdatedAtUtc = "2026-08-10T00:00:01.0000000+00:00",
            },
        };
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromSeconds(1));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "unsaved draft";

        Assert.IsFalse(await viewModel.CreateNoteAsync(CancellationToken.None));
        Assert.AreEqual(NoteEditorViewModel.DefaultNoteId, viewModel.NoteId);
        Assert.AreEqual("unsaved draft", viewModel.Body);
        Assert.IsTrue(viewModel.HasUnsavedChanges);
        Assert.AreEqual(0, fake.Saves.Count);
    }

    [TestMethod(DisplayName = "UT-NOTE-031 [NTE-001] Editor keeps the current note when creation fails")]
    public async Task EditorKeepsCurrentNoteWhenCreationFails()
    {
        var fake = new FakeNoteClient
        {
            Existing = new NoteDto
            {
                NoteId = NoteEditorViewModel.DefaultNoteId,
                Title = "Current title",
                Body = "Current body",
                BodyFormat = NotesContract.MarkdownFormat,
                CreatedAtUtc = "2026-08-10T00:00:00.0000000+00:00",
                UpdatedAtUtc = "2026-08-10T00:00:01.0000000+00:00",
            },
            SaveException = new CoreBrokerClientException(
                NotesContract.SaveMethod,
                "transport.unavailable",
                "create failure"),
        };
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromSeconds(1));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsFalse(await viewModel.CreateNoteAsync(CancellationToken.None));

        Assert.AreEqual(NoteEditorViewModel.DefaultNoteId, viewModel.NoteId);
        Assert.AreEqual("Current title", viewModel.Title);
        Assert.AreEqual("Current body", viewModel.Body);
        Assert.AreEqual(NoteEditorStatus.Error, viewModel.Status);
        Assert.AreEqual("transport.unavailable", viewModel.ErrorCode);
        Assert.IsTrue(viewModel.CanLoadNote);
    }

    [TestMethod(DisplayName = "UT-NOTE-013 [NTE-001] Editor keeps retry available after a second save failure")]
    public async Task EditorKeepsRetryAvailableAfterSecondSaveFailure()
    {
        var fake = new FakeNoteClient();
        fake.SaveFailures.Enqueue(new CoreBrokerClientException(
            NotesContract.SaveMethod,
            "transport.unavailable",
            "first test failure"));
        fake.SaveFailures.Enqueue(new CoreBrokerClientException(
            NotesContract.SaveMethod,
            "transport.timeout",
            "second test failure"));
        await using var viewModel = new NoteEditorViewModel(
            fake,
            autosaveDelay: TimeSpan.FromMilliseconds(20));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.Body = "draft survives another failure";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await viewModel.WaitForIdleAsync(timeout.Token);
        Assert.IsTrue(viewModel.CanRetrySave);

        Assert.IsFalse(await viewModel.RetrySaveAsync(timeout.Token));
        await viewModel.WaitForIdleAsync(timeout.Token);

        Assert.AreEqual("draft survives another failure", viewModel.Body);
        Assert.IsTrue(viewModel.HasUnsavedChanges);
        Assert.AreEqual(NoteEditorStatus.Error, viewModel.Status);
        Assert.AreEqual("transport.timeout", viewModel.ErrorCode);
        Assert.IsTrue(viewModel.CanRetrySave);
    }

    [TestMethod(DisplayName = "UT-NOTE-009 [NTE-001] Editor leaves loading when CoreBroker is unavailable")]
    public async Task EditorLeavesLoadingWhenCoreBrokerIsUnavailable()
    {
        await using var viewModel = new NoteEditorViewModel(
            new FakeNoteClient(),
            autosaveDelay: TimeSpan.FromMilliseconds(20));

        Assert.AreEqual(NoteEditorStatus.Loading, viewModel.Status);
        viewModel.MarkUnavailable();

        Assert.AreEqual(NoteEditorStatus.Unavailable, viewModel.Status);
        Assert.IsFalse(viewModel.IsLoading);
        Assert.IsFalse(viewModel.CanEdit);
        Assert.AreEqual("transport.unavailable", viewModel.ErrorCode);
    }

    private sealed class FakeNoteClient : INoteClient
    {
        public NoteDto? Existing { get; init; }

        public Exception? SaveException { get; init; }

        public Queue<Exception> SaveFailures { get; } = [];

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
            if (SaveFailures.TryDequeue(out Exception? queuedException))
            {
                return Task.FromException<NoteDto>(queuedException);
            }

            if (SaveException is not null)
            {
                return Task.FromException<NoteDto>(SaveException);
            }

            return Task.FromResult(new NoteDto
            {
                NoteId = request.NoteId!,
                Title = request.Title!,
                Body = request.Body!,
                BodyFormat = request.BodyFormat!,
                CreatedAtUtc = "2026-08-08T00:00:00.0000000+00:00",
                UpdatedAtUtc = $"2026-08-08T00:00:00.{Saves.Count:0000000}+00:00",
            });
        }
    }
}
