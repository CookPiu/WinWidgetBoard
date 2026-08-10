using WinWidgetBoard.WorkspacePanel.Shell;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class WorkspacePanelInstanceIdentityTests
{
    [TestMethod(DisplayName = "UT-BUILD-002 [SYS-001] Production launches keep the stable singleton mutex")]
    public void ProductionLaunchKeepsStableMutex()
    {
        string instanceId = Guid.NewGuid().ToString("N");

        string mutexName = WorkspacePanelInstanceIdentity.ResolveMutexName([
            "WinWidgetBoard.WorkspacePanel.exe",
            WorkspacePanelInstanceIdentity.TestInstanceIdSwitch,
            instanceId,
        ]);

        Assert.AreEqual(
            WorkspacePanelInstanceIdentity.ProductionMutexName,
            mutexName);
    }

    [TestMethod(DisplayName = "UT-BUILD-003 [SYS-001] Acceptance launches use an isolated singleton mutex")]
    public void AcceptanceLaunchUsesIsolatedMutex()
    {
        Guid instanceId = Guid.NewGuid();

        string mutexName = WorkspacePanelInstanceIdentity.ResolveMutexName([
            "WinWidgetBoard.WorkspacePanel.exe",
            WorkspacePanelInstanceIdentity.AcceptanceTestSwitch,
            WorkspacePanelInstanceIdentity.TestInstanceIdSwitch,
            instanceId.ToString("D"),
        ]);

        Assert.AreEqual(
            $"{WorkspacePanelInstanceIdentity.ProductionMutexName}.Acceptance.{instanceId:N}",
            mutexName);
    }

    [TestMethod(DisplayName = "UT-BUILD-004 [BLD-001] Smoke launches can isolate window construction")]
    public void SmokeLaunchCanUseIsolatedMutex()
    {
        Guid instanceId = Guid.NewGuid();

        string mutexName = WorkspacePanelInstanceIdentity.ResolveMutexName([
            "WinWidgetBoard.WorkspacePanel.exe",
            "--smoke-test",
            WorkspacePanelInstanceIdentity.TestInstanceIdSwitch,
            instanceId.ToString("N"),
        ]);

        StringAssert.EndsWith(mutexName, instanceId.ToString("N"));
    }

    [TestMethod(DisplayName = "UT-BUILD-005 [BLD-001] Invalid or repeated acceptance instance IDs fail closed")]
    public void InvalidOrRepeatedAcceptanceInstanceIdsFailClosed()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            WorkspacePanelInstanceIdentity.ResolveMutexName([
                "WinWidgetBoard.WorkspacePanel.exe",
                WorkspacePanelInstanceIdentity.AcceptanceTestSwitch,
                WorkspacePanelInstanceIdentity.TestInstanceIdSwitch,
                "not-a-guid",
            ]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            WorkspacePanelInstanceIdentity.ResolveMutexName([
                "WinWidgetBoard.WorkspacePanel.exe",
                WorkspacePanelInstanceIdentity.AcceptanceTestSwitch,
                WorkspacePanelInstanceIdentity.TestInstanceIdSwitch,
                Guid.NewGuid().ToString("N"),
                WorkspacePanelInstanceIdentity.TestInstanceIdSwitch,
                Guid.NewGuid().ToString("N"),
            ]));
    }
}
