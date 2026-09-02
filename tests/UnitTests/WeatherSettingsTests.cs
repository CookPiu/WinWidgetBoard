using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class WeatherSettingsTests
{
    [TestMethod(DisplayName = "UT-WEA-010 [WEA-001/DAT-001] Weather settings persist a local location with revision")]
    public async Task WeatherSettingsPersistLocationWithRevision()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new WeatherSettingsRepository(database);
        DateTimeOffset timestamp = new(2026, 8, 15, 10, 20, 30, TimeSpan.Zero);

        Assert.IsNull(repository.Get(WeatherSettingsContract.DefaultInstanceId));
        WeatherSettingsRecord saved = repository.Save(
            WeatherSettingsContract.DefaultInstanceId,
            "Tokyo",
            35.6762,
            139.6503,
            useDeviceLocation: false,
            WeatherSettingsContract.ImperialUnitSystem,
            expectedRevision: 0,
            nowUtc: timestamp);

        Assert.AreEqual(1, saved.Revision);
        Assert.AreEqual("Tokyo", saved.Label);
        Assert.IsFalse(saved.UseDeviceLocation);
        Assert.AreEqual(WeatherSettingsContract.ImperialUnitSystem, saved.UnitSystem);
        Assert.AreEqual(
            timestamp.ToUniversalTime().UtcDateTime.ToString("O"),
            saved.UpdatedAtUtc);
        Assert.AreEqual(saved, repository.Get(saved.InstanceId));
    }

    [TestMethod(DisplayName = "UT-WEA-011 [WEA-001/NFR-REL-002] Weather settings reject stale revision without overwriting")]
    public async Task WeatherSettingsRejectStaleRevision()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        var repository = new WeatherSettingsRepository(database);
        WeatherSettingsRecord initial = repository.Save(
            WeatherSettingsContract.DefaultInstanceId,
            "Tokyo",
            35.6762,
            139.6503,
            useDeviceLocation: false,
            WeatherSettingsContract.MetricUnitSystem,
            expectedRevision: 0);

        WeatherSettingsRevisionConflictException conflict =
            Assert.Throws<WeatherSettingsRevisionConflictException>(() => repository.Save(
                initial.InstanceId,
                "Sydney",
                -33.8688,
                151.2093,
                useDeviceLocation: false,
                WeatherSettingsContract.MetricUnitSystem,
                expectedRevision: 0));

        Assert.AreEqual(1, conflict.ActualRevision);
        Assert.AreEqual("Tokyo", repository.Get(initial.InstanceId)!.Label);
    }

    [TestMethod(DisplayName = "UT-WEA-012 [WEA-001] Weather settings validate label and geographic bounds")]
    public void WeatherSettingsValidateInput()
    {
        Assert.IsFalse(WeatherSettingsContract.TryNormalizeLabel(" ", out _));
        Assert.IsFalse(WeatherSettingsContract.TryNormalizeLabel(
            new string('x', WeatherSettingsContract.MaxLabelLength + 1),
            out _));
        Assert.IsFalse(WeatherSettingsContract.IsValidCoordinates(91, 0));
        Assert.IsFalse(WeatherSettingsContract.IsValidCoordinates(0, 181));
        Assert.IsFalse(WeatherSettingsContract.IsValidCoordinates(double.NaN, 0));
        Assert.IsTrue(WeatherSettingsContract.IsValidCoordinates(0, 0));
        Assert.IsFalse(WeatherSettingsContract.IsValidUnitSystem("Metric"));
        Assert.IsFalse(WeatherSettingsContract.IsValidUnitSystem("fahrenheit"));
        Assert.IsTrue(WeatherSettingsContract.IsValidUnitSystem("metric"));
        Assert.IsTrue(WeatherSettingsContract.IsValidUnitSystem("imperial"));
        // Null means metric - the meaning silence had before the field existed.
        Assert.IsTrue(WeatherSettingsContract.TryNormalizeUnitSystem(null, out string norm));
        Assert.AreEqual(WeatherSettingsContract.MetricUnitSystem, norm);
        Assert.IsFalse(WeatherSettingsContract.TryNormalizeUnitSystem("kelvin", out _));
    }

    [TestMethod(DisplayName = "UT-WEA-073 [WEA-001/DAT-001] Existing weather rows migrate to automatic device location")]
    public async Task ExistingWeatherSettingsEnableDeviceLocationOnMigration()
    {
        await using SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        var legacy = new SqliteMigrationRunner(SqliteSchema.Migrations.Take(6));
        Assert.AreEqual(6, await legacy.ApplyAsync(database.Connection, database.Options));
        database.Connection.ExecuteNonQuery(
            "INSERT INTO weather_settings " +
            "(instance_id, label, latitude, longitude, revision, updated_at_utc) " +
            "VALUES ('demo.weather', 'Tokyo', 35.6762, 139.6503, 1, " +
            "'2026-08-29T00:00:00.0000000Z');");

        // Migrations 7 (device location), 8 (units) and 9 (token pricing sync) are pending.
        Assert.AreEqual(3, await database.ApplySchemaAsync());

        WeatherSettingsRecord migrated = new WeatherSettingsRepository(database)
            .Get(WeatherSettingsContract.DefaultInstanceId)!;
        Assert.IsTrue(migrated.UseDeviceLocation);
        Assert.AreEqual("Tokyo", migrated.Label);
        // A row from before units existed keeps meaning metric.
        Assert.AreEqual(WeatherSettingsContract.MetricUnitSystem, migrated.UnitSystem);
    }

    private static async Task<SqliteDatabase> OpenDatabaseAsync()
    {
        SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        await database.ApplySchemaAsync();
        return database;
    }
}
