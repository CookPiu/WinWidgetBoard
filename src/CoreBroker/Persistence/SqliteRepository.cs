namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Small, parameterized data-access boundary shared by future domain repositories.
/// It deliberately does not expose domain entities or add card/layout CRUD ahead of its
/// approved work packages.
/// </summary>
public sealed class SqliteRepository
{
    private readonly SqliteDatabase _database;

    public SqliteRepository(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public int Execute(string sql, Action<SqliteStatement>? bind = null)
    {
        _database.Options.FaultInjector.BeforeWrite();
        using SqliteStatement statement = _database.Connection.Prepare(sql);
        bind?.Invoke(statement);
        while (statement.Step())
        {
        }

        return _database.Connection.Changes;
    }

    public void Query(
        string sql,
        Action<SqliteStatement>? bind,
        Action<SqliteStatement> readRow)
    {
        ArgumentNullException.ThrowIfNull(readRow);
        using SqliteStatement statement = _database.Connection.Prepare(sql);
        bind?.Invoke(statement);
        while (statement.Step())
        {
            readRow(statement);
        }
    }

    public SqliteTransaction BeginTransaction() =>
        _database.Connection.BeginTransaction(_database.Options.FaultInjector);
}
