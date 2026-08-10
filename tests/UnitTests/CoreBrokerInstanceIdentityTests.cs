using WinWidgetBoard.CoreBroker.Hosting;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CoreBrokerInstanceIdentityTests
{
    [TestMethod(DisplayName = "UT-BROKER-LAUNCH-005 [SYS-001] Production keeps the stable CoreBroker singleton")]
    public void ProductionKeepsStableMutex()
    {
        string mutexName = CoreBrokerInstanceIdentity.ResolveMutexName([
            "WinWidgetBoard.CoreBroker.exe",
            CoreBrokerInstanceIdentity.TestInstanceIdSwitch,
            Guid.NewGuid().ToString("N"),
        ]);

        Assert.AreEqual(
            CoreBrokerInstanceIdentity.ProductionMutexName,
            mutexName);
    }

    [TestMethod(DisplayName = "UT-BROKER-LAUNCH-006 [SYS-001] Acceptance CoreBroker uses an isolated singleton")]
    public void AcceptanceUsesIsolatedMutex()
    {
        Guid instanceId = Guid.NewGuid();

        string mutexName = CoreBrokerInstanceIdentity.ResolveMutexName([
            "WinWidgetBoard.CoreBroker.exe",
            CoreBrokerInstanceIdentity.AcceptanceTestSwitch,
            CoreBrokerInstanceIdentity.TestInstanceIdSwitch,
            instanceId.ToString("D"),
        ]);

        Assert.AreEqual(
            $"{CoreBrokerInstanceIdentity.ProductionMutexName}.Acceptance.{instanceId:N}",
            mutexName);
    }

    [TestMethod(DisplayName = "UT-BROKER-LAUNCH-007 [SYS-001] Acceptance without an instance ID stays on the production singleton")]
    public void AcceptanceWithoutInstanceIdKeepsProductionMutex()
    {
        string mutexName = CoreBrokerInstanceIdentity.ResolveMutexName([
            "WinWidgetBoard.CoreBroker.exe",
            CoreBrokerInstanceIdentity.AcceptanceTestSwitch,
        ]);

        Assert.AreEqual(
            CoreBrokerInstanceIdentity.ProductionMutexName,
            mutexName);
    }

    [TestMethod(DisplayName = "UT-BROKER-LAUNCH-008 [SYS-001] Invalid or repeated acceptance instance IDs fail closed")]
    public void InvalidOrRepeatedAcceptanceInstanceIdsFailClosed()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            CoreBrokerInstanceIdentity.ResolveMutexName([
                "WinWidgetBoard.CoreBroker.exe",
                CoreBrokerInstanceIdentity.AcceptanceTestSwitch,
                CoreBrokerInstanceIdentity.TestInstanceIdSwitch,
                "not-a-guid",
            ]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            CoreBrokerInstanceIdentity.ResolveMutexName([
                "WinWidgetBoard.CoreBroker.exe",
                CoreBrokerInstanceIdentity.AcceptanceTestSwitch,
                CoreBrokerInstanceIdentity.TestInstanceIdSwitch,
                Guid.NewGuid().ToString("N"),
                CoreBrokerInstanceIdentity.TestInstanceIdSwitch,
                Guid.NewGuid().ToString("N"),
            ]));
    }
}
