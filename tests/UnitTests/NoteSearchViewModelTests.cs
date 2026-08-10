using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteSearchViewModelTests
{
    [TestMethod(DisplayName = "UT-NOTE-014 [NTE-001] Search maps matching notes to bounded previews")]
    public async Task SearchMapsMatchingNotesToBoundedPreviews()
    {
        var fake = new FakeNoteClient
        {
            SearchResults =
            [
                new NoteDto
                {
                    NoteId = "note-1",
                    Title = "Alpha note",
                    Body = "first line\nsecond line",
                },
            ],
        };
        await using var viewModel = new NoteSearchViewModel(
            fake,
            searchDebounce: TimeSpan.FromMilliseconds(10));

        Assert.IsTrue(await viewModel.SearchAsync("alpha"));

        Assert.AreEqual(1, fake.Queries.Count);
        Assert.AreEqual("alpha", fake.Queries[0]);
        Assert.AreEqual(NoteSearchStatus.Ready, viewModel.Status);
        Assert.IsTrue(viewModel.HasResults);
        Assert.AreEqual(1, viewModel.Results.Count);
        Assert.AreEqual("Alpha note", viewModel.Results[0].Title);
        Assert.AreEqual("first line second line", viewModel.Results[0].Preview);
    }

    [TestMethod(DisplayName = "UT-NOTE-028 [NTE-001] List loads all saved notes through an empty search query")]
    public async Task ListLoadsAllSavedNotesThroughEmptySearchQuery()
    {
        var fake = new FakeNoteClient
        {
            ResultsByQuery =
            {
                [string.Empty] =
                [
                    new NoteDto
                    {
                        NoteId = "primary-note",
                        Title = "Primary",
                        Body = "saved body",
                    },
                    new NoteDto
                    {
                        NoteId = "secondary-note",
                        Title = "Secondary",
                        Body = "another body",
                    },
                ],
            },
        };
        await using var viewModel = new NoteSearchViewModel(
            fake,
            searchDebounce: TimeSpan.FromMilliseconds(10));

        Assert.IsTrue(await viewModel.LoadAllAsync());

        Assert.AreEqual(1, fake.Queries.Count);
        Assert.AreEqual(string.Empty, fake.Queries[0]);
        Assert.AreEqual(string.Empty, viewModel.Query);
        Assert.AreEqual(NoteSearchStatus.Ready, viewModel.Status);
        Assert.AreEqual(2, viewModel.Results.Count);
        Assert.AreEqual("primary-note", viewModel.Results[0].NoteId);
        Assert.AreEqual("Secondary", viewModel.Results[1].Title);
    }

    [TestMethod(DisplayName = "UT-NOTE-015 [NTE-001] Search cancels stale queries and keeps the latest result")]
    public async Task SearchCancelsStaleQueriesAndKeepsLatestResult()
    {
        var fake = new FakeNoteClient
        {
            SearchDelay = TimeSpan.FromMilliseconds(40),
        };
        fake.ResultsByQuery["first"] =
        [
            new NoteDto { NoteId = "first", Title = "First", Body = "old" },
        ];
        fake.ResultsByQuery["second"] =
        [
            new NoteDto { NoteId = "second", Title = "Second", Body = "latest" },
        ];
        await using var viewModel = new NoteSearchViewModel(
            fake,
            searchDebounce: TimeSpan.FromMilliseconds(10));

        Task<bool> firstSearch = viewModel.SearchAsync("first");
        await Task.Delay(15);
        Task<bool> secondSearch = viewModel.SearchAsync("second");

        Assert.IsFalse(await firstSearch);
        Assert.IsTrue(await secondSearch);
        Assert.AreEqual("second", viewModel.Query);
        Assert.AreEqual("Second", viewModel.Results[0].Title);
        Assert.AreEqual(NoteSearchStatus.Ready, viewModel.Status);
    }

    [TestMethod(DisplayName = "UT-NOTE-016 [NTE-001] Search exposes errors and clears results for an empty query")]
    public async Task SearchExposesErrorsAndClearsResultsForEmptyQuery()
    {
        var fake = new FakeNoteClient
        {
            SearchResults =
            [
                new NoteDto
                {
                    NoteId = "note-1",
                    Title = "Existing",
                    Body = "body",
                },
            ],
            SearchException = new CoreBrokerClientException(
                NotesContract.SearchMethod,
                "transport.unavailable",
                "test search failure"),
        };
        await using var viewModel = new NoteSearchViewModel(
            fake,
            searchDebounce: TimeSpan.FromMilliseconds(10));

        Assert.IsFalse(await viewModel.SearchAsync("failure"));
        Assert.AreEqual(NoteSearchStatus.Error, viewModel.Status);
        Assert.AreEqual("transport.unavailable", viewModel.ErrorCode);
        Assert.IsFalse(viewModel.HasResults);

        fake.SearchException = null;
        Assert.IsTrue(await viewModel.SearchAsync("existing"));
        Assert.IsTrue(viewModel.HasResults);

        Assert.IsTrue(await viewModel.SearchAsync("   "));
        Assert.AreEqual(string.Empty, viewModel.Query);
        Assert.AreEqual(NoteSearchStatus.Idle, viewModel.Status);
        Assert.IsFalse(viewModel.HasResults);
    }

    private sealed class FakeNoteClient : INoteClient
    {
        public IReadOnlyList<NoteDto> SearchResults { get; set; } = [];

        public Dictionary<string, IReadOnlyList<NoteDto>> ResultsByQuery { get; } = [];

        public TimeSpan SearchDelay { get; init; }

        public Exception? SearchException { get; set; }

        public List<string> Queries { get; } = [];

        public Task<NoteDto?> GetNoteAsync(
            string noteId,
            CancellationToken cancellationToken) =>
            Task.FromResult<NoteDto?>(null);

        public async Task<IReadOnlyList<NoteDto>> SearchNotesAsync(
            string query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);
            if (SearchDelay > TimeSpan.Zero)
            {
                await Task.Delay(SearchDelay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (SearchException is not null)
            {
                throw SearchException;
            }

            return ResultsByQuery.TryGetValue(query, out IReadOnlyList<NoteDto>? results)
                ? results
                : SearchResults;
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
