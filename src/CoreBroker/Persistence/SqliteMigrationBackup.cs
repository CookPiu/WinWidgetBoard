namespace WinWidgetBoard.CoreBroker.Persistence;

public interface ISqliteMigrationBackup
{
    string? Create(
        SqliteConnection source,
        SqliteDatabaseOptions options,
        int firstPendingVersion,
        CancellationToken cancellationToken);
}

public sealed class SqliteMigrationBackup : ISqliteMigrationBackup
{
    public string? Create(
        SqliteConnection source,
        SqliteDatabaseOptions options,
        int firstPendingVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.IsInMemory ||
            !File.Exists(options.DatabasePath) ||
            new FileInfo(options.DatabasePath).Length == 0)
        {
            return null;
        }

        string backupDirectory = options.ResolveBackupDirectory()!;
        Directory.CreateDirectory(backupDirectory);

        string databaseName = Path.GetFileName(options.DatabasePath);
        string timestamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMdd'T'HHmmssfff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        string backupPath = Path.Combine(
            backupDirectory,
            $"{databaseName}.pre-migration-v{firstPendingVersion}-{timestamp}-{Guid.NewGuid():N}.bak");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.ExecuteNonQuery(
                $"VACUUM INTO {SqliteConnection.QuoteLiteral(backupPath)};");
            return backupPath;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(backupPath);
            throw new SqliteMigrationBackupException(
                "The database backup failed; migration was not started.",
                exception);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Preserve the original backup failure. A later diagnostic can remove the partial file.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original backup failure. A later diagnostic can remove the partial file.
        }
    }
}

public sealed class SqliteMigrationBackupException : IOException
{
    public SqliteMigrationBackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
