namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Named persistence/recovery seam that can be reached before each documented failure
/// surface. Production callers never pass an injector and therefore always receive the
/// no-op default; tests use <see cref="PersistenceFaultInjector"/> to simulate open,
/// migration, backup/restore and transaction failures deterministically.
/// </summary>
public interface IPersistenceFaultInjector
{
    void BeforeOpen();

    void BeforeMigration(int pendingMigrationVersion);

    void BeforeBackup(int backupVersion);

    void BeforeRestore();

    void BeforeCommit();

    void BeforeWrite();
}

public enum PersistenceFaultPoint
{
    Open,
    Migration,
    Backup,
    Restore,
    Commit,
    Write,
}

/// <summary>
/// Test-only fault plan. Default instance is <see cref="PersistenceFaultInjector.None"/>,
/// which never throws. Configured faults can be unconditional, version-scoped (Migration and
/// Backup) and optionally one-shot so a retry path can be exercised.
/// </summary>
public sealed class PersistenceFaultInjector : IPersistenceFaultInjector
{
    public static IPersistenceFaultInjector None { get; } = new NoopPersistenceFaultInjector();

    private readonly Dictionary<PersistenceFaultPoint, FaultEntry> _faults = new();

    public void Inject(PersistenceFaultPoint point, Exception exception, int? version = null, bool once = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _faults[point] = new FaultEntry(exception, version, once);
    }

    public void Clear() => _faults.Clear();

    void IPersistenceFaultInjector.BeforeOpen() =>
        ThrowIfConfigured(PersistenceFaultPoint.Open, version: null);

    void IPersistenceFaultInjector.BeforeMigration(int pendingMigrationVersion) =>
        ThrowIfConfigured(PersistenceFaultPoint.Migration, pendingMigrationVersion);

    void IPersistenceFaultInjector.BeforeBackup(int backupVersion) =>
        ThrowIfConfigured(PersistenceFaultPoint.Backup, backupVersion);

    void IPersistenceFaultInjector.BeforeRestore() =>
        ThrowIfConfigured(PersistenceFaultPoint.Restore, version: null);

    void IPersistenceFaultInjector.BeforeCommit() =>
        ThrowIfConfigured(PersistenceFaultPoint.Commit, version: null);

    void IPersistenceFaultInjector.BeforeWrite() =>
        ThrowIfConfigured(PersistenceFaultPoint.Write, version: null);

    private void ThrowIfConfigured(PersistenceFaultPoint point, int? version)
    {
        if (!_faults.TryGetValue(point, out FaultEntry? entry) || entry is null)
        {
            return;
        }

        if (entry.Version is not null && entry.Version != version)
        {
            return;
        }

        if (entry.Once)
        {
            _faults.Remove(point);
        }

        throw entry.Exception;
    }

    private sealed record FaultEntry(Exception Exception, int? Version, bool Once);
}

internal sealed class NoopPersistenceFaultInjector : IPersistenceFaultInjector
{
    public void BeforeOpen()
    {
    }

    public void BeforeMigration(int pendingMigrationVersion)
    {
    }

    public void BeforeBackup(int backupVersion)
    {
    }

    public void BeforeRestore()
    {
    }

    public void BeforeCommit()
    {
    }

    public void BeforeWrite()
    {
    }
}
