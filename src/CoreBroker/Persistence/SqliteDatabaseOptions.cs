namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Describes a database location without coupling storage to LocalAppData or a UI process.
/// </summary>
public sealed record SqliteDatabaseOptions
{
    public SqliteDatabaseOptions(
        string databasePath,
        string? backupDirectory = null,
        IPersistenceFaultInjector? faultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        IsInMemory = string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase);
        DatabasePath = IsInMemory ? ":memory:" : Path.GetFullPath(databasePath);
        BackupDirectory = backupDirectory is null
            ? null
            : Path.GetFullPath(backupDirectory);
        FaultInjector = faultInjector ?? PersistenceFaultInjector.None;
    }

    public string DatabasePath { get; }

    public string? BackupDirectory { get; }

    public bool IsInMemory { get; }

    public IPersistenceFaultInjector FaultInjector { get; }

    public string? DatabaseDirectory => IsInMemory
        ? null
        : Path.GetDirectoryName(DatabasePath);

    public string? ResolveBackupDirectory() => IsInMemory
        ? null
        : BackupDirectory ?? Path.Combine(DatabaseDirectory!, "Backups");
}
