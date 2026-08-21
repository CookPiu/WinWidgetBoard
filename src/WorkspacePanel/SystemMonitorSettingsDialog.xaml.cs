using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.WorkspacePanel;

public sealed partial class SystemMonitorSettingsDialog : ContentDialog
{
    public SystemMonitorSettingsDialog(SystemMonitorSettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public SystemMonitorSettingsViewModel ViewModel { get; }

    private async void SaveButton_Click(object sender, RoutedEventArgs args)
    {
        if (await ViewModel.SaveAsync(CancellationToken.None))
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
            ? ViewModel.CardOptions
            : ViewModel.EntryOptions;
        return true;
    }
}
