using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.WorkspacePanel;

public sealed partial class AddCardDialog : ContentDialog
{
    public AddCardDialog(AddCardViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public AddCardViewModel ViewModel { get; }

    /// <summary>
    /// True once the user confirmed a selection. The dialog itself changes no layout: the
    /// caller applies the choice through the edit view model so the addition joins the same
    /// undo history as a move or a resize.
    /// </summary>
    public bool WasConfirmed { get; private set; }

    private void ConfirmButton_Click(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.CanAdd)
        {
            return;
        }

        WasConfirmed = true;
        Hide();
    }
}
