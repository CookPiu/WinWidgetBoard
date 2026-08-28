using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.WorkspacePanel;

/// <summary>
/// Which category the settings dialog opens on. The caller decides: the header button opens
/// the dialog cold, while a card's own settings button is a shortcut into that card's section
/// and must land there rather than making the user find it again.
/// </summary>
public enum SettingsCategory
{
    Weather = 0,
    SystemMonitor = 1,
    TokenUsage = 2,
}

/// <summary>
/// The one settings surface. Every card's settings live here as a section; a card that offers
/// its own settings button deep-links into its section instead of opening a dialog of its own.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(
        WeatherSettingsViewModel weatherViewModel,
        SystemMonitorSettingsViewModel systemMonitorViewModel,
        TokenUsageSettingsViewModel tokenUsageViewModel,
        SettingsCategory initialCategory = SettingsCategory.Weather)
    {
        WeatherViewModel = weatherViewModel ??
            throw new ArgumentNullException(nameof(weatherViewModel));
        SystemMonitorViewModel = systemMonitorViewModel ??
            throw new ArgumentNullException(nameof(systemMonitorViewModel));
        TokenUsageViewModel = tokenUsageViewModel ??
            throw new ArgumentNullException(nameof(tokenUsageViewModel));
        InitializeComponent();
        SettingsCategoryList.SelectedIndex = (int)initialCategory;
        ApplyCategory((int)initialCategory);
        Closed += SettingsDialog_Closed;
    }

    public WeatherSettingsViewModel WeatherViewModel { get; }

    public SystemMonitorSettingsViewModel SystemMonitorViewModel { get; }

    public TokenUsageSettingsViewModel TokenUsageViewModel { get; }

    private void SettingsCategoryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is ListView list)
        {
            ApplyCategory(list.SelectedIndex);
        }
    }

    private void ApplyCategory(int index)
    {
        // A negative index means the list cleared its selection, which happens while the
        // dialog is being torn down. Leave whatever is on screen rather than blanking both.
        // The null check is not defensive padding: SelectionChanged fires while the rail is
        // still being parsed, before the sections further down the tree exist.
        if (index < 0 ||
            WeatherSection is null ||
            SystemMonitorSection is null ||
            TokenUsageSection is null ||
            WeatherFooter is null ||
            SystemMonitorFooter is null ||
            TokenUsageFooter is null)
        {
            return;
        }

        Visibility forWeather = Show(index, SettingsCategory.Weather);
        Visibility forSystemMonitor = Show(index, SettingsCategory.SystemMonitor);
        Visibility forTokenUsage = Show(index, SettingsCategory.TokenUsage);
        WeatherSection.Visibility = forWeather;
        WeatherFooter.Visibility = forWeather;
        SystemMonitorSection.Visibility = forSystemMonitor;
        SystemMonitorFooter.Visibility = forSystemMonitor;
        TokenUsageSection.Visibility = forTokenUsage;
        TokenUsageFooter.Visibility = forTokenUsage;
    }

    private static Visibility Show(int index, SettingsCategory category) =>
        index == (int)category ? Visibility.Visible : Visibility.Collapsed;

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

    private void MoveUpButton_Click(object sender, RoutedEventArgs args)
    {
        if (TryResolve(sender, out var options, out SystemMonitorMetricOption? option) &&
            options is not null &&
            option is not null)
        {
            SystemMonitorSettingsViewModel.MoveUp(options, option);
        }
    }

    private void MoveDownButton_Click(object sender, RoutedEventArgs args)
    {
        if (TryResolve(sender, out var options, out SystemMonitorMetricOption? option) &&
            options is not null &&
            option is not null)
        {
            SystemMonitorSettingsViewModel.MoveDown(options, option);
        }
    }

    /// <summary>
    /// Resolves the row from the button's DataContext rather than from its Tag alone: the two
    /// lists hold options with the same metric IDs, and the surface on the row itself is the
    /// only thing that says which list the button belongs to.
    /// </summary>
    private bool TryResolve(
        object sender,
        out System.Collections.ObjectModel.ObservableCollection<SystemMonitorMetricOption>?
            options,
        out SystemMonitorMetricOption? option)
    {
        options = null;
        option = null;
        if (sender is not FrameworkElement { DataContext: SystemMonitorMetricOption row })
        {
            return false;
        }

        option = row;
        options = row.Surface == SystemMonitorSurface.Card
            ? SystemMonitorViewModel.CardOptions
            : SystemMonitorViewModel.EntryOptions;
        return true;
    }

    private void SettingsDialog_Closed(
        ContentDialog sender,
        ContentDialogClosedEventArgs args)
    {
        WeatherViewModel.CancelDraft();
        Closed -= SettingsDialog_Closed;
    }
}
