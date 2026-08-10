using WinWidgetBoard.CoreBroker.Hosting;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CoreBrokerDataDirectoryResolverTests
{
    [TestMethod(DisplayName = "UT-BROKER-LAUNCH-001 [DAT-001] Production resolves the known local data directory")]
    public void ProductionResolvesKnownLocalDataDirectory()
    {
        string localData = Path.Combine(
            Path.GetTempPath(),
            "production-local-data");
        string[] arguments = [];

        bool resolved = CoreBrokerDataDirectoryResolver.TryResolve(
            arguments,
            localData,
            Path.GetTempPath(),
            out string? dataDirectory,
            out string? error);

        Assert.IsTrue(resolved, error);
        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(localData), "WinWidgetBoard"),
            dataDirectory);
    }

    [TestMethod(DisplayName = "UT-BROKER-LAUNCH-002 [DAT-001] Production rejects a data directory override")]
    public void ProductionRejectsDataDirectoryOverride()
    {
        string requested = Path.Combine(
            Path.GetTempPath(),
            "WinWidgetBoard-override");
        string[] arguments = ["--data-directory", requested];

        bool resolved = CoreBrokerDataDirectoryResolver.TryResolve(
            arguments,
            Path.GetTempPath(),
            Path.GetTempPath(),
            out string? dataDirectory,
            out string? error);

        Assert.IsFalse(resolved);
        Assert.IsNull(dataDirectory);
        StringAssert.Contains(error, "--acceptance-test");
    }

    [TestMethod(DisplayName = "UT-BROKER-LAUNCH-003 [DAT-001] Acceptance resolves an isolated temp data directory")]
    public void AcceptanceResolvesIsolatedTempDataDirectory()
    {
        string requested = Path.Combine(
            Path.GetTempPath(),
            "WinWidgetBoard.Acceptance." + Guid.NewGuid().ToString("N"));
        string[] arguments = [
            "--acceptance-test",
            "--data-directory",
            requested,
        ];

        bool resolved = CoreBrokerDataDirectoryResolver.TryResolve(
            arguments,
            Path.GetTempPath(),
            Path.GetTempPath(),
            out string? dataDirectory,
            out string? error);

        Assert.IsTrue(resolved, error);
        Assert.AreEqual(Path.GetFullPath(requested), dataDirectory);
    }

    [TestMethod(DisplayName = "UT-BROKER-LAUNCH-004 [DAT-001] Acceptance rejects paths outside temp and malformed values")]
    public void AcceptanceRejectsUnsafeOrMalformedOverrides()
    {
        string outsideTemp = Path.Combine(
            Path.GetPathRoot(Path.GetTempPath())!,
            "WinWidgetBoard-outside-temp");
        string[][] invalidArguments = [
            ["--acceptance-test", "--data-directory", outsideTemp],
            ["--acceptance-test", "--data-directory", "relative-data"],
            ["--acceptance-test", "--data-directory"],
            [
                "--acceptance-test",
                "--data-directory",
                Path.Combine(Path.GetTempPath(), "first"),
                "--data-directory",
                Path.Combine(Path.GetTempPath(), "second"),
            ],
        ];

        foreach (string[] arguments in invalidArguments)
        {
            Assert.IsFalse(CoreBrokerDataDirectoryResolver.TryResolve(
                arguments,
                Path.GetTempPath(),
                Path.GetTempPath(),
                out string? dataDirectory,
                out string? error));
            Assert.IsNull(dataDirectory);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
    }
}
