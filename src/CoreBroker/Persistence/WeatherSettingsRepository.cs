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
            "unit_system, revision, updated_at_utc, provider_id, api_host, " +
            "api_key_protected " +
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
        string unitSystem,
        int expectedRevision,
        DateTimeOffset? nowUtc = null,
        string providerId = WeatherSettingsContract.OpenMeteoProviderId,
        string apiHost = "",
        string? apiKey = null)
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

        if (!WeatherSettingsContract.IsValidUnitSystem(unitSystem))
        {
            throw new ArgumentException(
                "Weather settings unit system is invalid.",
                nameof(unitSystem));
        }

        if (!WeatherSettingsContract.IsValidProviderId(providerId))
        {
            throw new ArgumentException(
                "Weather settings provider is invalid.",
                nameof(providerId));
        }

        if (!WeatherSettingsContract.TryNormalizeApiHost(
                apiHost,
                out string normalizedApiHost))
        {
            throw new ArgumentException(
                "Weather settings API host is invalid.",
                nameof(apiHost));
        }

        if (apiKey is not null && !WeatherSettingsContract.IsValidApiKey(apiKey))
        {
            throw new ArgumentException(
                "Weather settings API key is invalid.",
                nameof(apiKey));
        }

        // Encrypted once, before the transaction: a DPAPI failure must not leave a half
        // written row behind, and the ciphertext is what every branch below binds.
        string? protectedApiKey = apiKey is null
            ? null
            : LocalDataProtector.Protect(apiKey);
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
                "unit_system, revision, updated_at_utc, provider_id, api_host, " +
                "api_key_protected) " +
                "VALUES (@instance, @label, @latitude, @longitude, @automatic, " +
                "@units, @revision, @updated, @provider, @host, @key);",
                statement =>
                {
                    statement.BindText("@instance", instanceId);
                    statement.BindText("@label", normalizedLabel);
                    statement.BindDouble("@latitude", latitude);
                    statement.BindDouble("@longitude", longitude);
                    statement.BindInt("@automatic", useDeviceLocation ? 1 : 0);
                    statement.BindText("@units", unitSystem);
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                    statement.BindText("@provider", providerId);
                    statement.BindText("@host", normalizedApiHost);
                    statement.BindNullableText("@key", protectedApiKey);
                });
        }
        else
        {
            int changes = _repository.Execute(
                "UPDATE weather_settings SET label = @label, latitude = @latitude, " +
                "longitude = @longitude, use_device_location = @automatic, " +
                "unit_system = @units, revision = @revision, updated_at_utc = @updated, " +
                "provider_id = @provider, api_host = @host, api_key_protected = @key " +
                "WHERE instance_id = @instance AND revision = @expected;",
                statement =>
                {
                    statement.BindText("@label", normalizedLabel);
                    statement.BindDouble("@latitude", latitude);
                    statement.BindDouble("@longitude", longitude);
                    statement.BindInt("@automatic", useDeviceLocation ? 1 : 0);
                    statement.BindText("@units", unitSystem);
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                    statement.BindText("@provider", providerId);
                    statement.BindText("@host", normalizedApiHost);
                    statement.BindNullableText("@key", protectedApiKey);
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
            unitSystem,
            nextRevision,
            updatedAtUtc,
            providerId,
            normalizedApiHost,
            apiKey);
    }

    private static WeatherSettingsRecord ReadRecord(SqliteStatement statement) =>
        new(
            statement.ReadText(0) ?? throw new SqliteException(1, "weather_settings.instance_id is NULL."),
            statement.ReadText(1) ?? throw new SqliteException(1, "weather_settings.label is NULL."),
            statement.ReadDouble(2),
            statement.ReadDouble(3),
            statement.ReadInt(4) != 0,
            statement.ReadText(5) ?? throw new SqliteException(1, "weather_settings.unit_system is NULL."),
            statement.ReadInt(6),
            statement.ReadText(7) ?? throw new SqliteException(1, "weather_settings.updated_at_utc is NULL."),
            statement.ReadText(8) ?? WeatherSettingsContract.OpenMeteoProviderId,
            statement.ReadText(9) ?? string.Empty,
            // A credential that does not decrypt here reads as absent rather than as a
            // storage fault: the row is still a valid settings row, it just has no usable
            // key - which is what a database copied to another account looks like.
            LocalDataProtector.TryUnprotect(statement.ReadText(10)));

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
