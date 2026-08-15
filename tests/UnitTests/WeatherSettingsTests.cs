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
            expectedRevision: 0,
            timestamp);

        Assert.AreEqual(1, saved.Revision);
        Assert.AreEqual("Tokyo", saved.Label);
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
            expectedRevision: 0);

        WeatherSettingsRevisionConflictException conflict =
            Assert.Throws<WeatherSettingsRevisionConflictException>(() => repository.Save(
                initial.InstanceId,
                "Sydney",
                -33.8688,
                151.2093,
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
    }

    private static async Task<SqliteDatabase> OpenDatabaseAsync()
    {
        SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        await database.ApplySchemaAsync();
        return database;
    }
}
