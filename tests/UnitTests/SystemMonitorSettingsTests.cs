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

        public Task<SystemMonitorSettingsDto> GetSystemMonitorSettingsAsync(
            string instanceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Settings);

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
                Revision = request.ExpectedRevision + 1,
            };
            return Task.FromResult(Settings);
        }
    }
}
