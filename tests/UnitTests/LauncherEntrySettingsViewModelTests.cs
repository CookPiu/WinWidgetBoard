using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The taskbar entry's settings apply straight to the launcher's HKCU key, which the
/// launcher watches; there is no commit row, so what these tests pin down is that every
/// change is written immediately and that a recorded chord is validated before it is stored.
/// </summary>
[TestClass]
public sealed class LauncherEntrySettingsViewModelTests
{
    [TestMethod(DisplayName =
        "UT-ENTRY-001 [ENT-005] Missing values load as the launcher's own defaults")]
    public void MissingValuesLoadAsLauncherDefaults()
    {
        var viewModel = new LauncherEntrySettingsViewModel(new FakeStore(), key => key);

        Assert.AreEqual(0, viewModel.ContentModeIndex);
        Assert.IsFalse(viewModel.ShowSystemMonitor);
        Assert.AreEqual(0, viewModel.PlacementIndex);
        Assert.AreEqual(0, viewModel.LeftAlignFallbackIndex);
        // Absent means the default preset, on - the launcher reads it the same way.
        Assert.AreEqual(1, viewModel.HotkeyModeIndex);
        Assert.IsFalse(viewModel.IsCustomHotkey);
    }

    [TestMethod(DisplayName =
        "UT-ENTRY-002 [ENT-005] A change is persisted immediately, not on a save")]
    public void ChangesPersistImmediately()
    {
        var store = new FakeStore();
        var viewModel = new LauncherEntrySettingsViewModel(store, key => key);

        viewModel.ContentModeIndex = 1;
        viewModel.ShowSystemMonitor = true;
        viewModel.PlacementIndex = 2;
        viewModel.LeftAlignFallbackIndex = 1;
        viewModel.HotkeyModeIndex = 0;

        Assert.AreEqual(1, store.Values["Content"]);
        Assert.AreEqual(1, store.Values["ShowSystemMonitor"]);
        Assert.AreEqual(2, store.Values["Placement"]);
        Assert.AreEqual(1, store.Values["LeftAlignFallback"]);
        Assert.AreEqual(0, store.Values["Hotkey"]);
        Assert.AreEqual("EntrySettingsAppliedStatus", viewModel.StatusText);
    }

    [TestMethod(DisplayName =
        "UT-ENTRY-003 [ENT-005] A recorded chord needs a real modifier and stores as custom")]
    public void RecordedChordIsValidatedAndStoredAsCustom()
    {
        var store = new FakeStore();
        var viewModel = new LauncherEntrySettingsViewModel(store, key => key);

        // No modifier: a bare global key would eat ordinary typing everywhere.
        Assert.IsFalse(viewModel.TrySetCustomHotkey(0, 'P'));
        // The Windows key belongs to the shell.
        Assert.IsFalse(viewModel.TrySetCustomHotkey(0x8, 'P'));

        Assert.IsTrue(viewModel.TrySetCustomHotkey(
            LauncherEntrySettingsViewModel.ModifierControl |
                LauncherEntrySettingsViewModel.ModifierShift,
            'P'));
        Assert.AreEqual(4, store.Values["Hotkey"]);
        Assert.AreEqual(
            LauncherEntrySettingsViewModel.ModifierControl |
                LauncherEntrySettingsViewModel.ModifierShift,
            store.Values["HotkeyModifiers"]);
        Assert.AreEqual('P', store.Values["HotkeyKey"]);
        Assert.IsTrue(viewModel.IsCustomHotkey);
        Assert.AreEqual("Ctrl + Shift + P", viewModel.CustomHotkeyText);
    }

    [TestMethod(DisplayName =
        "UT-ENTRY-004 [ENT-005] The launcher's write-back is what reports an occupied chord")]
    public void OccupiedChordIsReportedFromTheWriteBack()
    {
        var store = new FakeStore();
        var viewModel = new LauncherEntrySettingsViewModel(store, key => key);

        store.Values["HotkeyActive"] = 0;
        viewModel.RefreshHotkeyState();
        Assert.AreEqual("EntryHotkeyTakenStatus", viewModel.StatusText);

        store.Values["HotkeyActive"] = 1;
        viewModel.RefreshHotkeyState();
        Assert.AreEqual("EntrySettingsAppliedStatus", viewModel.StatusText);

        // A disabled hotkey has nothing to fail; the stale flag must not scare anyone.
        store.Values["HotkeyActive"] = 0;
        viewModel.HotkeyModeIndex = 0;
        viewModel.RefreshHotkeyState();
        Assert.AreEqual("EntrySettingsAppliedStatus", viewModel.StatusText);
    }

    private sealed class FakeStore : ILauncherPreferenceStore
    {
        public Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);

        public int? ReadValue(string name) =>
            Values.TryGetValue(name, out int value) ? value : null;

        public bool WriteValue(string name, int value)
        {
            Values[name] = value;
            return true;
        }
    }
}
