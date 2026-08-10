namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Restores a migration backup into a new isolated file for read-only recovery workflows.
/// It never replaces the active database; selecting and promoting a recovered file belongs
/// to a later, explicitly approved recovery workflow.
/// </summary>
public sealed class SqliteRecoveryManager
{
    private readonly IPersistenceFaultInjector _faultInjector;

    public SqliteRecoveryManager(IPersistenceFaultInjector? faultInjector = null)
    {
        _faultInjector = faultInjector ?? PersistenceFaultInjector.None;
    }

    public string RestoreToRecoveryCopy(
        string backupPath,
        string recoveryDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryDirectory);
        _faultInjector.BeforeRestore();
        cancellationToken.ThrowIfCancellationRequested();

        string sourcePath = Path.GetFullPath(backupPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The SQLite backup file was not found.", sourcePath);
        }

        string directory = Path.GetFullPath(recoveryDirectory);
        Directory.CreateDirectory(directory);
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string destinationPath = Path.Combine(
            directory,
            $"{name}.recovered-{Guid.NewGuid():N}.db");
        string temporaryPath = destinationPath + ".tmp";

        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: false);
            return destinationPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
