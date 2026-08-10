using System.Runtime.InteropServices;

namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Minimal, synchronous wrapper over the Windows 11 system SQLite C API.
/// The wrapper intentionally exposes only the operations needed by the migration foundation.
/// </summary>
public sealed class SqliteConnection : IDisposable
{
    private const int SqliteOk = 0;
    private readonly string _databasePath;
    private nint _handle;
    private bool _disposed;

    public SqliteConnection(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public bool IsOpen => _handle != nint.Zero;

    internal nint DatabaseHandle => _handle;

    internal string ErrorMessage =>
        _handle == nint.Zero
            ? "SQLite operation failed."
            : NativeSqlite.GetErrorMessage(_handle);

    public void Open()
    {
        ThrowIfDisposed();
        if (IsOpen)
        {
            return;
        }

        int result = NativeSqlite.Open16(_databasePath, out _handle);
        if (result != SqliteOk)
        {
            string message = NativeSqlite.GetErrorMessage(_handle);
            CloseHandle();
            throw new SqliteException(result, message);
        }
    }

    public SqliteTransaction BeginTransaction(IPersistenceFaultInjector? faultInjector = null)
    {
        EnsureOpen();
        ExecuteNonQuery("BEGIN IMMEDIATE;");
        return new SqliteTransaction(this, faultInjector ?? PersistenceFaultInjector.None);
    }

    public void ExecuteNonQuery(string sql)
    {
        _ = Execute(sql, callback: null, out _);
    }

    public string? ExecuteScalar(string sql)
    {
        string? value = null;
        NativeSqlite.ExecCallback callback = (_, columnCount, values, _) =>
        {
            if (columnCount > 0 && values != nint.Zero)
            {
                nint valuePointer = Marshal.ReadIntPtr(values);
                value = valuePointer == nint.Zero
                    ? null
                    : Marshal.PtrToStringUTF8(valuePointer);
            }

            return 0;
        };

        _ = Execute(sql, callback, out _);
        GC.KeepAlive(callback);
        return value;
    }

    public SqliteStatement Prepare(string sql)
    {
        EnsureOpen();
        return new SqliteStatement(this, sql);
    }

    /// <summary>
    /// Number of rows modified by the most recently completed INSERT, UPDATE or DELETE
    /// on this connection. Useful for optimistic-concurrency checks.
    /// </summary>
    public int Changes
    {
        get
        {
            EnsureOpen();
            return NativeSqlite.Changes(_handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseHandle();
    }

    internal static string QuoteLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    internal void CommitTransaction()
    {
        EnsureOpen();
        ExecuteNonQuery("COMMIT;");
    }

    internal void RollbackTransaction()
    {
        if (IsOpen)
        {
            ExecuteNonQuery("ROLLBACK;");
        }
    }

    private int Execute(
        string sql,
        NativeSqlite.ExecCallback? callback,
        out string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        EnsureOpen();

        nint sqlPointer = Marshal.StringToCoTaskMemUTF8(sql);
        nint errorPointer = nint.Zero;
        try
        {
            int result = NativeSqlite.Exec(_handle, sqlPointer, callback, nint.Zero, out errorPointer);
            errorMessage = result == SqliteOk
                ? null
                : errorPointer == nint.Zero
                    ? NativeSqlite.GetErrorMessage(_handle)
                    : Marshal.PtrToStringUTF8(errorPointer);

            if (result != SqliteOk)
            {
                throw new SqliteException(result, errorMessage ?? "SQLite operation failed.");
            }

            return result;
        }
        finally
        {
            if (errorPointer != nint.Zero)
            {
                NativeSqlite.Free(errorPointer);
            }

            Marshal.FreeCoTaskMem(sqlPointer);
        }
    }

    private void EnsureOpen()
    {
        ThrowIfDisposed();
        if (!IsOpen)
        {
            throw new InvalidOperationException("The SQLite connection is not open.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private void CloseHandle()
    {
        if (_handle == nint.Zero)
        {
            return;
        }

        _ = NativeSqlite.Close(_handle);
        _handle = nint.Zero;
    }

    private static class NativeSqlite
    {
        private const string LibraryName = "winsqlite3.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int ExecCallback(
            nint argument,
            int columnCount,
            nint values,
            nint names);

        [DllImport(
            LibraryName,
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "sqlite3_open16",
            CharSet = CharSet.Unicode)]
        internal static extern int Open16(string filename, out nint database);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close_v2")]
        internal static extern int Close(nint database);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_exec")]
        internal static extern int Exec(
            nint database,
            nint sql,
            ExecCallback? callback,
            nint argument,
            out nint errorMessage);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
        internal static extern nint ErrorMessage(nint database);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_free")]
        internal static extern void Free(nint memory);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_changes")]
        internal static extern int Changes(nint database);

        internal static string GetErrorMessage(nint database) =>
            database == nint.Zero
                ? "SQLite failed to open the database."
                : Marshal.PtrToStringUTF8(ErrorMessage(database)) ?? "SQLite operation failed.";
    }
}

public sealed class SqliteTransaction : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IPersistenceFaultInjector _faultInjector;
    private bool _completed;

    internal SqliteTransaction(
        SqliteConnection connection,
        IPersistenceFaultInjector faultInjector)
    {
        _connection = connection;
        _faultInjector = faultInjector;
    }

    public void Commit()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The SQLite transaction is already completed.");
        }

        _faultInjector.BeforeCommit();
        _connection.CommitTransaction();
        _completed = true;
    }

    public void Rollback()
    {
        if (_completed)
        {
            return;
        }

        _connection.RollbackTransaction();
        _completed = true;
    }

    public void Dispose()
    {
        if (!_completed)
        {
            Rollback();
        }
    }
}

public sealed class SqliteException : Exception
{
    public SqliteException(int errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }
}
