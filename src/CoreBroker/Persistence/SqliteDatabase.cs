namespace WinWidgetBoard.CoreBroker.Persistence;

public sealed class SqliteDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private SqliteDatabase(SqliteDatabaseOptions options, SqliteConnection connection)
    {
        Options = options;
        _connection = connection;
    }

    public SqliteDatabaseOptions Options { get; }

    public SqliteConnection Connection
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connection;
        }
    }

    public static Task<SqliteDatabase> OpenAsync(
        SqliteDatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.FaultInjector.BeforeOpen();

        if (!options.IsInMemory)
        {
            string databaseDirectory = options.DatabaseDirectory
                ?? throw new ArgumentException(
                    "The database path must include a directory.",
                    nameof(options));
            Directory.CreateDirectory(databaseDirectory);
        }

        var connection = new SqliteConnection(options.DatabasePath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            connection.Open();
            ConfigureConnection(connection, options.IsInMemory, cancellationToken);
            return Task.FromResult(new SqliteDatabase(options, connection));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public Task<int> ApplySchemaAsync(CancellationToken cancellationToken = default) =>
        new SqliteMigrationRunner(faultInjector: Options.FaultInjector)
            .ApplyAsync(Connection, Options, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void ConfigureConnection(
        SqliteConnection connection,
        bool isInMemory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        connection.ExecuteNonQuery("PRAGMA foreign_keys = ON;");
        connection.ExecuteNonQuery("PRAGMA busy_timeout = 5000;");

        if (!isInMemory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            connection.ExecuteNonQuery("PRAGMA journal_mode = WAL;");
        }
    }
}
