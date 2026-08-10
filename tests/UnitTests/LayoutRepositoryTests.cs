using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class LayoutRepositoryTests
{
    [TestMethod(DisplayName = "UT-LAYOUT-001 [LYT-006/DAT-001] Layout save and reload preserve order, size and logical cell")]
    public async Task LayoutSaveAndReloadPreserveLayoutItems()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new LayoutRepository(database);
        LayoutItemRecord[] items =
        [
            new("demo.notes", 0, 2, 2, "l", 0, 3),
            new("demo.timer", 1, 2, 1, "m", 2, 1),
        ];

        LayoutRecord saved = repository.Save(
            "primary-default",
            "primary",
            expectedRevision: 0,
            items,
            new DateTimeOffset(2026, 8, 8, 1, 2, 3, TimeSpan.Zero));

        Assert.AreEqual(1, saved.Revision);
        LayoutRecord loaded = repository.Get("primary-default", "primary")!;
        Assert.AreEqual(saved.LayoutId, loaded.LayoutId);
        Assert.AreEqual(saved.DisplayId, loaded.DisplayId);
        Assert.AreEqual(saved.Revision, loaded.Revision);
        Assert.AreEqual(saved.UpdatedAtUtc, loaded.UpdatedAtUtc);
        CollectionAssert.AreEqual(items, loaded.Items.ToArray());
    }

    [TestMethod(DisplayName = "UT-LAYOUT-002 [LYT-006/NFR-REL-002] Layout save enforces optimistic revision")]
    public async Task LayoutSaveEnforcesRevision()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new LayoutRepository(database);
        LayoutItemRecord[] items =
        [new("demo.notes", 0, 2, 2, "l", 0)];

        LayoutRecord first = repository.Save(
            "primary-default",
            "primary",
            0,
            items);

        LayoutRevisionConflictException conflict = Assert.Throws<LayoutRevisionConflictException>(() =>
            repository.Save("primary-default", "primary", 0, items));

        Assert.AreEqual(first.Revision, conflict.ActualRevision);
        Assert.AreEqual(1, repository.Get("primary-default", "primary")!.Revision);
    }

    [TestMethod(DisplayName = "UT-LAYOUT-003 [LYT-006] Invalid layout order is rejected before storage mutation")]
    public async Task InvalidLayoutOrderIsRejected()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new LayoutRepository(database);

        Assert.Throws<ArgumentException>(() => repository.Save(
            "primary-default",
            "primary",
            0,
            [
                new LayoutItemRecord("demo.notes", 1, 2, 2, "l", 0),
            ]));

        Assert.IsNull(repository.Get("primary-default", "primary"));
    }

    private static async Task<SqliteDatabase> OpenDatabaseAsync()
    {
        SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        await database.ApplySchemaAsync();
        return database;
    }
}
