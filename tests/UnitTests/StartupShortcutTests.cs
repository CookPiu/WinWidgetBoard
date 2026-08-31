using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The autostart switch reads and writes the shell's own state - a shortcut in the Startup
/// folder - so these run against two real temporary directories rather than a stub: the whole
/// point of the design is that there is no second copy of the fact to test against.
/// </summary>
[TestClass]
public sealed class StartupShortcutTests
{
    private string _root = string.Empty;
    private string _sourcePath = string.Empty;
    private string _startupPath = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "wwb-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "programs"));
        Directory.CreateDirectory(Path.Combine(_root, "startup"));
        _sourcePath = Path.Combine(
            _root,
            "programs",
            StartupShortcut.ShortcutFileName);
        _startupPath = Path.Combine(
            _root,
            "startup",
            StartupShortcut.ShortcutFileName);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod(DisplayName =
        "UT-GEN-001 [PNL-001] Enabling copies the installed shortcut into Startup")]
    public void EnablingCopiesTheInstalledShortcut()
    {
        File.WriteAllText(_sourcePath, "shortcut");
        var shortcut = new StartupShortcut(_sourcePath, _startupPath);

        Assert.IsTrue(shortcut.IsAvailable);
        Assert.IsFalse(shortcut.IsEnabled);
        Assert.IsTrue(shortcut.TrySetEnabled(true));

        // The copy has to be byte-identical: the installer is the single author of what
        // "launching WinWidgetBoard" means, and this must not write a second definition.
        Assert.IsTrue(shortcut.IsEnabled);
        Assert.AreEqual("shortcut", File.ReadAllText(_startupPath));
    }

    [TestMethod(DisplayName =
        "UT-GEN-002 [PNL-001] Disabling removes it, and can still remove it with no source")]
    public void DisablingRemovesTheShortcutEvenWithoutASource()
    {
        File.WriteAllText(_startupPath, "shortcut");
        var shortcut = new StartupShortcut(_sourcePath, _startupPath);

        // A user must be able to stop an application from starting itself even after its
        // installer is gone, so "off" never depends on the source being there.
        Assert.IsTrue(shortcut.IsEnabled);
        Assert.IsTrue(shortcut.IsAvailable);
        Assert.IsTrue(shortcut.TrySetEnabled(false));
        Assert.IsFalse(shortcut.IsEnabled);
        Assert.IsFalse(File.Exists(_startupPath));
    }

    [TestMethod(DisplayName =
        "UT-GEN-003 [PNL-001] With nothing to copy the switch is unavailable, not silently off")]
    public void WithNothingToCopyTheSwitchIsUnavailable()
    {
        var shortcut = new StartupShortcut(_sourcePath, _startupPath);

        // A build run straight out of artifacts\ has no Start-menu shortcut. Reporting that
        // is the difference between a disabled switch and one that quietly does nothing.
        Assert.IsFalse(shortcut.IsAvailable);
        Assert.IsFalse(shortcut.TrySetEnabled(true));
        Assert.IsFalse(shortcut.IsEnabled);
    }

    [TestMethod(DisplayName =
        "UT-GEN-004 [PNL-001] Turning it on twice replaces the copy rather than failing")]
    public void TurningItOnTwiceReplacesTheCopy()
    {
        File.WriteAllText(_sourcePath, "first");
        var shortcut = new StartupShortcut(_sourcePath, _startupPath);
        Assert.IsTrue(shortcut.TrySetEnabled(true));

        // An upgrade rewrites the Start-menu shortcut; re-applying has to pick that up rather
        // than leaving the Startup copy pointing at wherever the old build lived.
        File.WriteAllText(_sourcePath, "second");
        File.Delete(_startupPath);
        Assert.IsTrue(shortcut.TrySetEnabled(true));
        Assert.AreEqual("second", File.ReadAllText(_startupPath));
    }

    [TestMethod(DisplayName =
        "UT-GEN-005 [PNL-001] The view model reports the shell's state, not its own")]
    public void ViewModelReportsTheShellState()
    {
        File.WriteAllText(_sourcePath, "shortcut");
        var shortcut = new StartupShortcut(_sourcePath, _startupPath);
        var viewModel = new GeneralSettingsViewModel(shortcut, key => key);

        Assert.IsTrue(viewModel.IsAutostartAvailable);
        Assert.IsFalse(viewModel.IsAutostartEnabled);

        viewModel.IsAutostartEnabled = true;
        Assert.IsTrue(File.Exists(_startupPath));
        Assert.AreEqual("GeneralAutostartEnabledStatus", viewModel.StatusText);

        viewModel.IsAutostartEnabled = false;
        Assert.IsFalse(File.Exists(_startupPath));
        Assert.AreEqual("GeneralAutostartDisabledStatus", viewModel.StatusText);
    }

    [TestMethod(DisplayName =
        "UT-GEN-006 [PNL-001] A failed switch snaps back to what the machine holds")]
    public void AFailedSwitchSnapsBack()
    {
        var shortcut = new StartupShortcut(_sourcePath, _startupPath);
        var viewModel = new GeneralSettingsViewModel(shortcut, key => key);

        // Nothing to copy: the switch must not sit in a position the machine disagrees with,
        // which is what a plain two-way binding would leave behind.
        viewModel.IsAutostartEnabled = true;

        Assert.IsFalse(viewModel.IsAutostartEnabled);
        Assert.AreEqual("GeneralAutostartFailedStatus", viewModel.StatusText);
    }
}
