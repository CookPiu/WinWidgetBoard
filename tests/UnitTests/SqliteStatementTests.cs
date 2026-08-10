using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class SqliteStatementTests
{
    [TestMethod(DisplayName = "UT-STORAGE-007 [DAT-001] Repository binds and reads typed values")]
    public async Task RepositoryBindsAndReadsTypedValues()
    {
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        database.Connection.ExecuteNonQuery(
            "CREATE TABLE samples (id TEXT NOT NULL PRIMARY KEY, count INTEGER NOT NULL, note TEXT NULL);");
        var repository = new SqliteRepository(database);

        int changes = repository.Execute(
            "INSERT INTO samples (id, count, note) VALUES (@id, @count, @note);",
            statement =>
            {
                statement.BindText("@id", "sample-1");
                statement.BindInt("@count", 3);
                statement.BindNull("@note");
            });

        Assert.AreEqual(1, changes);

        string? id = null;
        int count = 0;
        bool noteWasNull = false;
        repository.Query(
            "SELECT id, count, note FROM samples WHERE id = @id;",
            statement => statement.BindText("@id", "sample-1"),
            statement =>
            {
                id = statement.ReadText(0);
                count = statement.ReadInt(1);
                noteWasNull = statement.IsNull(2);
            });

        Assert.AreEqual("sample-1", id);
        Assert.AreEqual(3, count);
        Assert.IsTrue(noteWasNull);
    }

    [TestMethod(DisplayName = "UT-STORAGE-008 [NFR-REL-002] Repository transaction rolls back")]
    public async Task RepositoryTransactionRollsBack()
    {
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        database.Connection.ExecuteNonQuery(
            "CREATE TABLE samples (id TEXT NOT NULL PRIMARY KEY);");
        var repository = new SqliteRepository(database);

        using (SqliteTransaction transaction = repository.BeginTransaction())
        {
            repository.Execute(
                "INSERT INTO samples (id) VALUES (@id);",
                statement => statement.BindText("@id", "rolled-back"));
            transaction.Rollback();
        }

        Assert.AreEqual(
            "0",
            database.Connection.ExecuteScalar("SELECT COUNT(*) FROM samples;"));
    }

    [TestMethod(DisplayName = "UT-STORAGE-009 [NFR-REL-002] Repository transaction commits")]
    public async Task RepositoryTransactionCommits()
    {
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        database.Connection.ExecuteNonQuery(
            "CREATE TABLE samples (id TEXT NOT NULL PRIMARY KEY);");
        var repository = new SqliteRepository(database);

        using (SqliteTransaction transaction = repository.BeginTransaction())
        {
            repository.Execute(
                "INSERT INTO samples (id) VALUES (@id);",
                statement => statement.BindText("@id", "committed"));
            transaction.Commit();
        }

        Assert.AreEqual(
            "1",
            database.Connection.ExecuteScalar("SELECT COUNT(*) FROM samples;") );
    }
}
