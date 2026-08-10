using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteAutosaveCoordinatorTests
{
    [TestMethod(DisplayName = "UT-NOTE-006 [NTE-001] Autosave persists after debounce")]
    public async Task AutosavePersistsAfterDebounce()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new NoteRepository(database);
        await using var coordinator = new NoteAutosaveCoordinator(
            repository,
            TimeSpan.FromMilliseconds(10));

        NoteAutosaveResult result = await coordinator.Schedule(new NoteAutosaveDraft(
            "note-1",
            "Draft",
            "Saved body",
            NoteBodyFormat.PlainText));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("Saved body", repository.Get("note-1")!.Body);
        Assert.IsNull(coordinator.InMemoryDraft);
    }

    [TestMethod(DisplayName = "UT-NOTE-007 [NTE-001] Autosave coalesces to latest input")]
    public async Task AutosaveCoalescesToLatestInput()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new NoteRepository(database);
        await using var coordinator = new NoteAutosaveCoordinator(
            repository,
            TimeSpan.FromMilliseconds(40));

        Task<NoteAutosaveResult> first = coordinator.Schedule(new NoteAutosaveDraft(
            "note-1",
            "Draft",
            "First",
            NoteBodyFormat.PlainText));
        Task<NoteAutosaveResult> second = coordinator.Schedule(new NoteAutosaveDraft(
            "note-1",
            "Draft",
            "Latest",
            NoteBodyFormat.PlainText));

        await Assert.ThrowsAsync<TaskCanceledException>(() => first);
        NoteAutosaveResult result = await second;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("Latest", repository.Get("note-1")!.Body);
    }

    [TestMethod(DisplayName = "UT-NOTE-008 [NTE-001/NFR-REL-002] Autosave failure retains memory draft")]
    public async Task AutosaveFailureRetainsMemoryDraft()
    {
        var injector = new PersistenceFaultInjector();
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:", faultInjector: injector));
        await database.ApplySchemaAsync();
        injector.Inject(
            PersistenceFaultPoint.Write,
            new InvalidOperationException("injected autosave failure"),
            once: true);
        var repository = new NoteRepository(database);
        await using var coordinator = new NoteAutosaveCoordinator(
            repository,
            TimeSpan.FromMilliseconds(10));
        var draft = new NoteAutosaveDraft(
            "note-1",
            "Draft",
            "Keep in memory",
            NoteBodyFormat.Markdown);

        NoteAutosaveResult failed = await coordinator.Schedule(draft);

        Assert.IsFalse(failed.Succeeded);
        Assert.IsInstanceOfType<InvalidOperationException>(failed.Error);
        Assert.AreEqual(draft, coordinator.InMemoryDraft);

        NoteAutosaveResult retry = await coordinator.Schedule(draft);
        Assert.IsTrue(retry.Succeeded);
        Assert.IsNull(coordinator.InMemoryDraft);
    }

    private static async Task<SqliteDatabase> OpenDatabaseAsync()
    {
        SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        await database.ApplySchemaAsync();
        return database;
    }
}
