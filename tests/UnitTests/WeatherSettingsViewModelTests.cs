using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class WeatherSettingsViewModelTests
{
    [TestMethod(DisplayName = "UT-WEA-013 [WEA-001/SET-003] Weather settings ViewModel commits through CardSettingsDraft")]
    public async Task ViewModelUsesDraftForRevisionedCommit()
    {
        var client = new FakeWeatherSettingsClient();
        var viewModel = new WeatherSettingsViewModel(client, static key => key);

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        viewModel.LocationLabel = "Tokyo";
        viewModel.LatitudeText = "35.6762";
        viewModel.LongitudeText = "139.6503";

        Assert.IsTrue(await viewModel.SaveAsync(CancellationToken.None));
        Assert.IsNotNull(client.LastSaveRequest);
        Assert.AreEqual(0, client.LastSaveRequest!.ExpectedRevision);
        Assert.AreEqual("Tokyo", client.LastSaveRequest.Label);
        Assert.AreEqual(35.6762, client.LastSaveRequest.Latitude, 0.00001);
        Assert.AreEqual(139.6503, client.LastSaveRequest.Longitude, 0.00001);
        Assert.IsTrue(viewModel.WasSaved);
        Assert.AreEqual(1, viewModel.Revision);
        Assert.AreEqual("WeatherSettingsSavedStatus", viewModel.StatusText);
    }

    [TestMethod(DisplayName = "UT-WEA-014 [WEA-001/SET-003] Weather settings ViewModel blocks invalid coordinates before IPC")]
    public async Task ViewModelBlocksInvalidCoordinates()
    {
        var client = new FakeWeatherSettingsClient();
        var viewModel = new WeatherSettingsViewModel(client, static key => key);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.LatitudeText = "91";

        Assert.IsFalse(await viewModel.SaveAsync(CancellationToken.None));
        Assert.AreEqual("WeatherSettingsInvalidLocationStatus", viewModel.ValidationText);
        Assert.IsNull(client.LastSaveRequest);
    }

    private sealed class FakeWeatherSettingsClient : IWeatherSettingsClient
    {
        public WeatherSettingsSaveRequest? LastSaveRequest { get; private set; }

        public Task<WeatherSettingsDto> GetWeatherSettingsAsync(
            string instanceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WeatherSettingsDto
            {
                InstanceId = instanceId,
                Label = WeatherSettingsContract.DefaultLabel,
                Latitude = WeatherSettingsContract.DefaultLatitude,
                Longitude = WeatherSettingsContract.DefaultLongitude,
                Revision = 0,
            });

        public Task<WeatherSettingsDto> SaveWeatherSettingsAsync(
            WeatherSettingsSaveRequest request,
            CancellationToken cancellationToken)
        {
            LastSaveRequest = request;
            return Task.FromResult(new WeatherSettingsDto
            {
                InstanceId = request.InstanceId!,
                Label = request.Label!,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                Revision = request.ExpectedRevision + 1,
                UpdatedAtUtc = "2026-08-15T10:00:00.0000000Z",
            });
        }
    }
}
