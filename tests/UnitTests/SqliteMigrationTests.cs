using System.Globalization;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class SqliteMigrationTests
{
    private static string LatestUserVersion =>
        SqliteSchema.Migrations[^1].Version.ToString(CultureInfo.InvariantCulture);

    [TestMethod(DisplayName = "UT-STORAGE-001 [DAT-001] Initial schema creates core tables and constraints")]
    public async Task InitialSchemaCreatesCoreTablesAndConstraints()
    {
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));

        int applied = await database.ApplySchemaAsync();

        Assert.AreEqual(SqliteSchema.Migrations.Count, applied);
        Assert.AreEqual(
            LatestUserVersion,
            await ScalarTextAsync(database.Connection, "PRAGMA user_version;"));
        foreach (string table in new[]
                 {
                     "schema_migrations",
                     "card_definitions",
                     "card_instances",
                     "layouts",
                     "layout_items",
                     "notes",
                     "weather_settings",
                     "todos",
                     "calendar_events",
                     "timers",
                 })
        {
            Assert.IsTrue(await TableExistsAsync(database.Connection, table), table);
        }

        Assert.IsFalse(await TableExistsAsync(database.Connection, "clipboard_history"));
        Assert.IsFalse(await TableExistsAsync(database.Connection, "clipboard_items"));

        await InsertAsync(
            database.Connection,
            "INSERT INTO card_definitions (card_type_id, display_name_key, description_key, version, source, settings_schema_version, ui_schema_version, created_at_utc, updated_at_utc) VALUES ('app.test.card', 'card.name', 'card.description', '1.0.0', 'builtin', 1, 1, '2026-08-08T00:00:00.0000000Z', '2026-08-08T00:00:00.0000000Z');");
        await InsertAsync(
            database.Connection,
            "INSERT INTO card_instances (instance_id, card_type_id, size_id, created_at_utc, updated_at_utc) VALUES ('instance-1', 'app.test.card', 'm', '2026-08-08T00:00:00.0000000Z', '2026-08-08T00:00:00.0000000Z');");
        await Assert.ThrowsAsync<SqliteException>(() => InsertAsync(
            database.Connection,
            "INSERT INTO card_instances (instance_id, card_type_id, size_id, created_at_utc, updated_at_utc) VALUES ('instance-2', 'missing.card', 'm', '2026-08-08T00:00:00.0000000Z', '2026-08-08T00:00:00.0000000Z');"));
    }

    [TestMethod(DisplayName = "UT-STORAGE-002 [DAT-001] Reopening database does not repeat migration")]
    public async Task ReopeningDatabaseDoesNotRepeatMigration()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "data.db");

        try
        {
            await using (SqliteDatabase first = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath)))
            {
                Assert.AreEqual(
                    SqliteSchema.Migrations.Count,
                    await first.ApplySchemaAsync());
            }

            await using (SqliteDatabase second = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath)))
            {
                Assert.AreEqual(0, await second.ApplySchemaAsync());
                Assert.AreEqual(1, await ScalarIntAsync(
                    second.Connection,
                    "SELECT COUNT(*) FROM schema_migrations WHERE version = 1;"));
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod(DisplayName = "UT-STORAGE-003 [NFR-REL-002] Failed migration rolls back all statements")]
    public async Task FailedMigrationRollsBackAllStatements()
    {
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        var runner = new SqliteMigrationRunner(
            new[]
            {
                new SqliteMigration(
                    1,
                    "intentionally-failing",
                    "CREATE TABLE should_rollback (id INTEGER NOT NULL); THIS IS NOT VALID SQL;"),
            },
            new NoopBackup());

        await Assert.ThrowsAsync<SqliteException>(() => runner.ApplyAsync(
            database.Connection,
            database.Options));

        Assert.IsFalse(await TableExistsAsync(database.Connection, "should_rollback"));
        Assert.AreEqual(0, await ScalarIntAsync(
            database.Connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"));
    }

    [TestMethod(DisplayName = "UT-STORAGE-004 [NFR-REL-003] Existing file gets backup before pending migration")]
    public async Task ExistingFileGetsBackupBeforePendingMigration()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "data.db");
        string backupDirectory = Path.Combine(root, "Backups");

        try
        {
            await using (SqliteDatabase first = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath, backupDirectory)))
            {
                var legacyRunner = new SqliteMigrationRunner(
                    new[] { SqliteSchema.Migrations[0], SqliteSchema.Migrations[1] },
                    new NoopBackup());
                Assert.AreEqual(2, await legacyRunner.ApplyAsync(
                    first.Connection,
                    first.Options));
            }

            var runner = new SqliteMigrationRunner(
                new[]
                {
                    SqliteSchema.Migrations[0],
                    SqliteSchema.Migrations[1],
                    new SqliteMigration(
                        3,
                        "test-third-migration",
                        "CREATE TABLE backup_marker (id INTEGER NOT NULL);"),
                });
            await using (SqliteDatabase second = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath, backupDirectory)))
            {
                Assert.AreEqual(1, await runner.ApplyAsync(second.Connection, second.Options));
                Assert.IsTrue(await TableExistsAsync(second.Connection, "backup_marker"));
            }

            string[] backups = Directory.GetFiles(backupDirectory, "data.db.pre-migration-v3-*.bak");
            Assert.AreEqual(1, backups.Length);
            Assert.IsTrue(new FileInfo(backups[0]).Length > 0);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod(DisplayName = "UT-STORAGE-005 [NFR-REL-003] Backup failure stops migration")]
    public async Task BackupFailureStopsMigration()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "data.db");

        try
        {
            await using (SqliteDatabase first = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath)))
            {
                var legacyRunner = new SqliteMigrationRunner(
                    new[] { SqliteSchema.Migrations[0], SqliteSchema.Migrations[1] },
                    new NoopBackup());
                Assert.AreEqual(2, await legacyRunner.ApplyAsync(
                    first.Connection,
                    first.Options));
            }

            var runner = new SqliteMigrationRunner(
                new[]
                {
                    SqliteSchema.Migrations[0],
                    SqliteSchema.Migrations[1],
                    new SqliteMigration(
                        3,
                        "must-not-run",
                        "CREATE TABLE must_not_run (id INTEGER NOT NULL);"),
                },
                new FailingBackup());
            await using (SqliteDatabase second = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath)))
            {
                await Assert.ThrowsAsync<SqliteMigrationBackupException>(() => runner.ApplyAsync(
                    second.Connection,
                    second.Options));
                Assert.IsFalse(await TableExistsAsync(second.Connection, "must_not_run"));
                Assert.AreEqual(1, await ScalarIntAsync(
                    second.Connection,
                    "SELECT COUNT(*) FROM schema_migrations WHERE version = 1;"));
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod(DisplayName = "UT-STORAGE-006 [NFR-SEC-002] File uses foreign keys and WAL; memory uses private mode")]
    public async Task FileUsesForeignKeysAndWalMemoryUsesPrivateMode()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "data.db");

        try
        {
            await using (SqliteDatabase fileDatabase = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath)))
            {
                Assert.AreEqual(1, await ScalarIntAsync(fileDatabase.Connection, "PRAGMA foreign_keys;"));
                Assert.AreEqual("wal", await ScalarTextAsync(fileDatabase.Connection, "PRAGMA journal_mode;"));
            }

            await using (SqliteDatabase memoryDatabase = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(":memory:")))
            {
                Assert.AreEqual(1, await ScalarIntAsync(memoryDatabase.Connection, "PRAGMA foreign_keys;"));
                Assert.AreNotEqual("wal", await ScalarTextAsync(memoryDatabase.Connection, "PRAGMA journal_mode;"));
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod(DisplayName = "UT-STORAGE-007 [DAT-001/NFR-REL-003] Existing v1 layout file gains the logical row column")]
    public async Task ExistingV1FileMigratesToLogicalLayoutRow()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "data.db");

        try
        {
            await using (SqliteDatabase legacy = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath)))
            {
                var legacyRunner = new SqliteMigrationRunner(
                    new[] { SqliteSchema.Migrations[0] },
                    new NoopBackup());
                Assert.AreEqual(1, await legacyRunner.ApplyAsync(
                    legacy.Connection,
                    legacy.Options));
                Assert.AreEqual("1", await ScalarTextAsync(
                    legacy.Connection,
                    "PRAGMA user_version;"));
            }

            await using (SqliteDatabase upgraded = await SqliteDatabase.OpenAsync(
                             new SqliteDatabaseOptions(databasePath)))
            {
                Assert.AreEqual(
                    SqliteSchema.Migrations.Count - 1,
                    await upgraded.ApplySchemaAsync(),
                    "Migration 1 was applied by hand above; the rest follow.");
                Assert.AreEqual(LatestUserVersion, await ScalarTextAsync(
                    upgraded.Connection,
                    "PRAGMA user_version;"));
                Assert.IsTrue(await ColumnExistsAsync(
                    upgraded.Connection,
                    "layout_items",
                    "preferred_row"));
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        string escapedName = tableName.Replace("'", "''", StringComparison.Ordinal);
        return connection.ExecuteScalar(
            $"SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '{escapedName}' LIMIT 1;") is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName)
    {
        string escapedTable = tableName.Replace("'", "''", StringComparison.Ordinal);
        string escapedColumn = columnName.Replace("'", "''", StringComparison.Ordinal);
        return connection.ExecuteScalar(
            $"SELECT 1 FROM pragma_table_info('{escapedTable}') WHERE name = '{escapedColumn}' LIMIT 1;") is not null;
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql)
    {
        return Convert.ToInt32(connection.ExecuteScalar(sql), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        return connection.ExecuteScalar(sql) ?? string.Empty;
    }

    private static async Task InsertAsync(SqliteConnection connection, string sql)
    {
        connection.ExecuteNonQuery(sql);
        await Task.CompletedTask;
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

    private sealed class FailingBackup : ISqliteMigrationBackup
    {
        public string? Create(
            SqliteConnection source,
            SqliteDatabaseOptions options,
            int firstPendingVersion,
            CancellationToken cancellationToken) =>
            throw new SqliteMigrationBackupException(
                "test backup failure",
                new IOException("test"));
    }
}
