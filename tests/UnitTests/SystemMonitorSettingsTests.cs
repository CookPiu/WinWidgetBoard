using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Runtime;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class SystemMonitorSettingsTests
{
    [TestMethod(DisplayName =
        "UT-SYSMON-040 [MON-003] An item list rejects unknown, duplicate and overlong entries")]
    public void ItemListValidationRejectsBadInput()
    {
        Assert.IsTrue(
            SystemMonitorContract.IsValidItemList(
                SystemMonitorContract.DefaultCardItems),
            "The shipped defaults must themselves be valid.");

        Assert.IsFalse(
            SystemMonitorContract.IsValidItemList(
            [
                new SystemMonitorItemDto { MetricId = "cpu.aura" },
            ]),
            "An unknown metric id has no name and no reading.");

        Assert.IsFalse(
            SystemMonitorContract.IsValidItemList(
            [
                new SystemMonitorItemDto { MetricId = SystemMonitorContract.CpuUsage },
                new SystemMonitorItemDto { MetricId = SystemMonitorContract.CpuUsage },
            ]),
            "The same reading twice is a mistake, not a layout.");

        Assert.IsFalse(
            SystemMonitorContract.IsValidItemList(
                SystemMonitorContract.MetricIds
                    .Select(id => new SystemMonitorItemDto { MetricId = id })
                    .ToArray()),
            "Twelve metrics exceed what either surface can show.");
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-041 [MON-003] Loading places the saved metrics first, in saved order")]
    public async Task LoadOrdersSelectedMetricsFirst()
    {
        var client = new FakeSystemMonitorClient
        {
            Settings = new SystemMonitorSettingsDto
            {
                InstanceId = BuiltInCardCatalog.SystemMonitorInstanceId,
                CardItems =
                [
                    new SystemMonitorItemDto
                    {
                        MetricId = SystemMonitorContract.NetworkDown,
                        Detail = SystemMonitorDetail.Compact,
                    },
                    new SystemMonitorItemDto
                    {
                        MetricId = SystemMonitorContract.CpuUsage,
                        Detail = SystemMonitorDetail.Detailed,
                    },
                ],
                EntryItems = [],
                Revision = 3,
            },
        };
        var viewModel = new SystemMonitorSettingsViewModel(client);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.AreEqual(3, viewModel.Revision);
        Assert.AreEqual(SystemMonitorContract.NetworkDown, viewModel.CardOptions[0].MetricId);
        Assert.AreEqual(SystemMonitorContract.CpuUsage, viewModel.CardOptions[1].MetricId);
        Assert.IsTrue(viewModel.CardOptions[0].IsSelected);
        Assert.AreEqual(
            SystemMonitorDetail.Detailed,
            viewModel.CardOptions[1].Detail);
        Assert.IsFalse(
            viewModel.CardOptions[2].IsSelected,
            "Everything else stays listed but unselected.");
        Assert.AreEqual(0, viewModel.EntryOptions.Count(option => option.IsSelected));
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-042 [MON-003] Saving sends list order and the expected revision")]
    public async Task SaveSendsOrderAndRevision()
    {
        var client = new FakeSystemMonitorClient
        {
            Settings = new SystemMonitorSettingsDto
            {
                InstanceId = BuiltInCardCatalog.SystemMonitorInstanceId,
                CardItems = [],
                EntryItems = [],
                Revision = 7,
            },
        };
        var viewModel = new SystemMonitorSettingsViewModel(client);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.CardOptions[0].IsSelected = true;
        viewModel.CardOptions[1].IsSelected = true;
        SystemMonitorSettingsViewModel.MoveUp(
            viewModel.CardOptions,
            viewModel.CardOptions[1]);

        Assert.IsTrue(await viewModel.SaveAsync(CancellationToken.None));
        Assert.IsNotNull(client.LastSave);
        Assert.AreEqual(7, client.LastSave!.ExpectedRevision);
        Assert.AreEqual(
            2,
            client.LastSave.CardItems!.Count,
            "Only checked metrics are sent.");
        Assert.AreEqual(
            viewModel.CardOptions[0].MetricId,
            client.LastSave.CardItems[0].MetricId,
            "Display order is list order.");
        Assert.IsTrue(viewModel.WasSaved);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-043 [MON-003] Selecting more than a surface can show blocks the save")]
    public async Task OverfullSurfaceIsRejectedLocally()
    {
        var client = new FakeSystemMonitorClient();
        var viewModel = new SystemMonitorSettingsViewModel(client);
        await viewModel.LoadAsync(CancellationToken.None);

        foreach (SystemMonitorMetricOption option in viewModel.CardOptions)
        {
            option.IsSelected = true;
        }

        Assert.IsFalse(await viewModel.SaveAsync(CancellationToken.None));
        Assert.IsNull(client.LastSave, "Nothing should reach the broker.");
        Assert.AreNotEqual(string.Empty, viewModel.ValidationText);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-044 [MON-003] A revision conflict is reported as its own state")]
    public async Task ConflictHasItsOwnWording()
    {
        var client = new FakeSystemMonitorClient
        {
            SaveError = new CoreBrokerClientException(
                SystemMonitorContract.SettingsSaveMethod,
                "conflict.sysmon-settings-revision",
                "conflict"),
        };
        var viewModel = new SystemMonitorSettingsViewModel(client, key => key);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.CardOptions[0].IsSelected = true;

        Assert.IsFalse(await viewModel.SaveAsync(CancellationToken.None));
        Assert.AreEqual("SysMonSettingsConflictStatus", viewModel.StatusText);
        Assert.IsFalse(viewModel.WasSaved);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-045 [MON-003] With no broker the dialog reports unavailable and cannot save")]
    public async Task NoClientIsUnavailable()
    {
        var viewModel = new SystemMonitorSettingsViewModel(null, key => key);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.IsFalse(viewModel.IsAvailable);
        Assert.IsFalse(viewModel.CanSave);
        Assert.AreEqual("SysMonSettingsUnavailableStatus", viewModel.StatusText);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-046 [MON-003] Each surface gives its rows distinct automation IDs")]
    public void AutomationIdsDistinguishTheTwoSurfaces()
    {
        var viewModel = new SystemMonitorSettingsViewModel(new FakeSystemMonitorClient());

        SystemMonitorMetricOption card = viewModel.CardOptions[0];
        SystemMonitorMetricOption entry = viewModel.EntryOptions.Single(
            option => option.MetricId == card.MetricId);

        Assert.AreNotEqual(card.IncludeAutomationId, entry.IncludeAutomationId);
        Assert.AreNotEqual(card.MoveUpAutomationId, entry.MoveUpAutomationId);
        Assert.IsTrue(card.IncludeAutomationId.Contains("Card", StringComparison.Ordinal));
        Assert.IsTrue(entry.IncludeAutomationId.Contains("Entry", StringComparison.Ordinal));
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-050 [MON-003] An unusable network source degrades to all adapters")]
    public void AnUnusableNetworkSourceDegradesToAllAdapters()
    {
        Assert.IsTrue(SystemMonitorContract.IsValidNetworkInterfaceId(null));
        Assert.IsTrue(SystemMonitorContract.IsValidNetworkInterfaceId(string.Empty));
        Assert.IsTrue(SystemMonitorContract.IsValidNetworkInterfaceId("{adapter-a}"));
        Assert.IsFalse(SystemMonitorContract.IsValidNetworkInterfaceId("badid"));
        Assert.IsFalse(
            SystemMonitorContract.IsValidNetworkInterfaceId(
                new string('x', SystemMonitorContract.MaxNetworkInterfaceIdLength + 1)));

        // Normalizing rather than throwing is what lets a row written by a newer build, or a
        // corrupted one, still be read: the readings fall back to the previous behaviour.
        Assert.AreEqual(
            SystemMonitorContract.AllNetworkInterfaces,
            SystemMonitorContract.NormalizeNetworkInterfaceId("badid"));
        Assert.AreEqual(
            "{adapter-a}",
            SystemMonitorContract.NormalizeNetworkInterfaceId("{adapter-a}"));
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-047 [MON-003] The network source list starts at all adapters and restores the stored choice")]
    public async Task NetworkSourceListStartsAtAllAdaptersAndRestoresTheStoredChoice()
    {
        var client = new FakeSystemMonitorClient
        {
            NetworkInterfaces =
            [
                new() { Id = "{adapter-a}", Name = "Ethernet" },
                new() { Id = "{adapter-b}", Name = "Wi-Fi" },
            ],
            Settings = new SystemMonitorSettingsDto
            {
                InstanceId = BuiltInCardCatalog.SystemMonitorInstanceId,
                CardItems = SystemMonitorContract.DefaultCardItems,
                EntryItems = SystemMonitorContract.DefaultEntryItems,
                NetworkInterfaceId = "{adapter-b}",
                Revision = 3,
            },
        };
        var viewModel = new SystemMonitorSettingsViewModel(client);

        await viewModel.LoadAsync(CancellationToken.None);

        // "All adapters" is what the rates meant before the source was configurable, so it is
        // the first row rather than one option among equals.
        Assert.AreEqual(3, viewModel.NetworkOptions.Count);
        Assert.AreEqual(
            SystemMonitorContract.AllNetworkInterfaces,
            viewModel.NetworkOptions[0].Id);
        Assert.AreEqual("{adapter-b}", viewModel.SelectedNetworkInterfaceId);
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-048 [MON-003] A stored adapter the machine no longer reports stays selectable")]
    public async Task AStoredAdapterTheMachineNoLongerReportsStaysSelectable()
    {
        var client = new FakeSystemMonitorClient
        {
            NetworkInterfaces = [new() { Id = "{adapter-a}", Name = "Ethernet" }],
            Settings = new SystemMonitorSettingsDto
            {
                InstanceId = BuiltInCardCatalog.SystemMonitorInstanceId,
                CardItems = SystemMonitorContract.DefaultCardItems,
                EntryItems = SystemMonitorContract.DefaultEntryItems,
                NetworkInterfaceId = "{unplugged}",
                Revision = 1,
            },
        };
        var viewModel = new SystemMonitorSettingsViewModel(client);

        await viewModel.LoadAsync(CancellationToken.None);

        // Dropping the row would show "all adapters" while the broker still holds the missing
        // one, and the next save would discard a choice the user never changed.
        Assert.AreEqual("{unplugged}", viewModel.SelectedNetworkInterfaceId);
        Assert.IsTrue(
            viewModel.NetworkOptions.Any(option => option.Id == "{unplugged}"));
    }

    [TestMethod(DisplayName =
        "UT-SYSMON-049 [MON-003] Saving sends the selected network source")]
    public async Task SavingSendsTheSelectedNetworkSource()
    {
        var client = new FakeSystemMonitorClient
        {
            NetworkInterfaces =
            [
                new() { Id = "{adapter-a}", Name = "Ethernet" },
                new() { Id = "{adapter-b}", Name = "Wi-Fi" },
            ],
        };
        var viewModel = new SystemMonitorSettingsViewModel(client);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedNetworkIndex = 2;
        Assert.IsTrue(await viewModel.SaveAsync(CancellationToken.None));

        Assert.AreEqual("{adapter-b}", client.LastSave?.NetworkInterfaceId);
        Assert.AreEqual("{adapter-b}", viewModel.SelectedNetworkInterfaceId);
    }

    private sealed class FakeSystemMonitorClient : ISystemMonitorSettingsClient
    {
        public SystemMonitorSettingsDto Settings { get; set; } = new()
        {
            InstanceId = BuiltInCardCatalog.SystemMonitorInstanceId,
            CardItems = [],
            EntryItems = [],
            Revision = 0,
        };

        public SystemMonitorSettingsSaveRequest? LastSave { get; private set; }

        public CoreBrokerClientException? SaveError { get; set; }

        /// <summary>What the broker would report this machine can measure.</summary>
        public IReadOnlyList<SystemMonitorNetworkInterfaceDto> NetworkInterfaces { get; set; } =
            [];

        public Task<SystemMonitorSettingsGetResponse> GetSystemMonitorSettingsAsync(
            string instanceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new SystemMonitorSettingsGetResponse
                {
                    Settings = Settings,
                    NetworkInterfaces = NetworkInterfaces,
                });

        public Task<SystemMonitorSettingsDto> SaveSystemMonitorSettingsAsync(
            SystemMonitorSettingsSaveRequest request,
            CancellationToken cancellationToken)
        {
            if (SaveError is not null)
            {
                return Task.FromException<SystemMonitorSettingsDto>(SaveError);
            }

            LastSave = request;
            Settings = new SystemMonitorSettingsDto
            {
                InstanceId = request.InstanceId ?? string.Empty,
                CardItems = request.CardItems ?? [],
                EntryItems = request.EntryItems ?? [],
                NetworkInterfaceId =
                    SystemMonitorContract.NormalizeNetworkInterfaceId(
                        request.NetworkInterfaceId),
                Revision = request.ExpectedRevision + 1,
            };
            return Task.FromResult(Settings);
        }
    }
}
