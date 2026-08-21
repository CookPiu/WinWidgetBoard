using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Persistence;

public sealed class SystemMonitorSettingsRepository
{
    private readonly SqliteRepository _repository;

    public SystemMonitorSettingsRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _repository = new SqliteRepository(database);
    }

    public SystemMonitorSettingsRecord? Get(string instanceId)
    {
        ValidateInstanceId(instanceId);
        SystemMonitorSettingsRecord? result = null;
        _repository.Query(
            "SELECT instance_id, card_items, entry_items, revision, updated_at_utc " +
            "FROM sysmon_settings WHERE instance_id = @instance;",
            statement => statement.BindText("@instance", instanceId),
            statement => result = ReadRecord(statement));
        return result;
    }

    public SystemMonitorSettingsRecord Save(
        string instanceId,
        IReadOnlyList<SystemMonitorItemDto> cardItems,
        IReadOnlyList<SystemMonitorItemDto> entryItems,
        int expectedRevision,
        DateTimeOffset? nowUtc = null)
    {
        ValidateInstanceId(instanceId);
        if (!SystemMonitorContract.IsValidItemList(cardItems))
        {
            throw new ArgumentException(
                "System monitor card items are invalid.",
                nameof(cardItems));
        }

        if (!SystemMonitorContract.IsValidItemList(entryItems))
        {
            throw new ArgumentException(
                "System monitor entry items are invalid.",
                nameof(entryItems));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        using SqliteTransaction transaction = _repository.BeginTransaction();
        SystemMonitorSettingsRecord? current = Get(instanceId);
        int actualRevision = current?.Revision ?? 0;
        if (actualRevision != expectedRevision)
        {
            throw new SystemMonitorSettingsRevisionConflictException(
                instanceId,
                expectedRevision,
                actualRevision);
        }

        int nextRevision = checked(actualRevision + 1);
        string updatedAtUtc = NoteRecord.FormatTimestamp(nowUtc ?? DateTimeOffset.UtcNow);
        string cardJson = SystemMonitorSettingsRecord.SerializeItems(cardItems);
        string entryJson = SystemMonitorSettingsRecord.SerializeItems(entryItems);

        if (current is null)
        {
            _repository.Execute(
                "INSERT INTO sysmon_settings " +
                "(instance_id, card_items, entry_items, revision, updated_at_utc) " +
                "VALUES (@instance, @card, @entry, @revision, @updated);",
                statement =>
                {
                    statement.BindText("@instance", instanceId);
                    statement.BindText("@card", cardJson);
                    statement.BindText("@entry", entryJson);
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                });
        }
        else
        {
            int changes = _repository.Execute(
                "UPDATE sysmon_settings SET card_items = @card, entry_items = @entry, " +
                "revision = @revision, updated_at_utc = @updated " +
                "WHERE instance_id = @instance AND revision = @expected;",
                statement =>
                {
                    statement.BindText("@card", cardJson);
                    statement.BindText("@entry", entryJson);
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                    statement.BindText("@instance", instanceId);
                    statement.BindInt("@expected", expectedRevision);
                });
            if (changes != 1)
            {
                throw new SystemMonitorSettingsRevisionConflictException(
                    instanceId,
                    expectedRevision,
                    current.Revision);
            }
        }

        transaction.Commit();
        return new SystemMonitorSettingsRecord(
            instanceId,
            cardItems,
            entryItems,
            nextRevision,
            updatedAtUtc);
    }

    private static SystemMonitorSettingsRecord ReadRecord(SqliteStatement statement) =>
        new(
            statement.ReadText(0) ??
                throw new SqliteException(1, "sysmon_settings.instance_id is NULL."),
            SystemMonitorSettingsRecord.DeserializeItems(statement.ReadText(1)),
            SystemMonitorSettingsRecord.DeserializeItems(statement.ReadText(2)),
            statement.ReadInt(3),
            statement.ReadText(4) ??
                throw new SqliteException(1, "sysmon_settings.updated_at_utc is NULL."));

    private static void ValidateInstanceId(string instanceId)
    {
        if (!SystemMonitorContract.IsValidInstanceId(instanceId))
        {
            throw new ArgumentException(
                "System monitor settings instance ID is invalid.",
                nameof(instanceId));
        }
    }
}
