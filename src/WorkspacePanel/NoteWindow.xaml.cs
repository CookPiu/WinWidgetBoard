using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using WinWidgetBoard.WorkspacePanel.Notes;

namespace WinWidgetBoard.WorkspacePanel;

/// <summary>
/// The note being edited, in a window of its own.
///
/// It shares the panel's <see cref="NoteEditorViewModel"/> rather than owning a copy: the same
/// note in two places must be one note, and a second editor with its own state would give the
/// user two versions to reconcile and the autosave two writers to race.
///
/// The panel hides itself rather than closing (ADR-0025), and this window is deliberately not
/// tied to that: popping a note out is what you do when you want it to outlast the board being
/// on screen. It closes for real when the user closes it, and the launcher's job object still
/// takes it down with the process.
/// </summary>
public sealed partial class NoteWindow : Window
{
    private readonly NoteEditorViewModel _noteEditor;
    private readonly Func<NoteEditorStatus, string> _statusFormatter;
    private readonly AppWindow _appWindow;
    private bool _closed;

    public NoteWindow(
        NoteEditorViewModel noteEditor,
        Func<NoteEditorStatus, string> statusFormatter,
        string title,
        ElementTheme theme)
    {
        _noteEditor = noteEditor ?? throw new ArgumentNullException(nameof(noteEditor));
        _statusFormatter = statusFormatter ??
            throw new ArgumentNullException(nameof(statusFormatter));
        InitializeComponent();

        Title = title;
        NoteWindowRoot.RequestedTheme = theme;

        IntPtr handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(handle));
        // Resizable, unlike the panel: this one is a working surface the user sizes to the
        // note, not a board with a computed geometry.
        //
        // AppWindow.Resize takes physical pixels, so the logical size has to be scaled or the
        // window comes out half the intended size on a 200% display - which is exactly what
        // it did before this scaling was added.
        uint dpi = GetDpiForWindow(handle);
        double scale = dpi == 0 ? 1d : dpi / 96d;
        _appWindow.Resize(
            new SizeInt32(
                (int)Math.Round(420 * scale),
                (int)Math.Round(460 * scale)));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMinimizable = true;
            presenter.IsMaximizable = true;
        }

        _noteEditor.PropertyChanged += NoteEditor_PropertyChanged;
        Closed += NoteWindow_Closed;
        ApplyStatus();
    }

    public NoteEditorViewModel NoteEditor => _noteEditor;

    /// <summary>Raised once, when the window has closed for good.</summary>
    public event EventHandler? Dismissed;

    public void BringToFront()
    {
        _appWindow.Show();
        Activate();
    }

    private void AlwaysOnTopCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is not OverlappedPresenter presenter ||
            sender is not CheckBox box)
        {
            return;
        }

        presenter.IsAlwaysOnTop = box.IsChecked == true;
    }

    private void NoteWindowTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _noteEditor.Title = textBox.Text;
        }
    }

    private void NoteWindowBodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _noteEditor.Body = textBox.Text;
        }
    }

    private void NoteEditor_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteEditorViewModel.Status)
            or nameof(NoteEditorViewModel.ErrorCode))
        {
            ApplyStatus();
        }
    }

    private void ApplyStatus() =>
        NoteWindowStatusText.Text = _statusFormatter(_noteEditor.Status);

    // Declared here rather than shared with the panel: the panel's copy is a private nested
    // helper, and widening it to reach one call would put a window-styles utility on the
    // panel's public surface for no other reason.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private void NoteWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _noteEditor.PropertyChanged -= NoteEditor_PropertyChanged;
        Closed -= NoteWindow_Closed;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
