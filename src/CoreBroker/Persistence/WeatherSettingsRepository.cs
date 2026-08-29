using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Persistence;

public sealed class WeatherSettingsRepository
{
    private readonly SqliteRepository _repository;

    public WeatherSettingsRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _repository = new SqliteRepository(database);
    }

    public WeatherSettingsRecord? Get(string instanceId)
    {
        ValidateInstanceId(instanceId);
        WeatherSettingsRecord? result = null;
        _repository.Query(
            "SELECT instance_id, label, latitude, longitude, use_device_location, " +
            "revision, updated_at_utc " +
            "FROM weather_settings WHERE instance_id = @instance;",
            statement => statement.BindText("@instance", instanceId),
            statement => result = ReadRecord(statement));
        return result;
    }

    public WeatherSettingsRecord Save(
        string instanceId,
        string label,
        double latitude,
        double longitude,
        bool useDeviceLocation,
        int expectedRevision,
        DateTimeOffset? nowUtc = null)
    {
        ValidateInstanceId(instanceId);
        if (!WeatherSettingsContract.TryNormalizeLabel(label, out string? normalizedLabel) ||
            normalizedLabel is null)
        {
            throw new ArgumentException(
                "Weather settings label is invalid.",
                nameof(label));
        }

        if (!WeatherSettingsContract.IsValidCoordinates(latitude, longitude))
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitude),
                "Weather coordinates are outside the valid geographic range.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        using SqliteTransaction transaction = _repository.BeginTransaction();
        WeatherSettingsRecord? current = Get(instanceId);
        int actualRevision = current?.Revision ?? 0;
        if (actualRevision != expectedRevision)
        {
            throw new WeatherSettingsRevisionConflictException(
                instanceId,
                expectedRevision,
                actualRevision);
        }

        int nextRevision = checked(actualRevision + 1);
        string updatedAtUtc = NoteRecord.FormatTimestamp(
            nowUtc ?? DateTimeOffset.UtcNow);
        if (current is null)
        {
            _repository.Execute(
                "INSERT INTO weather_settings " +
                "(instance_id, label, latitude, longitude, use_device_location, " +
                "revision, updated_at_utc) " +
                "VALUES (@instance, @label, @latitude, @longitude, @automatic, " +
                "@revision, @updated);",
                statement =>
                {
                    statement.BindText("@instance", instanceId);
                    statement.BindText("@label", normalizedLabel);
                    statement.BindDouble("@latitude", latitude);
                    statement.BindDouble("@longitude", longitude);
                    statement.BindInt("@automatic", useDeviceLocation ? 1 : 0);
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                });
        }
        else
        {
            int changes = _repository.Execute(
                "UPDATE weather_settings SET label = @label, latitude = @latitude, " +
                "longitude = @longitude, use_device_location = @automatic, " +
                "revision = @revision, updated_at_utc = @updated " +
                "WHERE instance_id = @instance AND revision = @expected;",
                statement =>
                {
                    statement.BindText("@label", normalizedLabel);
                    statement.BindDouble("@latitude", latitude);
                    statement.BindDouble("@longitude", longitude);
                    statement.BindInt("@automatic", useDeviceLocation ? 1 : 0);
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                    statement.BindText("@instance", instanceId);
                    statement.BindInt("@expected", expectedRevision);
                });
            if (changes != 1)
            {
                throw new WeatherSettingsRevisionConflictException(
                    instanceId,
                    expectedRevision,
                    current.Revision);
            }
        }

        transaction.Commit();
        return new WeatherSettingsRecord(
            instanceId,
            normalizedLabel,
            latitude,
            longitude,
            useDeviceLocation,
            nextRevision,
            updatedAtUtc);
    }

    private static WeatherSettingsRecord ReadRecord(SqliteStatement statement) =>
        new(
            statement.ReadText(0) ?? throw new SqliteException(1, "weather_settings.instance_id is NULL."),
            statement.ReadText(1) ?? throw new SqliteException(1, "weather_settings.label is NULL."),
            statement.ReadDouble(2),
            statement.ReadDouble(3),
            statement.ReadInt(4) != 0,
            statement.ReadInt(5),
            statement.ReadText(6) ?? throw new SqliteException(1, "weather_settings.updated_at_utc is NULL."));

    private static void ValidateInstanceId(string instanceId)
    {
        if (!WeatherSettingsContract.IsValidInstanceId(instanceId))
        {
            throw new ArgumentException(
                "Weather settings instance ID is invalid.",
                nameof(instanceId));
        }
    }
}
