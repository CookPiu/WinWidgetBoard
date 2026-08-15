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

    private void WeatherSettingsDialog_Closed(
        ContentDialog sender,
        ContentDialogClosedEventArgs args)
    {
        ViewModel.CancelDraft();
        Closed -= WeatherSettingsDialog_Closed;
    }
}
