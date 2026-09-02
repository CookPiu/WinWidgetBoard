using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;
using WinWidgetBoard.WorkspacePanel.Motion;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.WorkspacePanel;

/// <summary>
/// Which category the settings dialog opens on. The caller decides: the header button opens
/// the dialog cold, while a card's own settings button is a shortcut into that card's section
/// and must land there rather than making the user find it again.
/// </summary>
public enum SettingsCategory
{
    General = 0,
    TaskbarEntry = 1,
    Weather = 2,
    SystemMonitor = 3,
    TokenUsage = 4,
}

/// <summary>
/// The one settings surface. Every card's settings live here as a section; a card that offers
/// its own settings button deep-links into its section instead of opening a dialog of its own.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog, IDisposable
{
    public SettingsDialog(
        GeneralSettingsViewModel generalViewModel,
        LauncherEntrySettingsViewModel entryViewModel,
        WeatherSettingsViewModel weatherViewModel,
        SystemMonitorSettingsViewModel systemMonitorViewModel,
        TokenUsageSettingsViewModel tokenUsageViewModel,
        SettingsCategory initialCategory = SettingsCategory.General)
    {
        GeneralViewModel = generalViewModel ??
            throw new ArgumentNullException(nameof(generalViewModel));
        EntryViewModel = entryViewModel ??
            throw new ArgumentNullException(nameof(entryViewModel));
        WeatherViewModel = weatherViewModel ??
            throw new ArgumentNullException(nameof(weatherViewModel));
        SystemMonitorViewModel = systemMonitorViewModel ??
            throw new ArgumentNullException(nameof(systemMonitorViewModel));
        TokenUsageViewModel = tokenUsageViewModel ??
            throw new ArgumentNullException(nameof(tokenUsageViewModel));
        InitializeComponent();
        _surfaceMotion = new SurfaceMotionCoordinator(
            reducedMotion: !new UISettings().AnimationsEnabled,
            highContrast: new AccessibilitySettings().HighContrast);
        SettingsCategoryList.SelectedIndex = (int)initialCategory;
        // The first category is placed, not animated: the dialog is still playing its own
        // entrance, and a section fading in inside a sheet that is itself fading in reads as
        // a stutter rather than as two things happening.
        ApplyCategory((int)initialCategory, animate: false);
        Closed += SettingsDialog_Closed;
    }

    private readonly SurfaceMotionCoordinator _surfaceMotion;

    public GeneralSettingsViewModel GeneralViewModel { get; }

    public LauncherEntrySettingsViewModel EntryViewModel { get; }

    public WeatherSettingsViewModel WeatherViewModel { get; }

    public SystemMonitorSettingsViewModel SystemMonitorViewModel { get; }

    public TokenUsageSettingsViewModel TokenUsageViewModel { get; }

    private void SettingsCategoryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is ListView list)
        {
            ApplyCategory(list.SelectedIndex, animate: true);
        }
    }

    private void ApplyCategory(int index, bool animate)
    {
        // A negative index means the list cleared its selection, which happens while the
        // dialog is being torn down. Leave whatever is on screen rather than blanking both.
        // The null check is not defensive padding: SelectionChanged fires while the rail is
        // still being parsed, before the sections further down the tree exist.
        if (index < 0 ||
            GeneralSection is null ||
            TaskbarEntrySection is null ||
            WeatherSection is null ||
            SystemMonitorSection is null ||
            TokenUsageSection is null ||
            WeatherFooter is null ||
            SystemMonitorFooter is null ||
            TokenUsageFooter is null)
        {
            return;
        }

        SetSection(index, SettingsCategory.General, animate, GeneralSection);
        SetSection(
            index,
            SettingsCategory.TaskbarEntry,
            animate,
            TaskbarEntrySection);
        SetSection(
            index,
            SettingsCategory.Weather,
            animate,
            WeatherSection,
            WeatherFooter);
        SetSection(
            index,
            SettingsCategory.SystemMonitor,
            animate,
            SystemMonitorSection,
            SystemMonitorFooter);
        SetSection(
            index,
            SettingsCategory.TokenUsage,
            animate,
            TokenUsageSection,
            TokenUsageFooter);
    }

    /// <summary>
    /// Shows or hides one category's parts. The outgoing category is dropped in one frame
    /// rather than faded out: its section and the incoming one share the same cell, and two
    /// pages of dense text dissolving through each other reads as a rendering fault. What
    /// carries the change is the incoming section fading up - the same short compositor-only
    /// transition every other progressive-disclosure surface in the panel uses, so it is
    /// interruptible when the rail is clicked through quickly and flattens under reduced
    /// motion.
    /// </summary>
    private void SetSection(
        int index,
        SettingsCategory category,
        bool animate,
        params FrameworkElement[] parts)
    {
        bool visible = index == (int)category;
        foreach (FrameworkElement part in parts)
        {
            if (!visible)
            {
                _surfaceMotion.HideImmediately(part);
            }
            else if (animate)
            {
                _surfaceMotion.Show(part, SurfaceMotionAnchor.Top);
            }
            else
            {
                part.Visibility = Visibility.Visible;
            }
        }
    }

    private async void TokenUsageSettingsSaveButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        await TokenUsageViewModel.SaveAsync(CancellationToken.None);
    }

    private async void WeatherSettingsSaveButton_Click(object sender, RoutedEventArgs args)
    {
        if (await WeatherViewModel.SaveAsync(CancellationToken.None))
        {
            Hide();
        }
    }

    private async void WeatherSettingsDeviceLocationButton_Click(
        object sender,
        RoutedEventArgs args)
    {
        await WeatherViewModel.RefreshDeviceLocationAsync(CancellationToken.None);
    }

    // Search runs on submit, never per keystroke: every search leaves the machine, and a
    // request per character would send far more of what the user typed than they asked to.
    private async void WeatherSettingsSearchButton_Click(object sender, RoutedEventArgs args)
    {
        await WeatherViewModel.SearchAsync(CancellationToken.None);
    }

    private async void WeatherSettingsSearchBox_KeyDown(
        object sender,
        Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        args.Handled = true;
        await WeatherViewModel.SearchAsync(CancellationToken.None);
    }

    private void WeatherSettingsResultsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is ListView { SelectedItem: WeatherLocationOption option })
        {
            WeatherViewModel.SelectSearchResult(option);
        }
    }

    private async void SysMonSettingsSaveButton_Click(object sender, RoutedEventArgs args)
    {
        if (await SystemMonitorViewModel.SaveAsync(CancellationToken.None))
        {
            Hide();
        }
    }

    /// <summary>
    /// Records a chord from a single key press: the modifiers held at that moment plus the
    /// key itself. Modifier keys alone are ignored - a chord ends with a real key - and the
    /// registration outcome is read back shortly after, because the launcher, not this
    /// dialog, is the process that actually registers it.
    /// </summary>
    private void EntryHotkeyCaptureBox_KeyDown(
        object sender,
        Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        Windows.System.VirtualKey key = args.Key;
        if (key is Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.LeftWindows or
            Windows.System.VirtualKey.RightWindows)
        {
            args.Handled = true;
            return;
        }

        int modifiers =
            (IsKeyDown(Windows.System.VirtualKey.Control)
                ? LauncherEntrySettingsViewModel.ModifierControl
                : 0) |
            (IsKeyDown(Windows.System.VirtualKey.Menu)
                ? LauncherEntrySettingsViewModel.ModifierAlt
                : 0) |
            (IsKeyDown(Windows.System.VirtualKey.Shift)
                ? LauncherEntrySettingsViewModel.ModifierShift
                : 0);
        args.Handled = true;
        if (EntryViewModel.TrySetCustomHotkey(modifiers, (int)key))
        {
            ScheduleHotkeyStateRefresh();
        }
    }

    private void EntryHotkeyModeBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        ScheduleHotkeyStateRefresh();
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Reads the launcher's registration write-back once, after it has had a moment to see
    /// the change. One shot, not a poll: the launcher applies within milliseconds, and a
    /// second look on the next change costs nothing.
    /// </summary>
    private void ScheduleHotkeyStateRefresh()
    {
        if (_hotkeyStateTimer is null)
        {
            _hotkeyStateTimer = DispatcherQueue.CreateTimer();
            _hotkeyStateTimer.Interval = TimeSpan.FromMilliseconds(800);
            _hotkeyStateTimer.IsRepeating = false;
            _hotkeyStateTimer.Tick += HotkeyStateTimer_Tick;
        }

        _hotkeyStateTimer.Stop();
        _hotkeyStateTimer.Start();
    }

    private void HotkeyStateTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        EntryViewModel.RefreshHotkeyState();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hotkeyStateTimer;

    /// <summary>
    /// Releases the section transition's state. Closing the dialog does this on its own; the
    /// interface is here so a caller that never got to show it - the dialog is built before
    /// its sections are loaded - still cleans up.
    /// </summary>
    public void Dispose() => _surfaceMotion.Dispose();

    private void SettingsDialog_Closed(
        ContentDialog sender,
        ContentDialogClosedEventArgs args)
    {
        WeatherViewModel.CancelDraft();
        Dispose();
        Closed -= SettingsDialog_Closed;
    }
}
