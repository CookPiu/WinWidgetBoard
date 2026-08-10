using System.Runtime.InteropServices;

namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// A prepared, parameterized statement over a <see cref="SqliteConnection"/>.
/// Named parameters use SQLite syntax such as <c>@id</c>; result columns are read by 0-based index.
/// This is the parameterized access used by the repository layer; the migration foundation
/// continues to use <see cref="SqliteConnection.ExecuteNonQuery"/> for scripted DDL.
/// </summary>
public sealed class SqliteStatement : IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteNullType = 5;

    private static readonly nint SqliteTransient = new nint(-1);

    private readonly SqliteConnection _connection;
    private nint _handle;
    private bool _disposed;

    internal SqliteStatement(SqliteConnection connection, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        _connection = connection;
        nint sqlPointer = Marshal.StringToCoTaskMemUTF8(sql);
        try
        {
            int result = NativeSqlite.PrepareV2(
                connection.DatabaseHandle,
                sqlPointer,
                -1,
                out _handle,
                nint.Zero);
            if (result != SqliteOk)
            {
                string message = connection.ErrorMessage;
                _handle = nint.Zero;
                throw new SqliteException(result, message);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(sqlPointer);
        }
    }

    public void BindText(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        int index = ParameterIndex(name);
        nint valuePointer = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            int result = NativeSqlite.BindText(_handle, index, valuePointer, -1, SqliteTransient);
            ThrowIfNotOk(result);
        }
        finally
        {
            Marshal.FreeCoTaskMem(valuePointer);
        }
    }

    public void BindNullableText(string name, string? value)
    {
        if (value is null)
        {
            BindNull(name);
        }
        else
        {
            BindText(name, value);
        }
    }

    public void BindInt(string name, int value)
    {
        int index = ParameterIndex(name);
        ThrowIfNotOk(NativeSqlite.BindInt(_handle, index, value));
    }

    public void BindNullableInt(string name, int? value)
    {
        if (value is null)
        {
            BindNull(name);
        }
        else
        {
            BindInt(name, value.Value);
        }
    }

    public void BindLong(string name, long value)
    {
        int index = ParameterIndex(name);
        ThrowIfNotOk(NativeSqlite.BindInt64(_handle, index, value));
    }

    public void BindNullableLong(string name, long? value)
    {
        if (value is null)
        {
            BindNull(name);
        }
        else
        {
            BindLong(name, value.Value);
        }
    }

    public void BindDouble(string name, double value)
    {
        int index = ParameterIndex(name);
        ThrowIfNotOk(NativeSqlite.BindDouble(_handle, index, value));
    }

    public void BindNull(string name)
    {
        int index = ParameterIndex(name);
        ThrowIfNotOk(NativeSqlite.BindNull(_handle, index));
    }

    /// <summary>
    /// Executes one step of the statement. Returns true while a result row is available
    /// (SQLITE_ROW) and false when the statement is complete (SQLITE_DONE). Execution
    /// errors surface as <see cref="SqliteException"/>.
    /// </summary>
    public bool Step()
    {
        ThrowIfDisposed();
        int result = NativeSqlite.Step(_handle);
        if (result == SqliteRow)
        {
            return true;
        }

        if (result == SqliteDone)
        {
            return false;
        }

        throw new SqliteException(result, _connection.ErrorMessage);
    }

    public bool IsNull(int column) =>
        NativeSqlite.ColumnType(_handle, column) == SqliteNullType;

    public string? ReadText(int column)
    {
        ThrowIfDisposed();
        nint valuePointer = NativeSqlite.ColumnText(_handle, column);
        return valuePointer == nint.Zero ? null : Marshal.PtrToStringUTF8(valuePointer);
    }

    public int ReadInt(int column) => NativeSqlite.ColumnInt(_handle, column);

    public long ReadLong(int column) => NativeSqlite.ColumnInt64(_handle, column);

    public double ReadDouble(int column) => NativeSqlite.ColumnDouble(_handle, column);

    /// <summary>
    /// Rewinds the statement and clears bound values so it can be bound and executed again.
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        _ = NativeSqlite.Reset(_handle);
        _ = NativeSqlite.ClearBindings(_handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != nint.Zero)
        {
            _ = NativeSqlite.Finalize(_handle);
            _handle = nint.Zero;
        }
    }

    private int ParameterIndex(string name)
    {
        ThrowIfDisposed();
        nint namePointer = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            int index = NativeSqlite.BindParameterIndex(_handle, namePointer);
            if (index == 0)
            {
                throw new ArgumentException(
                    $"SQLite parameter '{name}' was not found in the prepared statement.",
                    nameof(name));
            }

            return index;
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }
    }

    private void ThrowIfNotOk(int result)
    {
        if (result != SqliteOk)
        {
            throw new SqliteException(result, _connection.ErrorMessage);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static class NativeSqlite
    {
        private const string LibraryName = "winsqlite3.dll";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
        internal static extern int PrepareV2(nint database, nint sql, int length, out nint statement, nint tail);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_parameter_index")]
        internal static extern int BindParameterIndex(nint statement, nint name);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_null")]
        internal static extern int BindNull(nint statement, int index);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int")]
        internal static extern int BindInt(nint statement, int index, int value);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int64")]
        internal static extern int BindInt64(nint statement, int index, long value);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_double")]
        internal static extern int BindDouble(nint statement, int index, double value);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_text")]
        internal static extern int BindText(nint statement, int index, nint value, int length, nint destructor);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
        internal static extern int Step(nint statement);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_type")]
        internal static extern int ColumnType(nint statement, int column);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
        internal static extern nint ColumnText(nint statement, int column);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int")]
        internal static extern int ColumnInt(nint statement, int column);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int64")]
        internal static extern long ColumnInt64(nint statement, int column);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_double")]
        internal static extern double ColumnDouble(nint statement, int column);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_reset")]
        internal static extern int Reset(nint statement);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_clear_bindings")]
        internal static extern int ClearBindings(nint statement);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
        internal static extern int Finalize(nint statement);
    }
}
