namespace WinWidgetBoard.CoreBroker.Persistence;

public sealed class SqliteMigrationRunner
{
    private const string MigrationTableName = "schema_migrations";

    private readonly IReadOnlyList<SqliteMigration> _migrations;
    private readonly ISqliteMigrationBackup _backup;
    private readonly IPersistenceFaultInjector _faultInjector;

    public SqliteMigrationRunner(
        IEnumerable<SqliteMigration>? migrations = null,
        ISqliteMigrationBackup? backup = null,
        IPersistenceFaultInjector? faultInjector = null)
    {
        _migrations = (migrations ?? SqliteSchema.Migrations).ToArray();
        ValidateMigrations(_migrations);
        _backup = backup ?? new SqliteMigrationBackup();
        _faultInjector = faultInjector ?? PersistenceFaultInjector.None;
    }

    public Task<int> ApplyAsync(
        SqliteConnection connection,
        SqliteDatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        if (!connection.IsOpen)
        {
            throw new InvalidOperationException("The SQLite connection must be open before migrations run.");
        }

        HashSet<int> appliedVersions = ReadAppliedVersions(connection, cancellationToken);
        var pending = _migrations
            .Where(migration => !appliedVersions.Contains(migration.Version))
            .ToArray();

        if (pending.Length == 0)
        {
            return Task.FromResult(0);
        }

        _faultInjector.BeforeBackup(pending[0].Version);
        _backup.Create(
            connection,
            options,
            pending[0].Version,
            cancellationToken);

        using SqliteTransaction transaction = connection.BeginTransaction(_faultInjector);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _faultInjector.BeforeWrite();
            EnsureMigrationTable(connection);

            foreach (SqliteMigration migration in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _faultInjector.BeforeMigration(migration.Version);
                _faultInjector.BeforeWrite();
                connection.ExecuteNonQuery(migration.CommandText);
                _faultInjector.BeforeWrite();
                RecordMigration(connection, migration);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return Task.FromResult(pending.Length);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static HashSet<int> ReadAppliedVersions(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? exists = connection.ExecuteScalar(
            $"SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = {SqliteConnection.QuoteLiteral(MigrationTableName)} LIMIT 1;");
        if (exists is null)
        {
            return new HashSet<int>();
        }

        string versionsText = connection.ExecuteScalar(
            "SELECT COALESCE(group_concat(version, ','), '') FROM schema_migrations;") ?? string.Empty;
        var versions = new HashSet<int>();
        foreach (string versionText in versionsText.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(versionText, System.Globalization.CultureInfo.InvariantCulture, out int version))
            {
                throw new SqliteException(
                    1,
                    "schema_migrations contains a non-integer version.");
            }

            versions.Add(version);
        }

        return versions;
    }

    private static void EnsureMigrationTable(SqliteConnection connection)
    {
        connection.ExecuteNonQuery(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            );
            """);
    }

    private static void RecordMigration(
        SqliteConnection connection,
        SqliteMigration migration)
    {
        connection.ExecuteNonQuery(
            "INSERT INTO schema_migrations (version, name, applied_at_utc) VALUES (" +
            $"{migration.Version}, {SqliteConnection.QuoteLiteral(migration.Name)}, " +
            $"{SqliteConnection.QuoteLiteral(DateTimeOffset.UtcNow.ToString("O"))});");
    }

    private static void ValidateMigrations(IReadOnlyList<SqliteMigration> migrations)
    {
        var versions = new HashSet<int>();
        foreach (SqliteMigration migration in migrations)
        {
            if (!versions.Add(migration.Version))
            {
                throw new ArgumentException(
                    $"Migration version {migration.Version} is declared more than once.",
                    nameof(migrations));
            }
        }

        if (migrations
            .Select(migration => migration.Version)
            .Zip(migrations.Skip(1).Select(migration => migration.Version))
            .Any(pair => pair.First >= pair.Second))
        {
            throw new ArgumentException(
                "Migrations must be ordered by strictly increasing version.",
                nameof(migrations));
        }
    }
}
