using WinWidgetBoard.WorkspacePanel.Shell;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class StartupTraceTests
{
    [TestMethod(DisplayName =
        "UT-PANEL-TRACE-001 [NFR-PERF-001] Startup tracing stays off unless it is switched on")]
    public void TracingStaysOffUnlessSwitchedOn()
    {
        Assert.IsFalse(StartupTrace.ShouldEnable(null, null));
        Assert.IsFalse(StartupTrace.ShouldEnable(["WorkspacePanel.exe"], null));
        Assert.IsFalse(StartupTrace.ShouldEnable(null, "0"));
        Assert.IsFalse(StartupTrace.ShouldEnable(null, "   "));
        Assert.IsTrue(StartupTrace.ShouldEnable(
            ["WorkspacePanel.exe", StartupTrace.EnableSwitch],
            null));
        Assert.IsTrue(StartupTrace.ShouldEnable(null, "1"));
    }

    [TestMethod(DisplayName =
        "UT-PANEL-TRACE-002 [NFR-PERF-001] Marks report milliseconds since process start")]
    public void MarksReportMillisecondsSinceProcessStart()
    {
        var lines = new List<string>();
        try
        {
            StartupTrace.Initialize(
                [StartupTrace.EnableSwitch],
                environmentValue: null,
                processStartUtc: DateTime.UtcNow.AddMilliseconds(-250),
                sink: lines.Add);
            StartupTrace.Mark("window-constructed");

            Assert.AreEqual(1, lines.Count);
            StringAssert.StartsWith(lines[0], "STARTUP-TRACE window-constructed ");
            string value = lines[0].Split(' ')[2];
            Assert.IsTrue(double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double elapsed));
            Assert.IsTrue(elapsed >= 250, $"Elapsed {elapsed} did not include the process start offset.");
        }
        finally
        {
            // Leave the shared state disabled so no other test writes trace output.
            StartupTrace.Initialize([], null, DateTime.UtcNow);
        }
    }
}
