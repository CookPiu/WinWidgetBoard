using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteRepositoryTests
{
    [TestMethod(DisplayName = "UT-NOTE-001 [NTE-001/DAT-001] Note create, read and list persist content")]
    public async Task NoteCreateReadAndListPersistContent()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new NoteRepository(database);
        DateTimeOffset createdAt = new(2026, 8, 8, 1, 2, 3, TimeSpan.Zero);

        NoteRecord created = repository.Create(
            "note-1",
            "First note",
            "# Hello",
            NoteBodyFormat.Markdown,
            createdAt);

        NoteRecord? loaded = repository.Get("note-1");
        Assert.IsNotNull(loaded);
        Assert.AreEqual(created, loaded);
        Assert.AreEqual(1, repository.List().Count);
        Assert.AreEqual(NoteBodyFormat.Markdown, loaded.BodyFormat);
        Assert.AreEqual("# Hello", loaded.Body);
    }

    [TestMethod(DisplayName = "UT-NOTE-027 [NTE-001/AC-005] Note reopens with the last committed state")]
    public async Task NoteReopensWithLastCommittedState()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "WinWidgetBoard-NoteRecovery-" + Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(testDirectory, "data.db");

        try
        {
            await using (SqliteDatabase database = await SqliteDatabase.OpenAsync(
                new SqliteDatabaseOptions(databasePath)))
            {
                await database.ApplySchemaAsync();
                var repository = new NoteRepository(database);
                NoteRecord initial = repository.Create(
                    "primary-note",
                    "Initial title",
                    "Initial body",
                    NoteBodyFormat.PlainText,
                    new DateTimeOffset(2026, 8, 10, 1, 2, 3, TimeSpan.Zero));

                repository.Update(
                    initial.NoteId,
                    initial.UpdatedAtUtc,
                    "Committed title",
                    "Committed body",
                    NoteBodyFormat.Markdown);
            }

            await using (SqliteDatabase reopened = await SqliteDatabase.OpenAsync(
                new SqliteDatabaseOptions(databasePath)))
            {
                await reopened.ApplySchemaAsync();
                NoteRecord? loaded = new NoteRepository(reopened).Get("primary-note");

                Assert.IsNotNull(loaded);
                Assert.AreEqual("Committed title", loaded.Title);
                Assert.AreEqual("Committed body", loaded.Body);
                Assert.AreEqual(NoteBodyFormat.Markdown, loaded.BodyFormat);
            }
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [TestMethod(DisplayName = "UT-NOTE-002 [NTE-001] Note search escapes LIKE wildcards")]
    public async Task NoteSearchEscapesLikeWildcards()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new NoteRepository(database);
        repository.Create("note-1", "100% plan", "Keep this", NoteBodyFormat.PlainText);
        repository.Create("note-2", "Other", "1000 plan", NoteBodyFormat.PlainText);

        IReadOnlyList<NoteRecord> matches = repository.Search("100%");

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual("note-1", matches[0].NoteId);
    }

    [TestMethod(DisplayName = "UT-NOTE-003 [NTE-001/NFR-REL-002] Note update uses revision conflict protection")]
    public async Task NoteUpdateUsesRevisionConflictProtection()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new NoteRepository(database);
        NoteRecord initial = repository.Create(
            "note-1",
            "First",
            "Original",
            NoteBodyFormat.PlainText,
            new DateTimeOffset(2026, 8, 8, 1, 2, 3, TimeSpan.Zero));

        NoteRecord updated = repository.Update(
            initial.NoteId,
            initial.UpdatedAtUtc,
            "Changed",
            "Updated",
            NoteBodyFormat.Markdown);

        Assert.AreEqual("Changed", updated.Title);
        Assert.AreEqual(NoteBodyFormat.Markdown, updated.BodyFormat);
        Assert.AreNotEqual(initial.UpdatedAtUtc, updated.UpdatedAtUtc);

        NoteRevisionConflictException conflict = await Assert.ThrowsAsync<NoteRevisionConflictException>(
            () => Task.FromResult(repository.Update(
                initial.NoteId,
                initial.UpdatedAtUtc,
                "Stale",
                "Must not win",
                NoteBodyFormat.PlainText)));
        Assert.AreEqual(initial.NoteId, conflict.NoteId);
        Assert.AreEqual("Updated", repository.Get(initial.NoteId)!.Body);
    }

    [TestMethod(DisplayName = "UT-NOTE-004 [NTE-001/NFR-REL-002] Note delete requires current revision")]
    public async Task NoteDeleteRequiresCurrentRevision()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new NoteRepository(database);
        NoteRecord note = repository.Create(
            "note-1",
            "First",
            "Original",
            NoteBodyFormat.PlainText);

        Assert.Throws<NoteRevisionConflictException>(() => repository.Delete(
            note.NoteId,
            "2026-08-08T00:00:00.0000000Z"));

        repository.Delete(note.NoteId, note.UpdatedAtUtc);
        Assert.IsNull(repository.Get(note.NoteId));
    }

    [TestMethod(DisplayName = "UT-NOTE-005 [NTE-001/NFR-REL-002] Missing note is reported distinctly")]
    public async Task MissingNoteIsReportedDistinctly()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new NoteRepository(database);

        NoteNotFoundException exception = await Assert.ThrowsAsync<NoteNotFoundException>(
            () => Task.FromResult(repository.Update(
                "missing",
                "2026-08-08T00:00:00.0000000Z",
                "Title",
                "Body",
                NoteBodyFormat.PlainText)));

        Assert.AreEqual("missing", exception.NoteId);
    }

    private static async Task<SqliteDatabase> OpenDatabaseAsync()
    {
        SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        await database.ApplySchemaAsync();
        return database;
    }
}
