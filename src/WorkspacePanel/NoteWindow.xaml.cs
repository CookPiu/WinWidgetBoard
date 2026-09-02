using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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

        // The panel's own material, on the panel's own header. Without this the popped out
        // note was a flat white window with a system caption strip - the same note, but
        // visibly not part of the same board.
        TryConfigureSystemBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(NoteWindowDragRegion);

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

        // The caption buttons are drawn by the system over the extended content. Their
        // width comes from the title bar rather than from a constant: it is stated in
        // physical pixels and changes with the display's scaling, so a hard-coded reserve
        // would put the pin under the minimise button on one monitor and leave a gap on
        // another.
        _appWindow.Changed += AppWindow_Changed;
        NoteWindowRoot.SizeChanged += NoteWindowRoot_SizeChanged;
        UpdateCaptionSpacer();

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

    private void AlwaysOnTopToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is not OverlappedPresenter presenter ||
            sender is not ToggleButton toggle)
        {
            return;
        }

        presenter.IsAlwaysOnTop = toggle.IsChecked == true;
    }

    /// <summary>
    /// Desktop Acrylic, or the default window background when the system cannot provide it.
    /// Matches the panel: a backdrop is a nicety, and failing to get one must not stop a
    /// note from opening.
    /// </summary>
    private void TryConfigureSystemBackdrop()
    {
        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"NoteWindow Desktop Acrylic unavailable: {exception.Message}");
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange)
        {
            UpdateCaptionSpacer();
        }
    }

    private void NoteWindowRoot_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateCaptionSpacer();

    private void UpdateCaptionSpacer()
    {
        if (_closed)
        {
            return;
        }

        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        double scale = dpi == 0 ? 1d : dpi / 96d;
        double inset = _appWindow.TitleBar.RightInset / scale;
        // Before the first layout the title bar reports no inset. Reserving the Windows 11
        // default keeps the pin clear of the caption buttons for that one frame instead of
        // letting it flash underneath them.
        NoteWindowCaptionSpacer.Width = inset > 0 ? inset : 138;
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

    private void NoteWindowTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            NoteEditorKeyboard.TryHandle(_noteEditor, textBox, e);
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
        _appWindow.Changed -= AppWindow_Changed;
        NoteWindowRoot.SizeChanged -= NoteWindowRoot_SizeChanged;
        _noteEditor.PropertyChanged -= NoteEditor_PropertyChanged;
        Closed -= NoteWindow_Closed;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
