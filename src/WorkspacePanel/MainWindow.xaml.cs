using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Interaction;
using WinWidgetBoard.WorkspacePanel.Ipc;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Motion;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;
using WinWidgetBoard.WorkspacePanel.Shell;
using WinWidgetBoard.WorkspacePanel.Settings;
using UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace WinWidgetBoard.WorkspacePanel;

public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExLayered = 0x00080000L;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private static readonly CardSize[] EditableCardSizes = [
        CardSize.S,
        CardSize.M,
        CardSize.L,
        CardSize.W,
        CardSize.XL,
    ];

    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly PanelPlacement _placement;
    private readonly IWeatherSettingsClient? _weatherSettingsClient;
    private readonly ISystemMonitorSettingsClient? _systemMonitorClient;
    private readonly CardSnapshotDispatcher _cardSnapshotDispatcher;
    private int _statusVersion;
    private readonly CardSubscriptionLifecycleCoordinator?
        _cardSubscription;
    private readonly ResourceLoader _resources = new();
    private readonly CardDragController _demoNotesCardDrag = new();
    private readonly PanelMotionCoordinator _motion;
    private readonly UiDispatcherQueue _uiDispatcherQueue;
    private readonly CardGridLayout _cardGridLayout;
    private readonly CardLayoutViewModel _cardLayout;
    private readonly LayoutPersistenceCoordinator? _layoutPersistence;
    private readonly NoteListCoordinator _noteList;
    private readonly CardLayoutEditViewModel _cardEdit;
    private readonly CardLayoutSurfaceViewModel _cardSurface;
    private readonly SurfaceMotionCoordinator _surfaceMotion;
    private readonly bool _reducedMotion;
    private readonly bool _highContrast;
    private readonly Dictionary<UIElement, CardRuntimeInstance>
        _realizedCardRuntimes = [];
    private readonly bool _keepOpenForAcceptance;
    private bool _cardItemsBound;
    private bool _nativeOpacitySupported;
    private int _modalScopeDepth;
    private bool _deferredCloseRequest;
    private bool _isHiddenForResidency;
    private bool _hasBeenActivated;
    private bool _allowNativeClose;
    private uint? _demoNotesCardPointerId;
    private CompositeTransform? _demoNotesCardTransform;
    private FrameworkElement? _draggedCardSurface;
    private string? _draggedCardId;
    private CardPlacement? _dragStartPlacement;
    private bool _isSavingLayout;
    private bool _suppressNoteSearchTextChanged;
    private int _disposed;

    public MainWindow(
        INoteClient? noteClient = null,
        ILayoutClient? layoutClient = null,
        bool keepOpenForAcceptance = false,
        CoreBrokerCardsClient? cardsClient = null,
        IWeatherSettingsClient? weatherSettingsClient = null,
        ISystemMonitorSettingsClient? systemMonitorClient = null)
    {
        _weatherSettingsClient = weatherSettingsClient;
        _systemMonitorClient = systemMonitorClient;
        _keepOpenForAcceptance = keepOpenForAcceptance;
        _uiDispatcherQueue = UiDispatcherQueue.GetForCurrentThread();
        _cardSnapshotDispatcher = new CardSnapshotDispatcher(
            DispatchSnapshotToUiAsync);
        _cardSubscription = cardsClient is null
            ? null
            : new CardSubscriptionLifecycleCoordinator(
                cardsClient,
                _cardSnapshotDispatcher,
                _uiDispatcherQueue,
                CaptureCardSubscriptionStateAsync);
        NoteEditor = new NoteEditorViewModel(
            noteClient,
            dispatch: DispatchToUi);
        NoteSearch = new NoteSearchViewModel(
            noteClient,
            dispatch: DispatchToUi);
        _noteList = new NoteListCoordinator(NoteEditor, NoteSearch);
        StartupTrace.Mark("mainwindow-ctor-before-xaml");
        InitializeComponent();
        StartupTrace.Mark("mainwindow-xaml-inflated");
        _reducedMotion = !new UISettings().AnimationsEnabled;
        _highContrast = new AccessibilitySettings().HighContrast;
        _surfaceMotion = new SurfaceMotionCoordinator(
            _reducedMotion,
            _highContrast);
        StartupTrace.Mark("ctor-settings-read");
        TryConfigureSystemBackdrop();
        StartupTrace.Mark("ctor-backdrop");
        _cardLayout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem(
                    BuiltInCardCatalog.NotesInstanceId,
                    CardSize.L),
                new CardLayoutItem(
                    BuiltInCardCatalog.WeatherInstanceId,
                    CardSize.M),
                new CardLayoutItem(
                    BuiltInCardCatalog.TimerInstanceId,
                    CardSize.M),
                new CardLayoutItem(
                    BuiltInCardCatalog.TodoInstanceId,
                    CardSize.M),
                new CardLayoutItem(
                    BuiltInCardCatalog.CalendarInstanceId,
                    CardSize.M),
                new CardLayoutItem(
                    BuiltInCardCatalog.SystemMonitorInstanceId,
                    CardSize.L),
            ]);
        _cardEdit = new CardLayoutEditViewModel(_cardLayout);
        _cardSurface = new CardLayoutSurfaceViewModel(
            _cardEdit,
            NoteEditor,
            FormatNoteStatus,
            runtimeResourceResolver: key => _resources.GetString(
                key.Replace('.', '/')),
            uiInvoker: action =>
            {
                if (DispatcherQueue.HasThreadAccess)
                {
                    action();
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() => action());
                }
            });
        _cardGridLayout = new CardGridLayout
        {
            ColumnCount = _cardLayout.ColumnCount,
        };
        _layoutPersistence = layoutClient is null
            ? null
            : new LayoutPersistenceCoordinator(
                layoutClient,
                _cardLayout);
        StartupTrace.Mark("ctor-viewmodels");
        CardItemsRepeater.Layout = _cardGridLayout;
        _cardLayout.PropertyChanged += CardLayout_PropertyChanged;
        _cardSurface.PropertyChanged += CardSurface_PropertyChanged;
        _cardEdit.PropertyChanged += CardEdit_PropertyChanged;
        NoteEditor.PropertyChanged += NoteEditor_PropertyChanged;

        _windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        Title = _resources.GetString("WindowTitle");
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;

        ConfigureToolWindow();
        PanelLaunchContext context = ResolveLaunchContext(windowId);
        _placement = PanelGeometry.Calculate(context);
        if (!_placement.IsValid)
        {
            throw new InvalidOperationException(
                $"WorkspacePanel geometry rejected: {_placement.Reason}");
        }

        Windows.Foundation.Point transformOrigin = CalculateTransformOrigin(
            context,
            _placement);
        RootGrid.RenderTransformOrigin = transformOrigin;
        _motion = new PanelMotionCoordinator(
            transformOrigin.X < 0.5 ? -10 : 10,
            transformOrigin.Y < 0.5 ? -10 : 10,
            _reducedMotion,
            _uiDispatcherQueue,
            ApplyPanelMotion,
            ApplyDemoNotesCardReturn,
            () => _demoNotesCardDrag.IsActive,
            offset => _demoNotesCardDrag.SetOffset(offset),
            CloseAfterMotion);
        _appWindow.Closing += AppWindow_Closing;
        // SetForegroundWindow from the launcher does not reliably raise Activated, so the
        // re-show hook hangs off the window's own visibility change instead.
        _appWindow.Changed += AppWindow_Changed;

        ApplyPlacement();
        ApplyPanelMotion(_motion.PanelValue);
        DateText.Text = DateTime.Now.ToString("D", CultureInfo.CurrentCulture);
        ContextText.Text =
            $"{_placement.WindowRect.Width} × {_placement.WindowRect.Height} px · " +
            $"DPI {_placement.Dpi}";
        StartupTrace.Mark("ctor-wiring");
        ConfigureHeaderToolTips();
        UpdateEditLayoutButton();

        StartupTrace.Mark("ctor-tooltips");
        SynchronizeCardSnapshotRuntimes();
        StartupTrace.Mark("mainwindow-ctor-done");
    }

    public NoteEditorViewModel NoteEditor { get; }

    public NoteSearchViewModel NoteSearch { get; }

    public CardLayoutViewModel CardLayout => _cardLayout;

    public CardLayoutEditViewModel CardEdit => _cardEdit;

    public CardLayoutSurfaceViewModel CardSurface => _cardSurface;

    public void FocusInitialElement()
    {
        RootGrid.Focus(FocusState.Programmatic);
    }

    public void BeginOpeningMotion()
    {
        RequestOpenMotion();
    }

    public IDisposable EnterModalScope()
    {
        _modalScopeDepth++;
        return new ModalScope(this);
    }

    private void ConfigureToolWindow()
    {
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        NativeWindowStyles.MakeToolWindow(_windowHandle);
        NativeWindowStyles.PreferRoundedCorners(_windowHandle);
        _nativeOpacitySupported = NativeWindowStyles.EnableLayeredOpacity(_windowHandle);
    }

    private void TryConfigureSystemBackdrop()
    {
        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch (Exception exception)
            when (exception is COMException or NotSupportedException)
        {
            Debug.WriteLine(
                $"WorkspacePanel Desktop Acrylic unavailable: {exception.Message}");
            RootGrid.Background =
                Application.Current.Resources[
                    "ApplicationPageBackgroundThemeBrush"] as Brush;
        }
    }

    private PanelLaunchContext ResolveLaunchContext(WindowId windowId)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (!PanelLaunchContext.TryParse(arguments, out PanelLaunchContext? parsed, out string? error))
        {
            throw new InvalidOperationException(error ?? "invalid panel launch context");
        }

        if (parsed is not null)
        {
            return parsed;
        }

        DisplayArea displayArea = DisplayArea.GetFromWindowId(
            windowId,
            DisplayAreaFallback.Primary);
        uint dpi = NativeWindowStyles.GetDpiForWindow(_windowHandle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        return new PanelLaunchContext(
            ToScreenRect(displayArea.OuterBounds),
            ToScreenRect(displayArea.WorkArea),
            default,
            dpi);
    }

    private void ApplyPlacement()
    {
        _appWindow.Move(new Windows.Graphics.PointInt32(
            _placement.WindowRect.Left,
            _placement.WindowRect.Top));
        _appWindow.Resize(new Windows.Graphics.SizeInt32(
            _placement.WindowRect.Width,
            _placement.WindowRect.Height));
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (PanelActivationClosePolicy.ShouldRequestClose(
                    _keepOpenForAcceptance,
                    _hasBeenActivated,
                    _modalScopeDepth,
                    isDeactivated: true))
            {
                RequestCloseMotion();
            }

            return;
        }

        _hasBeenActivated = true;
        if (_isHiddenForResidency)
        {
            ShowAfterResidency();
            return;
        }

        if (_motion.IsClosing)
        {
            RequestOpenMotion();
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidVisibilityChange || !sender.IsVisible)
        {
            return;
        }

        ShowAfterResidency();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _motion.Stop();
        _cardSubscription?.OnWindowClosed();
        _motion.Dispose();
        _surfaceMotion.Dispose();
        _cardSurface.SetPanelVisibility(false);
        _realizedCardRuntimes.Clear();
        _appWindow.Closing -= AppWindow_Closing;
        _appWindow.Changed -= AppWindow_Changed;
        Activated -= MainWindow_Activated;
        Closed -= MainWindow_Closed;
        CardItemsRepeater.ElementPrepared -= CardItemsRepeater_ElementPrepared;
        CardItemsRepeater.ElementClearing -= CardItemsRepeater_ElementClearing;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _motion.Dispose();
        _surfaceMotion.Dispose();
        _cardSubscription?.OnWindowClosed();
        _cardSurface.SetPanelVisibility(false);
        _realizedCardRuntimes.Clear();
        _cardLayout.PropertyChanged -= CardLayout_PropertyChanged;
        _cardSurface.PropertyChanged -= CardSurface_PropertyChanged;
        _cardEdit.PropertyChanged -= CardEdit_PropertyChanged;
        NoteEditor.PropertyChanged -= NoteEditor_PropertyChanged;
        CardItemsRepeater.ElementPrepared -= CardItemsRepeater_ElementPrepared;
        CardItemsRepeater.ElementClearing -= CardItemsRepeater_ElementClearing;
        if (_cardSubscription is not null)
        {
            await _cardSubscription.DisposeAsync();
        }

        await _cardSurface.DisposeAsync();
        await NoteEditor.DisposeAsync();
        await NoteSearch.DisposeAsync();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowNativeClose)
        {
            _allowNativeClose = false;
            return;
        }

        args.Cancel = true;
        RequestCloseMotion();
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_cardEdit.IsEditing &&
            !_isSavingLayout &&
            IsControlPressed() &&
            e.Key is VirtualKey.Z or VirtualKey.Y)
        {
            bool changed = e.Key == VirtualKey.Z
                ? TryUndoLayout()
                : TryRedoLayout();
            if (changed)
            {
                e.Handled = true;
            }

            return;
        }

        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        if (_demoNotesCardDrag.IsActive)
        {
            CancelDemoNotesCardDrag();
            return;
        }

        if (_cardEdit.IsEditing)
        {
            CancelLayoutEdit();
            return;
        }

        if (_modalScopeDepth == 0)
        {
            RequestCloseMotion();
        }
    }

    private async void SysMonSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _statusVersion);
        try
        {
            var viewModel = new SystemMonitorSettingsViewModel(
                _systemMonitorClient,
                key => _resources.GetString(key.Replace('.', '/')));
            if (RootGrid.XamlRoot is null)
            {
                return;
            }

            var dialog = new SystemMonitorSettingsDialog(viewModel)
            {
                XamlRoot = RootGrid.XamlRoot,
            };
            await viewModel.LoadAsync(CancellationToken.None);
            using IDisposable modalScope = EnterModalScope();
            await dialog.ShowAsync();
            Interlocked.Increment(ref _statusVersion);
            StatusText.Text = viewModel.StatusText;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException and
                not StackOverflowException)
        {
            Interlocked.Increment(ref _statusVersion);
            StatusText.Text = _resources.GetString("SysMonSettingsLoadFailedStatus");
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _statusVersion);
        try
        {
            var viewModel = new WeatherSettingsViewModel(
                _weatherSettingsClient,
                key => _resources.GetString(key));
            if (RootGrid.XamlRoot is null)
            {
                return;
            }

            var dialog = new WeatherSettingsDialog(viewModel)
            {
                XamlRoot = RootGrid.XamlRoot,
            };
            await viewModel.LoadAsync(CancellationToken.None);
            using IDisposable modalScope = EnterModalScope();
            await dialog.ShowAsync();
            Interlocked.Increment(ref _statusVersion);
            StatusText.Text = viewModel.WasSaved
                ? _resources.GetString("WeatherSettingsSavedStatus")
                : viewModel.StatusText;
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException and
                not StackOverflowException and
                not AccessViolationException)
        {
            Debug.WriteLine($"WorkspacePanel weather settings dialog failed: {exception}");
            StatusText.Text = _resources.GetString("WeatherSettingsLoadFailedStatus");
        }
    }

    private async void EditLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_cardEdit.IsEditing)
        {
            ClearDemoNotesCardVisual();
            _cardEdit.BeginEdit();
            StatusText.Text = _resources.GetString("LayoutEditStartedStatus");
            UpdateEditLayoutButton();
            return;
        }

        await RunOnUiAsync(() =>
        {
            EditLayoutButton.IsEnabled = false;
            _isSavingLayout = true;
            StatusText.Text = _resources.GetString("LayoutSavingStatus");
            UpdateLayoutHistoryButtons();
        });
        try
        {
            if (!await SaveLayoutAsync(CancellationToken.None))
            {
                return;
            }

            await RunOnUiAsync(() =>
            {
                ClearDemoNotesCardVisual();
                _cardEdit.CommitEdit();
                StatusText.Text = _resources.GetString("LayoutEditCommittedStatus");
                UpdateEditLayoutButton();
            });
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                _isSavingLayout = false;
                EditLayoutButton.IsEnabled = true;
                UpdateEditLayoutButton();
                UpdateLayoutHistoryButtons();
            });
        }
    }

    private async void AddCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSavingLayout || !_cardEdit.IsEditing || RootGrid.XamlRoot is null)
        {
            return;
        }

        Interlocked.Increment(ref _statusVersion);
        try
        {
            var viewModel = new AddCardViewModel(
                _cardLayout.Items.Select(item => item.InstanceId),
                key => _resources.GetString(key.Replace('.', '/')));
            if (!viewModel.HasOptions)
            {
                StatusText.Text = _resources.GetString("AddCardNoneAvailableStatus");
                return;
            }

            var dialog = new AddCardDialog(viewModel)
            {
                XamlRoot = RootGrid.XamlRoot,
            };
            using (IDisposable modalScope = EnterModalScope())
            {
                await dialog.ShowAsync();
            }

            Interlocked.Increment(ref _statusVersion);
            if (!dialog.WasConfirmed)
            {
                return;
            }

            int added = 0;
            foreach (AddCardOption option in viewModel.Selected)
            {
                if (_cardEdit.TryAddCard(option.InstanceId, option.DefaultSize))
                {
                    added++;
                }
            }

            StatusText.Text = added > 0
                ? _resources.GetString("CardAddedStatus")
                : _resources.GetString("AddCardNoneAvailableStatus");
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException and
                not StackOverflowException and
                not AccessViolationException)
        {
            Debug.WriteLine($"WorkspacePanel add card dialog failed: {exception}");
            Interlocked.Increment(ref _statusVersion);
            StatusText.Text = _resources.GetString("AddCardFailedStatus");
        }
    }

    private void UndoLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TryUndoLayout();
    }

    private void RedoLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TryRedoLayout();
    }

    private void CancelLayoutEdit()
    {
        _cardEdit.CancelEdit();
        ClearDemoNotesCardVisual();
        UpdateEditLayoutButton();
        StatusText.Text = _resources.GetString("LayoutEditCanceledStatus");
    }

    private void UpdateEditLayoutButton()
    {
        string content = _cardEdit.IsEditing
            ? _resources.GetString("FinishEditLayoutButtonContent")
            : _resources.GetString("EditLayoutButton/Content");
        string automationName = _cardEdit.IsEditing
            ? _resources.GetString("FinishEditLayoutButtonAutomationName")
            : _resources.GetString("EditLayoutButtonAutomationName");
        EditLayoutButton.Content = content;
        AutomationProperties.SetName(EditLayoutButton, automationName);
        ToolTipService.SetToolTip(EditLayoutButton, automationName);
        UpdateLayoutHistoryButtons();
    }

    private void ConfigureHeaderToolTips()
    {
        ToolTipService.SetToolTip(
            ListNotesButton,
            _resources.GetString("ListNotesButtonToolTip"));
        ToolTipService.SetToolTip(
            AddCardButton,
            _resources.GetString("AddCardButtonToolTip"));
        ToolTipService.SetToolTip(
            UndoLayoutButton,
            _resources.GetString("UndoLayoutButtonToolTip"));
        ToolTipService.SetToolTip(
            RedoLayoutButton,
            _resources.GetString("RedoLayoutButtonToolTip"));
        ToolTipService.SetToolTip(
            SettingsButton,
            _resources.GetString("SettingsButtonToolTip"));
        ToolTipService.SetToolTip(
            ClosePanelButton,
            _resources.GetString("ClosePanelButtonToolTip"));
    }

    private void CardEdit_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CardLayoutEditViewModel.IsEditing)
            or nameof(CardLayoutEditViewModel.CanUndo)
            or nameof(CardLayoutEditViewModel.CanRedo))
        {
            UpdateLayoutHistoryButtons();
        }
    }

    private void UpdateLayoutHistoryButtons()
    {
        Visibility visibility = _cardEdit.IsEditing
            ? Visibility.Visible
            : Visibility.Collapsed;
        AddCardButton.Visibility = visibility;
        UndoLayoutButton.Visibility = visibility;
        RedoLayoutButton.Visibility = visibility;
        AddCardButton.IsEnabled = !_isSavingLayout;
        UndoLayoutButton.IsEnabled = !_isSavingLayout && _cardEdit.CanUndo;
        RedoLayoutButton.IsEnabled = !_isSavingLayout && _cardEdit.CanRedo;
    }

    private bool TryUndoLayout()
    {
        if (!_cardEdit.TryUndo())
        {
            return false;
        }

        ClearDemoNotesCardVisual();
        ResetCardDropPreview();
        StatusText.Text = _resources.GetString("LayoutUndoStatus");
        return true;
    }

    private bool TryRedoLayout()
    {
        if (!_cardEdit.TryRedo())
        {
            return false;
        }

        ClearDemoNotesCardVisual();
        ResetCardDropPreview();
        StatusText.Text = _resources.GetString("LayoutRedoStatus");
        return true;
    }

    private void DecreaseCardSizeButton_Click(object sender, RoutedEventArgs e)
    {
        ResizeCard(sender, -1);
    }

    private void IncreaseCardSizeButton_Click(object sender, RoutedEventArgs e)
    {
        ResizeCard(sender, 1);
    }

    private void ResizeCard(object sender, int direction)
    {
        if (_isSavingLayout ||
            ResolveCurrentCardSurfaceItem(sender) is not CardSurfaceItem item ||
            !_cardEdit.TryStepCardSize(
                item.InstanceId,
                direction,
                EditableCardSizes))
        {
            return;
        }

        StatusText.Text = _resources.GetString("CardResizedStatus");
    }

    private CardSurfaceItem? ResolveCurrentCardSurfaceItem(object sender)
    {
        if (sender is not DependencyObject current)
        {
            return null;
        }

        while (current is not null &&
            !ReferenceEquals(current, CardItemsRepeater))
        {
            if (current is UIElement element)
            {
                int index = CardItemsRepeater.GetElementIndex(element);
                if (index >= 0)
                {
                    return _cardSurface.GetItemAt(index);
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void RemoveCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSavingLayout &&
            sender is FrameworkElement element &&
            element.Tag is string instanceId &&
            _cardEdit.TryRemoveCard(instanceId))
        {
            StatusText.Text = _resources.GetString("CardRemovedStatus");
        }
    }

    private void NoteMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                Tag: FrameworkElement overflowPanel,
            } button)
        {
            return;
        }

        bool isExpanded = !_surfaceMotion.IsVisible(overflowPanel);
        if (isExpanded)
        {
            _surfaceMotion.Show(
                overflowPanel,
                SurfaceMotionAnchor.TopRight,
                floating: true);
        }
        else
        {
            _surfaceMotion.Hide(
                overflowPanel,
                SurfaceMotionAnchor.TopRight);
        }

        UpdateNoteMoreButtonState(button, isExpanded);
    }

    private void CollapseNoteOverflow(object sender)
    {
        if (sender is not FrameworkElement
            {
                Tag: FrameworkElement overflowPanel,
            })
        {
            return;
        }

        _surfaceMotion.Hide(
            overflowPanel,
            SurfaceMotionAnchor.TopRight);
        if (overflowPanel.Tag is Button moreButton)
        {
            UpdateNoteMoreButtonState(moreButton, isExpanded: false);
        }
    }

    private void UpdateNoteMoreButtonState(Button button, bool isExpanded)
    {
        string resourceKey = isExpanded
            ? "NoteMoreCloseAutomationName"
            : "NoteMoreOpenAutomationName";
        string automationName = _resources.GetString(resourceKey);
        AutomationProperties.SetName(button, automationName);
        ToolTipService.SetToolTip(button, automationName);
    }

    private void NoteEditor_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName ==
            nameof(NoteEditorViewModel.IsMarkdownPreviewVisible))
        {
            ApplyNotePreviewState();
        }
    }

    private void ApplyNotePreviewState()
    {
        bool isPreviewVisible = NoteEditor.IsMarkdownPreviewVisible;
        StackPanel? inputPanel = FindDescendantByName<StackPanel>(
            RootGrid,
            "NoteEditorInputPanel");
        Border? previewPanel = FindDescendantByName<Border>(
            RootGrid,
            "NoteMarkdownPreviewPanel");
        if (isPreviewVisible)
        {
            if (inputPanel is not null)
            {
                _surfaceMotion.HideImmediately(inputPanel);
            }

            if (previewPanel is not null)
            {
                _surfaceMotion.Show(
                    previewPanel,
                    SurfaceMotionAnchor.Center);
            }
        }
        else
        {
            if (previewPanel is not null)
            {
                _surfaceMotion.HideImmediately(previewPanel);
            }

            if (inputPanel is not null)
            {
                _surfaceMotion.Show(
                    inputPanel,
                    SurfaceMotionAnchor.Center);
            }
        }

        SetVisibility(
            FindDescendantByName<Button>(
                RootGrid,
                "PreviewNoteButton"),
            !isPreviewVisible);
        SetVisibility(
            FindDescendantByName<Button>(
                RootGrid,
                "EditMarkdownButton"),
            isPreviewVisible);
    }

    private static void SetVisibility(
        FrameworkElement? element,
        bool isVisible)
    {
        if (element is not null)
        {
            element.Visibility = isVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void SetSearchResultsVisible(
        bool isVisible,
        bool immediate = false)
    {
        if (isVisible)
        {
            _surfaceMotion.Show(
                NoteSearchResultsBorder,
                SurfaceMotionAnchor.Top,
                floating: true);
            return;
        }

        if (immediate)
        {
            _surfaceMotion.HideImmediately(NoteSearchResultsBorder);
            return;
        }

        _surfaceMotion.Hide(
            NoteSearchResultsBorder,
            SurfaceMotionAnchor.Top);
    }

    private static T? FindDescendantByName<T>(
        DependencyObject root,
        string name)
        where T : FrameworkElement
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T element &&
                string.Equals(
                    element.Name,
                    name,
                    StringComparison.Ordinal))
            {
                return element;
            }

            T? descendant = FindDescendantByName<T>(child, name);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private async void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseNoteOverflow(sender);
        if (!NoteEditor.CanLoadNote)
        {
            StatusText.Text = _resources.GetString("NoteCreateBlockedStatus");
            return;
        }

        SetSearchResultsVisible(false);
        StatusText.Text = _resources.GetString("NoteCreatingStatus");
        bool created = await NoteEditor.CreateNoteAsync(CancellationToken.None);
        StatusText.Text = _resources.GetString(
            created ? "NoteCreatedStatus" : "NoteCreateFailedStatus");
    }

    private async void DeleteCurrentNoteButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CollapseNoteOverflow(sender);
        if (!NoteEditor.CanDelete)
        {
            StatusText.Text = _resources.GetString("NoteDeleteBlockedStatus");
            return;
        }

        string title = GetNoteDisplayTitle(NoteEditor.Title, NoteEditor.NoteId);
        if (!await ConfirmNoteDeletionAsync(title))
        {
            return;
        }

        StatusText.Text = _resources.GetString("NoteDeletingStatus");
        bool deleted = await NoteEditor.DeleteCurrentNoteAsync(
            CancellationToken.None);
        if (!deleted)
        {
            StatusText.Text = _resources.GetString("NoteDeleteFailedStatus");
            return;
        }

        bool refreshed = await RefreshNoteResultsAfterDeleteAsync();
        if (refreshed)
        {
            await LoadFirstListedNoteAsync();
        }

        StatusText.Text = _resources.GetString(
            refreshed ? "NoteDeletedStatus" : "NoteDeleteRefreshFailedStatus");
    }

    private async void DeleteNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not NoteSearchResult result)
        {
            return;
        }

        if (!NoteEditor.CanLoadNote)
        {
            StatusText.Text = _resources.GetString("NoteDeleteBlockedStatus");
            return;
        }

        button.IsEnabled = false;
        try
        {
            string title = GetNoteDisplayTitle(result.Title, result.NoteId);
            if (!await ConfirmNoteDeletionAsync(title))
            {
                return;
            }

            bool deletingCurrent = string.Equals(
                NoteEditor.NoteId,
                result.NoteId,
                StringComparison.Ordinal);
            StatusText.Text = _resources.GetString("NoteDeletingStatus");
            bool deleted = deletingCurrent
                ? await NoteEditor.DeleteCurrentNoteAsync(CancellationToken.None)
                : await NoteSearch.DeleteNoteAsync(result, CancellationToken.None);
            if (!deleted)
            {
                StatusText.Text = _resources.GetString("NoteDeleteFailedStatus");
                return;
            }

            bool refreshed = await RefreshNoteResultsAfterDeleteAsync();
            if (deletingCurrent && refreshed)
            {
                await LoadFirstListedNoteAsync();
            }

            StatusText.Text = _resources.GetString(
                refreshed ? "NoteDeletedStatus" : "NoteDeleteRefreshFailedStatus");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void CopyNoteButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseNoteOverflow(sender);
        string content = NoteClipboardFormatter.Format(
            NoteEditor.Title,
            NoteEditor.Body);
        if (content.Length == 0)
        {
            StatusText.Text = _resources.GetString("NoteCopyEmptyStatus");
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(content);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            StatusText.Text = _resources.GetString("NoteCopiedStatus");
        }
        catch (Exception exception)
            when (exception is COMException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            Debug.WriteLine($"WorkspacePanel note copy failed: {exception.Message}");
            StatusText.Text = _resources.GetString("NoteCopyFailedStatus");
        }
    }

    private async void ListNotesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NoteSearch.CanSearch)
        {
            return;
        }

        _suppressNoteSearchTextChanged = true;
        try
        {
            SearchBox.Text = string.Empty;
        }
        finally
        {
            _suppressNoteSearchTextChanged = false;
        }

        SetSearchResultsVisible(false, immediate: true);
        StatusText.Text = _resources.GetString("NoteListLoadingStatus");
        NoteListOperationResult result = await _noteList.LoadAllAsync(
            CancellationToken.None);
        if (!result.IsCurrentQuery)
        {
            return;
        }

        SetSearchResultsVisible(result.HasResults);
        StatusText.Text = result.Status switch
        {
            NoteSearchStatus.Ready => string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("NoteListResultsStatus"),
                result.ResultCount),
            NoteSearchStatus.Empty => _resources.GetString("NoteListEmptyStatus"),
            NoteSearchStatus.Error => _resources.GetString("NoteListFailedStatus"),
            _ => StatusText.Text,
        };
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressNoteSearchTextChanged || sender is not TextBox textBox)
        {
            return;
        }

        string query = textBox.Text.Trim();
        if (query.Length == 0)
        {
            await _noteList.SearchAsync(query, CancellationToken.None);
            SetSearchResultsVisible(false);
            StatusText.Text = _resources.GetString("NoteSearchClearedStatus");
            return;
        }

        SetSearchResultsVisible(false, immediate: true);
        StatusText.Text = _resources.GetString("NoteSearchSearchingStatus");
        NoteListOperationResult result = await _noteList.SearchAsync(
            query,
            CancellationToken.None);
        if (!result.IsCurrentQuery)
        {
            return;
        }

        SetSearchResultsVisible(result.HasResults);
        StatusText.Text = result.Status switch
        {
            NoteSearchStatus.Ready => string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("NoteSearchResultsStatus"),
                result.ResultCount),
            NoteSearchStatus.Empty => _resources.GetString("NoteSearchNoResultsStatus"),
            NoteSearchStatus.Error => _resources.GetString("NoteSearchFailedStatus"),
            _ => StatusText.Text,
        };
    }

    private async void NoteSearchResultButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string noteId })
        {
            return;
        }

        if (!NoteEditor.CanLoadNote)
        {
            StatusText.Text = _resources.GetString("NoteLoadBlockedStatus");
            return;
        }

        bool loaded = await _noteList.LoadNoteAsync(
            noteId,
            CancellationToken.None);
        StatusText.Text = _resources.GetString(
            loaded ? "NoteLoadedStatus" : "NoteLoadFailedStatus");
    }

    private async Task<bool> RefreshNoteResultsAfterDeleteAsync()
    {
        NoteListOperationResult result = await _noteList
            .RefreshAfterDeleteAsync(CancellationToken.None);
        if (!result.IsCurrentQuery)
        {
            return false;
        }

        SetSearchResultsVisible(result.HasResults);
        return result.Status is NoteSearchStatus.Ready or NoteSearchStatus.Empty;
    }

    private async Task LoadFirstListedNoteAsync()
    {
        await _noteList.LoadFirstListedNoteAsync(CancellationToken.None);
    }

    private async Task<bool> ConfirmNoteDeletionAsync(string title)
    {
        if (RootGrid.XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = _resources.GetString("NoteDeleteDialogTitle"),
            Content = string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("NoteDeleteDialogContent"),
                title),
            PrimaryButtonText = _resources.GetString("NoteDeleteDialogDeleteButton"),
            CloseButtonText = _resources.GetString("NoteDeleteDialogCancelButton"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };

        using IDisposable modalScope = EnterModalScope();
        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async void RetryNoteSaveButton_Click(object sender, RoutedEventArgs e)
    {
        bool saved = await NoteEditor.RetrySaveAsync(CancellationToken.None);
        StatusText.Text = _resources.GetString(
            saved ? "NoteSaveRetrySucceededStatus" : "NoteSaveRetryFailedStatus");
    }

    private void UndoNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (NoteEditor.Undo())
        {
            StatusText.Text = _resources.GetString("NoteUndoStatus");
        }
    }

    private void RedoNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (NoteEditor.Redo())
        {
            StatusText.Text = _resources.GetString("NoteRedoStatus");
        }
    }

    private void MarkdownModeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox)
        {
            return;
        }

        CollapseNoteOverflow(sender);
        bool enabled = checkBox.IsChecked == true;
        bool changed = NoteEditor.SetMarkdownMode(enabled);
        checkBox.IsChecked = NoteEditor.IsMarkdown;
        StatusText.Text = _resources.GetString(
            changed
                ? enabled
                    ? "NoteMarkdownEnabledStatus"
                    : "NotePlainTextEnabledStatus"
                : "NoteMarkdownModeBlockedStatus");
    }

    private void PreviewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseNoteOverflow(sender);
        if (!NoteEditor.CanPreviewMarkdown)
        {
            StatusText.Text = _resources.GetString("NoteMarkdownModeBlockedStatus");
            return;
        }

        NoteEditor.ToggleMarkdownPreview();
        StatusText.Text = _resources.GetString("NoteMarkdownPreviewStatus");
    }

    private void EditMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseNoteOverflow(sender);
        if (!NoteEditor.CanEdit)
        {
            return;
        }

        NoteEditor.ToggleMarkdownPreview();
        StatusText.Text = _resources.GetString("NoteMarkdownEditStatus");
    }

    private void NoteTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            NoteEditor.Title = textBox.Text;
        }
    }

    private void NoteBodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            NoteEditor.Body = textBox.Text;
        }
    }

    public Task<bool> InitializeNoteEditorAsync(CancellationToken cancellationToken) =>
        NoteEditor.LoadAsync(cancellationToken);

    public async Task<bool> InitializeCardSubscriptionAsync(
        CancellationToken cancellationToken)
    {
        return _cardSubscription is not null &&
            await _cardSubscription.InitializeAsync(cancellationToken)
                .ConfigureAwait(false);
    }

    public void HandleBrokerReconnected(object? sender, EventArgs args)
    {
        _cardSubscription?.HandleBrokerReconnected(sender, args);
    }

    private async Task<CardSubscriptionState> CaptureCardSubscriptionStateAsync(
        CancellationToken cancellationToken)
    {
        CardSubscriptionState? state = null;
        await RunOnUiAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            SynchronizeCardSnapshotRuntimes();
            CardRuntimeInstance[] runtimes = _cardSurface.Items
                .Select(item => item.Runtime)
                .ToArray();
            string[] visibleInstanceIds = _realizedCardRuntimes.Values
                .Select(runtime => runtime.InstanceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            state = new CardSubscriptionState(
                runtimes,
                _cardSurface.VisibilityScheduler.PanelVisible,
                visibleInstanceIds);
        }).ConfigureAwait(false);

        return state ?? throw new InvalidOperationException(
            "WorkspacePanel card subscription state was not captured.");
    }

    private void SynchronizeCardSnapshotRuntimes()
    {
        if (_cardSubscription is null ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _cardSubscription.SynchronizeRuntimes(
            _cardSurface.Items
                .Select(item => item.Runtime)
                .ToArray());
    }

    private void RequestCardSubscriptionRefresh()
    {
        _cardSubscription?.RequestRefresh();
    }

    private ValueTask<CardSnapshotDispatchResult> DispatchSnapshotToUiAsync(
        Func<CardSnapshotDispatchResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_uiDispatcherQueue.HasThreadAccess)
        {
            return ValueTask.FromResult(action());
        }

        var completion = new TaskCompletionSource<CardSnapshotDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_uiDispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            completion.SetException(
                new InvalidOperationException(
                    "The WorkspacePanel UI dispatcher is unavailable."));
        }

        return new ValueTask<CardSnapshotDispatchResult>(completion.Task);
    }

    public void MarkNoteEditorUnavailable(string errorCode = "transport.unavailable") =>
        NoteEditor.MarkUnavailable(errorCode);

    public async Task<bool> InitializeLayoutAsync(CancellationToken cancellationToken)
    {
        int statusVersion = Volatile.Read(ref _statusVersion);
        if (_layoutPersistence is null)
        {
            MarkLayoutUnavailable();
            return false;
        }

        try
        {
            LayoutLoadResult? loaded = await _layoutPersistence
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);

            await RunOnUiAsync(() =>
            {
                if (loaded is not null)
                {
                    _cardLayout.ReplaceItems(loaded.Items);
                }
                EnsureCardItemsBound();

                if (Volatile.Read(ref _statusVersion) == statusVersion)
                {
                    StatusText.Text = _resources.GetString(
                        loaded is null
                            ? "LayoutReadyToSaveStatus"
                            : loaded.RecoveredItemCount > 0
                                ? "LayoutRecoveredStatus"
                                : "LayoutLoadedStatus");
                }

                StartupTrace.Mark("layout-ready");

                if (loaded?.RecoveredItemCount > 0)
                {
                    Debug.WriteLine(
                        $"Recovered {loaded.RecoveredItemCount} layout item(s) " +
                        "from excessive persisted row gaps.");
                }
            }).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is CoreBrokerClientException or IOException or TimeoutException or InvalidDataException)
        {
            Debug.WriteLine($"WorkspacePanel layout load failed: {exception.Message}");
            await RunOnUiAsync(MarkLayoutUnavailable).ConfigureAwait(false);
            return false;
        }
    }

    public void MarkLayoutUnavailable()
    {
        if (_uiDispatcherQueue.HasThreadAccess)
        {
            EnsureCardItemsBound();
            StatusText.Text = _resources.GetString("LayoutUnavailableStatus");
        }
        else
        {
            _ = _uiDispatcherQueue.TryEnqueue(MarkLayoutUnavailable);
        }
    }

    private async Task<bool> SaveLayoutAsync(CancellationToken cancellationToken)
    {
        if (_layoutPersistence is null)
        {
            MarkLayoutUnavailable();
            return false;
        }

        LayoutSaveRequest request = _layoutPersistence.CreateSaveRequest();

        try
        {
            await _layoutPersistence
                .SaveAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is CoreBrokerClientException or IOException or
                InvalidOperationException or TimeoutException)
        {
            Debug.WriteLine($"WorkspacePanel layout save failed: {exception.Message}");
            await RunOnUiAsync(() =>
            {
                StatusText.Text = _resources.GetString("LayoutSaveFailedStatus");
            }).ConfigureAwait(false);
            return false;
        }
    }

    private void CardLayout_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardLayoutViewModel.ColumnCount))
        {
            _cardGridLayout.ColumnCount = _cardLayout.ColumnCount;
        }

        if (e.PropertyName == nameof(CardLayoutViewModel.Placements))
        {
            _cardGridLayout.InvalidatePlacements();
        }
    }

    private void CardSurface_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardLayoutSurfaceViewModel.Items))
        {
            SynchronizeCardSnapshotRuntimes();
            if (!_cardItemsBound)
            {
                return;
            }

            // Membership changes can recycle a realized DataTemplate into a
            // different card identity. Rebind so the selector chooses the
            // correct template. Placement and order-only changes stay in place.
            ClearDemoNotesCardVisual();
            _realizedCardRuntimes.Clear();
            _cardSurface.ClearViewportVisibility();
            CardItemsRepeater.ItemsSource = null;
            CardItemsRepeater.ItemsSource = _cardSurface.Items;
            RequestCardSubscriptionRefresh();
        }
    }

    private void CardItemsRepeater_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        CardSurfaceItem? item = ResolveCardSurfaceItem(sender, args.Element);
        if (_realizedCardRuntimes.TryGetValue(
                args.Element,
                out CardRuntimeInstance? previousRuntime) &&
            (item is null ||
                !ReferenceEquals(previousRuntime, item.Runtime)))
        {
            _cardSurface.SetViewportVisibility(previousRuntime, false);
            _realizedCardRuntimes.Remove(args.Element);
        }

        if (item is null)
        {
            return;
        }

        PrepareCardSurfaceDepth(args.Element);
        _realizedCardRuntimes[args.Element] = item.Runtime;
        _cardSurface.SetViewportVisibility(item.Runtime, true);
        RequestCardSubscriptionRefresh();
    }

    private void PrepareCardSurfaceDepth(UIElement element)
    {
        if (_highContrast || element.Shadow is not null)
        {
            return;
        }

        element.Shadow = new ThemeShadow();
        element.Translation = new Vector3(0, 0, 4);
    }

    private void CardItemsRepeater_ElementClearing(
        ItemsRepeater sender,
        ItemsRepeaterElementClearingEventArgs args)
    {
        if (_realizedCardRuntimes.Remove(
                args.Element,
                out CardRuntimeInstance? runtime))
        {
            _cardSurface.SetViewportVisibility(runtime, false);
            RequestCardSubscriptionRefresh();
        }
    }

    private CardSurfaceItem? ResolveCardSurfaceItem(
        ItemsRepeater sender,
        UIElement element)
    {
        if (element is FrameworkElement frameworkElement &&
            frameworkElement.DataContext is CardSurfaceItem item)
        {
            return item;
        }

        int index = sender.GetElementIndex(element);
        return _cardSurface.GetItemAt(index);
    }

    private void EnsureCardItemsBound()
    {
        if (_cardItemsBound)
        {
            return;
        }

        CardItemsRepeater.ItemsSource = _cardSurface.Items;
        _cardItemsBound = true;
    }

    private void CardGridHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
        {
            _cardLayout.SetEffectiveWidth(e.NewSize.Width);
        }
    }

    private string FormatNoteStatus(NoteEditorStatus status)
    {
        string resourceKey = status switch
        {
            NoteEditorStatus.Unavailable => "NoteEditorUnavailableStatus",
            NoteEditorStatus.Loading => "NoteEditorLoadingStatus",
            NoteEditorStatus.Ready => "NoteEditorReadyStatus",
            NoteEditorStatus.PendingSave => "NoteEditorPendingSaveStatus",
            NoteEditorStatus.Saving => "NoteEditorSavingStatus",
            NoteEditorStatus.Saved => "NoteEditorSavedStatus",
            NoteEditorStatus.Error => "NoteEditorErrorStatus",
            _ => "NoteEditorErrorStatus",
        };
        return _resources.GetString(resourceKey);
    }

    private static string GetNoteDisplayTitle(string title, string noteId) =>
        string.IsNullOrWhiteSpace(title)
            ? noteId
            : title.Trim();

    private Task RunOnUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_uiDispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_uiDispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult(null);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            completion.SetException(
                new InvalidOperationException("The WorkspacePanel UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private void DispatchToUi(Action action)
    {
        if (_uiDispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _uiDispatcherQueue.TryEnqueue(() => action());
    }

    private void ClosePanelButton_Click(object sender, RoutedEventArgs e)
    {
        RequestCloseMotion();
    }

    private void RequestOpenMotion()
    {
        _cardSurface.SetPanelVisibility(true);
        RequestCardSubscriptionRefresh();
        _motion.RequestOpen();
    }

    private void RequestCloseMotion()
    {
        if (PanelActivationClosePolicy.ShouldDeferCloseRequest(_modalScopeDepth))
        {
            _deferredCloseRequest = true;
            return;
        }

        _deferredCloseRequest = false;
        _cardSurface.SetPanelVisibility(false);
        RequestCardSubscriptionRefresh();
        _motion.RequestClose();
    }

    // Closing the panel hides it and keeps the process warm: process, runtime and XAML
    // startup account for most of the cold-start cost, and re-showing skips all of it.
    // See ADR-0025.
    private void CloseAfterMotion()
    {
        HideForResidency();
    }

    private void HideForResidency()
    {
        if (_isHiddenForResidency)
        {
            return;
        }

        _isHiddenForResidency = true;
        StartupTrace.Mark("residency-hide");
        _appWindow.Hide();

        // A hidden panel does not need its pages resident. This hands them back to the OS
        // and they fault in again on the next show.
        try
        {
            NativeWorkingSet.EmptyWorkingSet(NativeWorkingSet.GetCurrentProcess());
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
    }

    private void ShowAfterResidency()
    {
        if (!_isHiddenForResidency)
        {
            return;
        }

        _isHiddenForResidency = false;
        StartupTrace.Mark("residency-show");
        ApplyPlacement();
        RequestOpenMotion();
    }

    private static Windows.Foundation.Point CalculateTransformOrigin(
        PanelLaunchContext context,
        PanelPlacement placement)
    {
        bool hasAnchor = context.LauncherRect.IsValid;
        bool fromLeft = !hasAnchor ||
            context.LauncherRect.CenterX <= placement.WindowRect.CenterX;
        bool fromTop = hasAnchor &&
            context.LauncherRect.CenterY <= placement.WindowRect.CenterY;
        return new Windows.Foundation.Point(fromLeft ? 0 : 1, fromTop ? 0 : 1);
    }

    private void ApplyPanelMotion(PanelMotionValue value)
    {
        // The panel surface is a native top-level window. Move that surface itself
        // so its background follows the same anchored path as the content.
        NativeWindowStyles.Move(
            _windowHandle,
            _placement.WindowRect.Left + ToPhysicalPixels(value.OffsetX),
            _placement.WindowRect.Top + ToPhysicalPixels(value.OffsetY));
        PanelMotionTransform.TranslateX = 0;
        PanelMotionTransform.TranslateY = 0;
        PanelMotionTransform.ScaleX = value.Scale;
        PanelMotionTransform.ScaleY = value.Scale;

        if (_nativeOpacitySupported)
        {
            byte alpha = (byte)Math.Clamp(
                (int)Math.Round(value.Opacity * byte.MaxValue),
                0,
                byte.MaxValue);
            if (NativeWindowStyles.TrySetOpacity(_windowHandle, alpha))
            {
                // The native window alpha now includes the complete surface. Keeping
                // the XAML tree opaque avoids fading its content a second time.
                RootGrid.Opacity = 1;
                return;
            }

            NativeWindowStyles.DisableLayeredOpacity(_windowHandle);
            _nativeOpacitySupported = false;
            Debug.WriteLine("WorkspacePanel native window opacity became unavailable; using XAML opacity fallback.");
        }

        RootGrid.Opacity = value.Opacity;
    }

    private int ToPhysicalPixels(double logicalPixels) =>
        (int)Math.Round(
            logicalPixels * _placement.Dpi / 96.0,
            MidpointRounding.AwayFromZero);

    private void DemoNotesCardSurface_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_cardEdit.IsEditing ||
            _isSavingLayout ||
            _demoNotesCardPointerId is not null ||
            sender is not FrameworkElement surface ||
            ResolveCurrentCardSurfaceItem(surface) is not CardSurfaceItem item ||
            !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed ||
            IsInteractiveCardContent(e.OriginalSource as DependencyObject, surface) ||
            !_cardLayout.TryGetPlacement(
                item.InstanceId,
                out CardPlacement placement))
        {
            return;
        }

        string instanceId = item.InstanceId;
        InterruptDemoNotesCardReturn();
        ResetCardDropPreview();
        _demoNotesCardTransform = FindNotesCardTransform(surface);
        _draggedCardSurface = surface;
        _draggedCardId = instanceId;
        _dragStartPlacement = placement;
        _demoNotesCardPointerId = e.Pointer.PointerId;
        CardDragUpdate update = _demoNotesCardDrag.Press(GetRootPointer(e));
        if (!surface.CapturePointer(e.Pointer))
        {
            _demoNotesCardPointerId = null;
            _draggedCardSurface = null;
            _draggedCardId = null;
            _dragStartPlacement = null;
            update = _demoNotesCardDrag.Cancel();
        }

        ApplyDemoNotesCardDrag(update);
        e.Handled = true;
    }

    private void DemoNotesCardSurface_PointerMoved(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_demoNotesCardPointerId != e.Pointer.PointerId)
        {
            return;
        }

        CardDragUpdate update = _demoNotesCardDrag.Move(GetRootPointer(e));
        ApplyDemoNotesCardDrag(update);
        if (update.State == CardDragState.Dragging &&
            _draggedCardId is not null &&
            _dragStartPlacement is CardPlacement placement)
        {
            if (TryGetDropCell(placement, update.Offset, out GridCell cell))
            {
                if (_cardEdit.TryPreviewDrop(
                        _draggedCardId,
                        cell,
                        out IReadOnlyList<CardPlacement> previewPlacements))
                {
                    ApplyCardDropPreview(_draggedCardId, previewPlacements);
                }
                else
                {
                    ResetCardDropPreview();
                }
            }
            else
            {
                ResetCardDropPreview();
            }
        }
        e.Handled = true;
    }

    private void DemoNotesCardSurface_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_demoNotesCardPointerId != e.Pointer.PointerId)
        {
            return;
        }

        string? draggedCardId = _draggedCardId;
        CardDragUpdate update = _demoNotesCardDrag.Release(GetRootPointer(e));
        _demoNotesCardPointerId = null;
        if (sender is UIElement surface)
        {
            surface.ReleasePointerCapture(e.Pointer);
        }
        GridCell? dropCell = null;
        if (update.Completed &&
            draggedCardId is not null &&
            _dragStartPlacement is CardPlacement placement &&
            TryGetDropCell(placement, update.Offset, out GridCell finalCell) &&
            _cardEdit.TryPreviewDrop(draggedCardId, finalCell, out _))
        {
            dropCell = finalCell;
        }

        if (update.Completed &&
            draggedCardId is not null &&
            dropCell is GridCell targetCell &&
            _cardEdit.TryCommitDrop(draggedCardId, targetCell, out _))
        {
            ClearDemoNotesCardVisual();
            _draggedCardId = null;
            _dragStartPlacement = null;
            _demoNotesCardDrag.SetOffset(DragOffset.Zero);
            StatusText.Text = _resources.GetString("DemoCardDropCommittedStatus");
            e.Handled = true;
            return;
        }

        if (update.Completed)
        {
            ResetCardDropPreview();
            BeginDemoNotesCardReturn(update.Offset, DragOffset.Zero);
            StatusText.Text = _resources.GetString("DemoCardDropRejectedStatus");
        }
        else
        {
            ResetCardDropPreview();
            ApplyDemoNotesCardDrag(update);
        }
        _draggedCardId = null;
        _dragStartPlacement = null;
        e.Handled = true;
    }

    private void DemoNotesCardSurface_PointerCanceled(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_demoNotesCardPointerId == e.Pointer.PointerId)
        {
            CancelDemoNotesCardDrag();
        }
    }

    private void DemoNotesCardSurface_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_demoNotesCardPointerId is not null)
        {
            CancelDemoNotesCardDrag();
        }
    }

    private void CancelDemoNotesCardDrag()
    {
        DragOffset currentOffset = _demoNotesCardDrag.Offset;
        CardDragUpdate update = _demoNotesCardDrag.Cancel();
        _demoNotesCardPointerId = null;
        _draggedCardId = null;
        _dragStartPlacement = null;
        ResetCardDropPreview();
        BeginDemoNotesCardReturn(currentOffset, update.Offset);
        StatusText.Text = _resources.GetString("DemoCardDragCanceledStatus");
    }

    private void ApplyDemoNotesCardDrag(CardDragUpdate update)
    {
        ApplyDemoNotesCardOffset(
            update.Offset,
            update.State == CardDragState.Dragging);

        if (update.Clicked)
        {
            StatusText.Text = _resources.GetString("DemoCardClickStatus");
        }
        else if (update.Completed)
        {
            StatusText.Text = _resources.GetString("DemoCardDragStatus");
        }
    }

    private void BeginDemoNotesCardReturn(
        DragOffset currentOffset,
        DragOffset targetOffset)
    {
        _motion.BeginCardReturn(currentOffset, targetOffset);
    }

    private void ApplyDemoNotesCardReturn(DragOffset offset)
    {
        ApplyDemoNotesCardOffset(offset, dragging: false);
    }

    private void ApplyDemoNotesCardOffset(DragOffset offset, bool dragging)
    {
        if (_demoNotesCardTransform is null)
        {
            return;
        }

        _demoNotesCardTransform.TranslateX = offset.X;
        _demoNotesCardTransform.TranslateY = offset.Y;

        double scale = dragging ? 1.01 : 1.0;
        _demoNotesCardTransform.ScaleX = scale;
        _demoNotesCardTransform.ScaleY = scale;
        if (!_highContrast && _draggedCardSurface is not null)
        {
            _draggedCardSurface.Translation =
                new Vector3(0, 0, dragging ? 16 : 4);
        }
    }

    private bool TryGetDropCell(
        CardPlacement placement,
        DragOffset offset,
        out GridCell cell)
    {
        double availableWidth = CardItemsRepeater.ActualWidth > 0
            ? CardItemsRepeater.ActualWidth
            : CardGridHost.ActualWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            cell = default;
            return false;
        }

        cell = _cardGridLayout.GetDropCell(
            availableWidth,
            placement,
            offset.X,
            offset.Y);
        return true;
    }

    private void ApplyCardDropPreview(
        string draggedInstanceId,
        IReadOnlyList<CardPlacement> placements)
    {
        if (_cardSurface.ApplyDropPreview(draggedInstanceId, placements))
        {
            _cardGridLayout.InvalidatePlacements();
        }
    }

    private void ResetCardDropPreview()
    {
        if (_cardSurface.ResetDropPreview())
        {
            _cardGridLayout.InvalidatePlacements();
        }
    }

    private void ClearDemoNotesCardVisual()
    {
        CompositeTransform? transform = _demoNotesCardTransform;
        _demoNotesCardTransform = null;
        FrameworkElement? draggedSurface = _draggedCardSurface;
        _draggedCardSurface = null;
        if (transform is not null)
        {
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
        }

        if (!_highContrast && draggedSurface is not null)
        {
            draggedSurface.Translation = new Vector3(0, 0, 4);
        }

        if (!_demoNotesCardDrag.IsActive)
        {
            _demoNotesCardDrag.SetOffset(DragOffset.Zero);
        }

        _motion.ResetCardReturn();
    }

    private void InterruptDemoNotesCardReturn()
    {
        _motion.InterruptCardReturn();
    }

    private DragPoint GetRootPointer(PointerRoutedEventArgs args)
    {
        Windows.Foundation.Point point = args.GetCurrentPoint(RootGrid).Position;
        return new DragPoint(point.X, point.Y);
    }

    private static CompositeTransform? FindNotesCardTransform(
        DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Border border &&
                border.RenderTransform is CompositeTransform transform)
            {
                return transform;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsInteractiveCardContent(
        DependencyObject? source,
        DependencyObject dragSurface)
    {
        DependencyObject? current = source;
        while (current is not null && current != dragSurface)
        {
            if (current is Button or CheckBox or ListView or ScrollViewer or TextBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsControlPressed()
    {
        return InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private static ScreenRect ToScreenRect(Windows.Graphics.RectInt32 rectangle) =>
        new(
            rectangle.X,
            rectangle.Y,
            rectangle.X + rectangle.Width,
            rectangle.Y + rectangle.Height);

    private sealed class ModalScope : IDisposable
    {
        private MainWindow? _owner;

        public ModalScope(MainWindow owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_owner is null)
            {
                return;
            }

            MainWindow owner = _owner;
            _owner = null;
            owner._modalScopeDepth = Math.Max(0, owner._modalScopeDepth - 1);
            if (PanelActivationClosePolicy.ShouldReplayDeferredClose(
                    owner._modalScopeDepth,
                    owner._deferredCloseRequest))
            {
                owner.RequestCloseMotion();
            }
        }
    }

    private static class NativeWorkingSet
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyWorkingSet(IntPtr process);
    }

    private static class NativeWindowStyles
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(
            IntPtr window,
            uint colorKey,
            byte alpha,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        private static extern uint GetDpiForWindowNative(IntPtr window);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr window,
            int attribute,
            ref int value,
            int valueSize);

        public static void MakeToolWindow(IntPtr window)
        {
            long style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
            style = (style | WsExToolWindow) & ~WsExAppWindow;
            SetWindowLongPtr(window, GwlExStyle, new IntPtr(style));
            SetWindowPos(
                window,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoZOrder | SwpFrameChanged);
        }

        public static void PreferRoundedCorners(IntPtr window)
        {
            int preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(
                window,
                DwmwaWindowCornerPreference,
                ref preference,
                sizeof(int));
        }

        public static bool EnableLayeredOpacity(IntPtr window)
        {
            long style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
            SetWindowLongPtr(window, GwlExStyle, new IntPtr(style | WsExLayered));
            SetWindowPos(
                window,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoZOrder | SwpFrameChanged);
            return TrySetOpacity(window, byte.MaxValue);
        }

        public static bool TrySetOpacity(IntPtr window, byte alpha) =>
            SetLayeredWindowAttributes(window, 0, alpha, LwaAlpha);

        public static void Move(IntPtr window, int x, int y)
        {
            SetWindowPos(
                window,
                IntPtr.Zero,
                x,
                y,
                0,
                0,
                SwpNoSize | SwpNoActivate | SwpNoZOrder);
        }

        public static void DisableLayeredOpacity(IntPtr window)
        {
            long style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
            SetWindowLongPtr(window, GwlExStyle, new IntPtr(style & ~WsExLayered));
            SetWindowPos(
                window,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoZOrder | SwpFrameChanged);
        }

        public static uint GetDpiForWindow(IntPtr window) =>
            GetDpiForWindowNative(window);
    }
}
