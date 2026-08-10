using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class PersistenceFaultInjectionTests
{
    [TestMethod(DisplayName = "UT-STORAGE-010 [NFR-REL-003] Open fault is deterministic and one-shot")]
    public async Task OpenFaultIsDeterministicAndOneShot()
    {
        var injector = new PersistenceFaultInjector();
        injector.Inject(
            PersistenceFaultPoint.Open,
            new InvalidOperationException("injected open failure"),
            once: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:", faultInjector: injector)));

        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:", faultInjector: injector));
        Assert.IsTrue(database.Connection.IsOpen);
    }

    [TestMethod(DisplayName = "UT-STORAGE-011 [NFR-REL-003] Migration fault rolls back and retry succeeds")]
    public async Task MigrationFaultRollsBackAndRetrySucceeds()
    {
        var injector = new PersistenceFaultInjector();
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:", faultInjector: injector));
        injector.Inject(
            PersistenceFaultPoint.Migration,
            new InvalidOperationException("injected migration failure"),
            version: 1,
            once: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.ApplySchemaAsync());
        Assert.AreEqual(
            "0",
            database.Connection.ExecuteScalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"));

        Assert.AreEqual(2, await database.ApplySchemaAsync());
        Assert.AreEqual("2", database.Connection.ExecuteScalar("PRAGMA user_version;"));
    }

    [TestMethod(DisplayName = "UT-STORAGE-012 [NFR-REL-002] Write fault leaves migration transaction clean")]
    public async Task WriteFaultLeavesMigrationTransactionClean()
    {
        var injector = new PersistenceFaultInjector();
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:", faultInjector: injector));
        injector.Inject(
            PersistenceFaultPoint.Write,
            new InvalidOperationException("injected write failure"),
            once: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.ApplySchemaAsync());
        Assert.AreEqual(
            "0",
            database.Connection.ExecuteScalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"));

        Assert.AreEqual(2, await database.ApplySchemaAsync());
    }

    [TestMethod(DisplayName = "UT-STORAGE-013 [NFR-REL-003] Commit fault rolls back migration")]
    public async Task CommitFaultRollsBackMigration()
    {
        var injector = new PersistenceFaultInjector();
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        var runner = new SqliteMigrationRunner(
            new[]
            {
                new SqliteMigration(
                    1,
                    "commit-fault",
                    "CREATE TABLE commit_marker (id INTEGER NOT NULL);")
            },
            new NoopBackup(),
            injector);
        injector.Inject(
            PersistenceFaultPoint.Commit,
            new InvalidOperationException("injected commit failure"),
            once: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync(
            database.Connection,
            database.Options));

        Assert.AreEqual(
            "0",
            database.Connection.ExecuteScalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'commit_marker';"));
    }

    [TestMethod(DisplayName = "UT-STORAGE-014 [NFR-REL-003] Backup fault prevents migration")]
    public async Task BackupFaultPreventsMigration()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "data.db");

        try
        {
            await using (SqliteDatabase first = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath)))
            {
                Assert.AreEqual(2, await first.ApplySchemaAsync());
            }

            var injector = new PersistenceFaultInjector();
            injector.Inject(
                PersistenceFaultPoint.Backup,
                new InvalidOperationException("injected backup failure"),
                version: 3,
                once: true);
            var runner = new SqliteMigrationRunner(
                new[]
                {
                    SqliteSchema.Migrations[0],
                    SqliteSchema.Migrations[1],
                    new SqliteMigration(
                        3,
                        "backup-fault",
                        "CREATE TABLE backup_marker (id INTEGER NOT NULL);")
                },
                faultInjector: injector);

            await using SqliteDatabase second = await SqliteDatabase.OpenAsync(
                new SqliteDatabaseOptions(databasePath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ApplyAsync(
                second.Connection,
                second.Options));
            Assert.AreEqual(
                "0",
                second.Connection.ExecuteScalar(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'backup_marker';"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod(DisplayName = "UT-STORAGE-015 [NFR-REL-003] Recovery fault does not create a copy")]
    public void RecoveryFaultDoesNotCreateACopy()
    {
        string root = CreateTempDirectory();
        string backupPath = Path.Combine(root, "data.bak");
        string recoveryDirectory = Path.Combine(root, "Recovery");
        File.WriteAllText(backupPath, "backup");

        try
        {
            var injector = new PersistenceFaultInjector();
            injector.Inject(
                PersistenceFaultPoint.Restore,
                new InvalidOperationException("injected restore failure"));
            var recovery = new SqliteRecoveryManager(injector);

            Assert.Throws<InvalidOperationException>(() => recovery.RestoreToRecoveryCopy(
                backupPath,
                recoveryDirectory));
            Assert.IsFalse(Directory.Exists(recoveryDirectory));

            injector.Clear();
            string restoredPath = recovery.RestoreToRecoveryCopy(backupPath, recoveryDirectory);
            Assert.IsTrue(File.Exists(restoredPath));
            Assert.AreEqual("backup", File.ReadAllText(restoredPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "WinWidgetBoard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class NoopBackup : ISqliteMigrationBackup
    {
        public string? Create(
            SqliteConnection source,
            SqliteDatabaseOptions options,
            int firstPendingVersion,
            CancellationToken cancellationToken) => null;
    }
}
