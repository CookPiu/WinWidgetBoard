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
        Assert.IsTrue(client.LastSaveRequest.UseDeviceLocation);
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

    [TestMethod(DisplayName = "UT-WEA-070 [WEA-001/NFR-PRI-003] Device location updates and saves through the existing weather contract")]
    public async Task DeviceLocationUpdatesWeatherSettings()
    {
        var client = new FakeWeatherSettingsClient();
        var provider = new FakeDeviceLocationProvider(
            DeviceLocationResult.Available(1.29027, 103.851959));
        var viewModel = new WeatherSettingsViewModel(
            client,
            key => key == "WeatherSettingsAutomaticLocationLabel"
                ? "Current location"
                : key,
            provider);

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsTrue(await viewModel.RefreshDeviceLocationAsync(CancellationToken.None));

        Assert.IsNotNull(client.LastSaveRequest);
        Assert.AreEqual("Current location", client.LastSaveRequest!.Label);
        Assert.AreEqual(1.29027, client.LastSaveRequest.Latitude, 0.000001);
        Assert.AreEqual(103.851959, client.LastSaveRequest.Longitude, 0.000001);
        Assert.IsTrue(client.LastSaveRequest.UseDeviceLocation);
        Assert.AreEqual("WeatherSettingsDeviceLocationReadyStatus", viewModel.StatusText);
    }

    [TestMethod(DisplayName = "UT-WEA-071 [WEA-001/NFR-PRI-003] Denied device location preserves the saved manual location")]
    public async Task DeniedDeviceLocationPreservesCurrentSettings()
    {
        var client = new FakeWeatherSettingsClient();
        var viewModel = new WeatherSettingsViewModel(
            client,
            static key => key,
            new FakeDeviceLocationProvider(DeviceLocationResult.Denied()));

        Assert.IsTrue(await viewModel.LoadAsync(CancellationToken.None));
        Assert.IsFalse(await viewModel.RefreshDeviceLocationAsync(CancellationToken.None));

        Assert.IsNull(client.LastSaveRequest);
        Assert.AreEqual(WeatherSettingsContract.DefaultLabel, viewModel.LocationLabel);
        Assert.AreEqual("WeatherSettingsDeviceLocationDeniedStatus", viewModel.StatusText);
    }

    [TestMethod(DisplayName = "UT-WEA-072 [WEA-001/NFR-PRI-003] Manual place selection disables automatic device location")]
    public async Task ManualSelectionDisablesAutomaticLocation()
    {
        var client = new FakeWeatherSettingsClient();
        var viewModel = new WeatherSettingsViewModel(client, static key => key);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectSearchResult(WeatherLocationOption.FromCandidate(
            new WeatherLocationCandidateDto
            {
                Name = "Tokyo",
                Country = "Japan",
                Latitude = 35.6762,
                Longitude = 139.6503,
                Timezone = "Asia/Tokyo",
            }));
        Assert.IsTrue(await viewModel.SaveAsync(CancellationToken.None));

        Assert.IsNotNull(client.LastSaveRequest);
        Assert.IsFalse(client.LastSaveRequest!.UseDeviceLocation);
    }

    private sealed class FakeWeatherSettingsClient : IWeatherSettingsClient
    {
        public WeatherSettingsSaveRequest? LastSaveRequest { get; private set; }

        public string? LastSearchQuery { get; private set; }

        public IReadOnlyList<WeatherLocationCandidateDto> SearchResults { get; set; } =
            Array.Empty<WeatherLocationCandidateDto>();

        public Exception? SearchFailure { get; set; }

        public Task<IReadOnlyList<WeatherLocationCandidateDto>> SearchLocationsAsync(
            string query,
            CancellationToken cancellationToken)
        {
            LastSearchQuery = query;
            return SearchFailure is null
                ? Task.FromResult(SearchResults)
                : Task.FromException<IReadOnlyList<WeatherLocationCandidateDto>>(
                    SearchFailure);
        }

        public Task<WeatherSettingsDto> GetWeatherSettingsAsync(
            string instanceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WeatherSettingsDto
            {
                InstanceId = instanceId,
                Label = WeatherSettingsContract.DefaultLabel,
                Latitude = WeatherSettingsContract.DefaultLatitude,
                Longitude = WeatherSettingsContract.DefaultLongitude,
                UseDeviceLocation = true,
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
                UseDeviceLocation = request.UseDeviceLocation,
                Revision = request.ExpectedRevision + 1,
                UpdatedAtUtc = "2026-08-15T10:00:00.0000000Z",
            });
        }
    }

    private sealed class FakeDeviceLocationProvider(DeviceLocationResult result)
        : IDeviceLocationProvider
    {
        public Task<DeviceLocationResult> GetCurrentLocationAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
