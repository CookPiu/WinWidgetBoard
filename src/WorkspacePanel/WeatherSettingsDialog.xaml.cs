using Microsoft.UI.Xaml.Controls;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.WorkspacePanel;

public sealed partial class WeatherSettingsDialog : ContentDialog
{
    public WeatherSettingsDialog(WeatherSettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        Closed += WeatherSettingsDialog_Closed;
    }

    public WeatherSettingsViewModel ViewModel { get; }

    private async void WeatherSettingsSaveButton_Click(
        object sender,
        Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        if (await ViewModel.SaveAsync(CancellationToken.None))
        {
            Hide();
        }
    }

    // Search runs on submit, never per keystroke: every search leaves the machine, and a
    // request per character would send far more of what the user typed than they asked to.
    private async void WeatherSettingsSearchBox_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        await ViewModel.SearchAsync(CancellationToken.None);
    }

    private void WeatherSettingsResultsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is ListView { SelectedItem: WeatherLocationOption option })
        {
            ViewModel.SelectSearchResult(option);
        }
    }

    private void WeatherSettingsDialog_Closed(
        ContentDialog sender,
        ContentDialogClosedEventArgs args)
    {
        ViewModel.CancelDraft();
        Closed -= WeatherSettingsDialog_Closed;
    }
}
