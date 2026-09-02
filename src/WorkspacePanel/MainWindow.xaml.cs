using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    private readonly AppWindow _appWindow;
    // The popped-out note, while one is open. Null is the normal state; the panel does not
    // keep a closed window around because the editor it edits lives on this side anyway.
    private NoteWindow? _noteWindow;
    private readonly IntPtr _windowHandle;
    private readonly PanelPlacement _placement;
    private readonly IWeatherSettingsClient? _weatherSettingsClient;
    private readonly IDeviceLocationProvider _deviceLocationProvider =
        new WindowsDeviceLocationProvider();
    private readonly ISystemMonitorSettingsClient? _systemMonitorClient;
    private readonly ITokenUsageSettingsClient? _tokenUsageClient;
    private readonly CardSnapshotDispatcher _cardSnapshotDispatcher;
    private int _statusVersion;
    private readonly CardSubscriptionLifecycleCoordinator?
        _cardSubscription;
    private readonly ResourceLoader _resources = new();
    private readonly CardDragController _demoNotesCardDrag = new();
    private readonly PanelMotionCoordinator _motion;
    private readonly CardFoldVisualCoordinator _cardFoldVisuals;
    private readonly UiDispatcherQueue _uiDispatcherQueue;
    private readonly CardGridLayout _cardGridLayout;
    private readonly CardLayoutViewModel _cardLayout;
    private readonly LayoutPersistenceCoordinator? _layoutPersistence;
    private readonly NoteListCoordinator _noteList;
    private readonly CardLayoutEditViewModel _cardEdit;
    private readonly CardLayoutSurfaceViewModel _cardSurface;
    private readonly SurfaceMotionCoordinator _surfaceMotion;
    private readonly StatusLineController _statusLine;
    private readonly ScreenRect _launcherRect;
    private readonly bool _reducedMotion;
    private readonly bool _highContrast;
    private readonly Dictionary<UIElement, CardRuntimeInstance>
        _realizedCardRuntimes = [];
    private readonly bool _keepOpenForAcceptance;
    private bool _cardItemsBound;
    private bool _nativeOpacitySupported;
    private byte? _lastNativeOpacityAlpha;
    private int _modalScopeDepth;
    private bool _deferredCloseRequest;
    private bool _isHiddenForResidency;
    private bool _hasBeenActivated;
    private bool _initialOpenMotionPending;
    private bool _initialOpenMotionQueued;
    private int _initialOpenMotionDeferrals;
    private bool _allowNativeClose;
    private uint? _demoNotesCardPointerId;
    private CompositeTransform? _demoNotesCardTransform;
    private FrameworkElement? _draggedCardSurface;
    private string? _draggedCardId;
    private CardPlacement? _dragStartPlacement;
    private bool _isSavingLayout;
    private int _disposed;
    private RectangleClip? _headerRevealClip;

    public MainWindow(
        INoteClient? noteClient = null,
        ILayoutClient? layoutClient = null,
        bool keepOpenForAcceptance = false,
        CoreBrokerCardsClient? cardsClient = null,
        IWeatherSettingsClient? weatherSettingsClient = null,
        ISystemMonitorSettingsClient? systemMonitorClient = null,
        ITokenUsageSettingsClient? tokenUsageClient = null)
    {
        _weatherSettingsClient = weatherSettingsClient;
        _systemMonitorClient = systemMonitorClient;
        _tokenUsageClient = tokenUsageClient;
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
        _statusLine = new StatusLineController(
            StatusText,
            DispatcherQueue,
            _reducedMotion);
        StartupTrace.Mark("ctor-settings-read");
        TryConfigureSystemBackdrop();
        StartupTrace.Mark("ctor-backdrop");
        // The shipped board: the four real capabilities at their definitions' default sizes.
        // The deferred placeholders earn no slot - a card whose content is "not available
        // yet" is noise, not a preview.
        _cardLayout = new CardLayoutViewModel(
            4,
            [
                new CardLayoutItem(
                    BuiltInCardCatalog.NotesInstanceId,
                    CardSize.L),
                new CardLayoutItem(
                    BuiltInCardCatalog.WeatherInstanceId,
                    CardSize.L),
                new CardLayoutItem(
                    BuiltInCardCatalog.SystemMonitorInstanceId,
                    CardSize.L),
                new CardLayoutItem(
                    BuiltInCardCatalog.TokenUsageInstanceId,
                    CardSize.L),
            ]);
        _cardEdit = new CardLayoutEditViewModel(_cardLayout);
        _cardSurface = new CardLayoutSurfaceViewModel(
            _cardEdit,
            NoteEditor,
            NoteSearch,
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
        RootGrid.Loaded += RootGrid_Loaded;

        ConfigureToolWindow();
        PanelLaunchContext context = ResolveLaunchContext(windowId);
        _launcherRect = context.LauncherRect;
        _placement = PanelGeometry.Calculate(context);
        if (!_placement.IsValid)
        {
            throw new InvalidOperationException(
                $"WorkspacePanel geometry rejected: {_placement.Reason}");
        }

        _cardFoldVisuals = new CardFoldVisualCoordinator(_reducedMotion);
        _motion = new PanelMotionCoordinator(
            _reducedMotion,
            ApplyPanelMotion,
            ApplyCardFoldMotion,
            _cardFoldVisuals.Complete,
            ApplyDemoNotesCardReturn,
            () => _demoNotesCardDrag.IsActive,
            offset => _demoNotesCardDrag.SetOffset(offset),
            CloseAfterMotion,
            ReportMotionFrameTiming);
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
        EnsureCardItemsBound();
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
        _initialOpenMotionPending = true;
        TryQueueInitialOpenMotion();
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
        _cardFoldVisuals.Dispose();
        _surfaceMotion.Dispose();
        _statusLine.Dispose();
        _cardSurface.SetPanelVisibility(false);
        _realizedCardRuntimes.Clear();
        _appWindow.Closing -= AppWindow_Closing;
        _appWindow.Changed -= AppWindow_Changed;
        Activated -= MainWindow_Activated;
        Closed -= MainWindow_Closed;
        RootGrid.Loaded -= RootGrid_Loaded;
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
        _cardFoldVisuals.Dispose();
        _surfaceMotion.Dispose();
        _statusLine.Dispose();
        _cardSubscription?.OnWindowClosed();
        _cardSurface.SetPanelVisibility(false);
        _realizedCardRuntimes.Clear();
        _cardLayout.PropertyChanged -= CardLayout_PropertyChanged;
        _cardSurface.PropertyChanged -= CardSurface_PropertyChanged;
        _cardEdit.PropertyChanged -= CardEdit_PropertyChanged;
        NoteEditor.PropertyChanged -= NoteEditor_PropertyChanged;
        RootGrid.Loaded -= RootGrid_Loaded;
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

    private void SysMonSettingsButton_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsDialog(SettingsCategory.SystemMonitor);

    private void TokenUsageSettingsButton_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsDialog(SettingsCategory.TokenUsage);

    /// <summary>
    /// Switches the token-usage card to another page. Purely local view state: no IPC, no
    /// persistence, and the card returns to the overview the next time the panel opens.
    /// </summary>
    private void TokenUsagePageTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string pageId } ||
            pageId.Length == 0)
        {
            return;
        }

        foreach (CardSurfaceItem card in _cardSurface.Items)
        {
            if (string.Equals(
                    card.CardTypeId,
                    BuiltInCardCatalog.TokenUsageCardTypeId,
                    StringComparison.Ordinal))
            {
                card.SelectTokenUsagePage(pageId);
            }
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsDialog(SettingsCategory.General);

    /// <summary>
    /// Opens the one settings surface on the requested category. Both entry points land here:
    /// the header button and a card's own settings button differ only in where they start,
    /// not in what they open, so a user who arrived from the hardware card can still reach the
    /// weather section without closing anything.
    /// </summary>
    private async void ShowSettingsDialog(SettingsCategory category)
    {
        Interlocked.Increment(ref _statusVersion);
        var generalViewModel = new GeneralSettingsViewModel(
            StartupShortcut.ForCurrentUser(),
            key => _resources.GetString(key));
        var entryViewModel = new LauncherEntrySettingsViewModel(
            new RegistryLauncherPreferenceStore(),
            key => _resources.GetString(key));
        var weatherViewModel = new WeatherSettingsViewModel(
            _weatherSettingsClient,
            key => _resources.GetString(key),
            _deviceLocationProvider);
        var systemMonitorViewModel = new SystemMonitorSettingsViewModel(
            _systemMonitorClient,
            key => _resources.GetString(key.Replace('.', '/')));
        var tokenUsageViewModel = new TokenUsageSettingsViewModel(
            _tokenUsageClient,
            key => _resources.GetString(key.Replace('.', '/')));
        try
        {
            if (RootGrid.XamlRoot is null)
            {
                return;
            }

            using var dialog = new SettingsDialog(
                generalViewModel,
                entryViewModel,
                weatherViewModel,
                systemMonitorViewModel,
                tokenUsageViewModel,
                category)
            {
                XamlRoot = RootGrid.XamlRoot,
            };
            // Both sections load before the dialog opens: the rail lets the user switch at
            // any time, and a section that only starts loading when it is first shown would
            // flash its empty state on every switch.
            await weatherViewModel.LoadAsync(CancellationToken.None);
            await systemMonitorViewModel.LoadAsync(CancellationToken.None);
            await tokenUsageViewModel.LoadAsync(CancellationToken.None);
            using IDisposable modalScope = EnterModalScope();
            await dialog.ShowAsync();
            Interlocked.Increment(ref _statusVersion);
            StatusText.Text = ResolveSettingsStatus(
                category,
                generalViewModel,
                weatherViewModel,
                systemMonitorViewModel,
                tokenUsageViewModel);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException and
                not StackOverflowException and
                not AccessViolationException)
        {
            Debug.WriteLine($"WorkspacePanel settings dialog failed: {exception}");
            Interlocked.Increment(ref _statusVersion);
            StatusText.Text = _resources.GetString(
                category switch
                {
                    SettingsCategory.SystemMonitor => "SysMonSettingsLoadFailedStatus",
                    SettingsCategory.TokenUsage => "TokenUsageSettingsLoadFailedStatus",
                    _ => "WeatherSettingsLoadFailedStatus",
                });
        }
    }

    public Task RefreshAutomaticWeatherLocationAsync(
        CancellationToken cancellationToken) =>
        RunOnUiAsync(async () =>
        {
            var weatherViewModel = new WeatherSettingsViewModel(
                _weatherSettingsClient,
                key => _resources.GetString(key),
                _deviceLocationProvider);
            if (await weatherViewModel.LoadAsync(cancellationToken) &&
                weatherViewModel.UseDeviceLocation)
            {
                await weatherViewModel.RefreshDeviceLocationAsync(cancellationToken);
            }
        });

    /// <summary>
    /// A save in either section is worth reporting, whichever category the dialog opened on.
    /// The weather section states its own saved message; the hardware section already carries
    /// one in its status text.
    /// </summary>
    private string ResolveSettingsStatus(
        SettingsCategory category,
        GeneralSettingsViewModel general,
        WeatherSettingsViewModel weather,
        SystemMonitorSettingsViewModel systemMonitor,
        TokenUsageSettingsViewModel tokenUsage)
    {
        if (weather.WasSaved)
        {
            return _resources.GetString("WeatherSettingsSavedStatus");
        }

        return category switch
        {
            SettingsCategory.General => general.StatusText,
            // The entry's settings applied the moment they changed; there is no outcome
            // left to report once the dialog closes.
            SettingsCategory.TaskbarEntry => string.Empty,
            SettingsCategory.SystemMonitor => systemMonitor.StatusText,
            SettingsCategory.TokenUsage => tokenUsage.StatusText,
            _ => weather.StatusText,
        };
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
        // Every notes card, not the first one found: the editor is shared, so a second card
        // on the board shows the same note and has to follow the same state.
        foreach (FrameworkElement cardRoot in RealizedNoteCardRoots())
        {
            ApplyNoteContentState(cardRoot);
        }
    }

    /// <summary>
    /// The one place that decides what a notes card's content row shows. The editor, the
    /// Markdown preview and (on a one-row card) the note switcher share it and never appear
    /// together; the state is read from the editor and from the card's own switcher, so the
    /// card is right whether the change came from a button, a resize or a fresh realization.
    /// </summary>
    private void ApplyNoteContentState(FrameworkElement cardRoot)
    {
        bool isPreviewVisible = NoteEditor.IsMarkdownPreviewVisible;
        bool browsingExclusively =
            ResolveCurrentCardSurfaceItem(cardRoot) is { IsNoteBrowsingExclusive: true } &&
            IsNoteSwitcherOpen(cardRoot);
        // Looked up as FrameworkElement, not as the concrete panel type: this only needs
        // something it can show and hide, and typing it to StackPanel meant the editor
        // silently stopped hiding the day its layout became a Grid.
        FrameworkElement? inputPanel = FindDescendantByName<FrameworkElement>(
            cardRoot,
            "NoteEditorInputPanel");
        FrameworkElement? previewPanel = FindDescendantByName<FrameworkElement>(
            cardRoot,
            "NoteMarkdownPreviewPanel");
        bool showInput = !browsingExclusively && !isPreviewVisible;
        bool showPreview = !browsingExclusively && isPreviewVisible;
        ApplyContentPanel(inputPanel, showInput);
        ApplyContentPanel(previewPanel, showPreview);

        SetVisibility(
            FindDescendantByName<Button>(
                cardRoot,
                "PreviewNoteButton"),
            !isPreviewVisible);
        SetVisibility(
            FindDescendantByName<Button>(
                cardRoot,
                "EditMarkdownButton"),
            isPreviewVisible);
    }

    private void ApplyContentPanel(FrameworkElement? panel, bool isVisible)
    {
        if (panel is null)
        {
            return;
        }

        if (isVisible)
        {
            _surfaceMotion.Show(panel, SurfaceMotionAnchor.Center);
        }
        else
        {
            _surfaceMotion.HideImmediately(panel);
        }
    }

    // Realized elements are found through the repeater's own index, the way
    // ReestablishRealizedCards does, rather than through DataContext: the templates bind
    // with x:Bind, and the repeater is under no obligation to set a DataContext for those.
    private IEnumerable<FrameworkElement> RealizedNoteCardRoots()
    {
        for (int index = 0; index < _cardSurface.Items.Count; index++)
        {
            if (_cardSurface.GetItemAt(index) is { } item &&
                string.Equals(
                    item.CardTypeId,
                    BuiltInCardCatalog.NotesCardTypeId,
                    StringComparison.Ordinal) &&
                CardItemsRepeater.TryGetElement(index) is FrameworkElement element)
            {
                yield return element;
            }
        }
    }

    /// <summary>
    /// The realized root of the notes card an element belongs to: the element the repeater
    /// hands out for the card's index. Note handlers scope their lookups to it rather than
    /// to the page, so that with two notes cards on the board the switcher opens on the card
    /// whose button was pressed and not on whichever card the visual tree lists first.
    /// </summary>
    private FrameworkElement? FindNoteCardRoot(object sender)
    {
        DependencyObject? current = sender as DependencyObject;
        while (current is not null &&
            !ReferenceEquals(current, CardItemsRepeater))
        {
            if (current is FrameworkElement element &&
                CardItemsRepeater.GetElementIndex(element) >= 0)
            {
                return element;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private bool IsNoteSwitcherOpen(FrameworkElement cardRoot) =>
        FindDescendantByName<FrameworkElement>(cardRoot, "NoteSearchResultsBorder")
            is { } results &&
        _surfaceMotion.IsVisible(results);

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
        FrameworkElement cardRoot,
        bool isVisible,
        bool immediate = false)
    {
        // Looked up rather than referenced: the switcher moved into the notes card template,
        // so it is realized per card element and is not a field on this page.
        FrameworkElement? results = FindDescendantByName<FrameworkElement>(
            cardRoot,
            "NoteSearchResultsBorder");
        if (results is null)
        {
            return;
        }

        if (isVisible)
        {
            _surfaceMotion.Show(
                results,
                SurfaceMotionAnchor.Top,
                floating: true);
        }
        else if (immediate ||
            ResolveCurrentCardSurfaceItem(cardRoot) is { IsNoteBrowsingExclusive: true })
        {
            // On a one-row card the editor takes the switcher's place the moment it closes;
            // fading the switcher out would keep both in the layout for the length of the
            // fade and then jump.
            _surfaceMotion.HideImmediately(results);
        }
        else
        {
            _surfaceMotion.Hide(
                results,
                SurfaceMotionAnchor.Top);
        }

        ApplyNoteContentState(cardRoot);
    }

    private void CloseNoteSwitcher(FrameworkElement cardRoot)
    {
        ClearNoteSearchBox(cardRoot);
        _ = _noteList.SearchAsync(string.Empty, CancellationToken.None);
        SetSearchResultsVisible(cardRoot, false);
        StatusText.Text = _resources.GetString("NoteListClosedStatus");
    }

    // No suppression flag around the assignment: TextChanged is raised on a later tick, not
    // inside the setter, so a flag cleared on the way out was always down again by the time
    // the handler ran. The handler instead recognizes an empty box for an already-empty
    // query as nothing to do.
    private static void ClearNoteSearchBox(FrameworkElement cardRoot)
    {
        if (FindDescendantByName<TextBox>(cardRoot, "SearchBox") is { } searchBox)
        {
            searchBox.Text = string.Empty;
        }
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

    // "New", "open" and "delete" go through the list coordinator, which saves whatever the
    // user was typing a moment ago before the editor moves on. The blocked messages remain
    // for the one draft it cannot save: one whose save has already failed.
    private async void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseNoteOverflow(sender);
        if (FindNoteCardRoot(sender) is { } cardRoot)
        {
            SetSearchResultsVisible(cardRoot, false);
        }

        StatusText.Text = _resources.GetString("NoteCreatingStatus");
        bool created = await _noteList.CreateNoteAsync(CancellationToken.None);
        StatusText.Text = _resources.GetString(
            created
                ? "NoteCreatedStatus"
                : NoteEditor.HasUnsavedChanges
                    ? "NoteCreateBlockedStatus"
                    : "NoteCreateFailedStatus");
    }

    private async void DeleteCurrentNoteButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CollapseNoteOverflow(sender);
        FrameworkElement? cardRoot = FindNoteCardRoot(sender);
        bool switcherWasOpen = cardRoot is not null && IsNoteSwitcherOpen(cardRoot);
        string title = GetNoteDisplayTitle(NoteEditor.Title, NoteEditor.NoteId);
        if (!await ConfirmNoteDeletionAsync(title))
        {
            return;
        }

        StatusText.Text = _resources.GetString("NoteDeletingStatus");
        bool deleted = await _noteList.DeleteCurrentNoteAsync(CancellationToken.None);
        if (!deleted)
        {
            StatusText.Text = _resources.GetString(
                NoteEditor.HasUnsavedChanges
                    ? "NoteDeleteBlockedStatus"
                    : "NoteDeleteFailedStatus");
            return;
        }

        bool refreshed = await RefreshNoteResultsAfterDeleteAsync(
            cardRoot,
            keepOpen: switcherWasOpen);
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

        FrameworkElement? cardRoot = FindNoteCardRoot(button);
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
                ? await _noteList.DeleteCurrentNoteAsync(CancellationToken.None)
                : await NoteSearch.DeleteNoteAsync(result, CancellationToken.None);
            if (!deleted)
            {
                StatusText.Text = _resources.GetString(
                    deletingCurrent && NoteEditor.HasUnsavedChanges
                        ? "NoteDeleteBlockedStatus"
                        : "NoteDeleteFailedStatus");
                return;
            }

            bool refreshed = await RefreshNoteResultsAfterDeleteAsync(
                cardRoot,
                keepOpen: true);
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

    /// <summary>
    /// Opens the note switcher, or closes it if it is already open. Browsing needs a way out
    /// that is not picking a note: the button that opened it is the obvious one, and Escape
    /// in the search box is the other.
    /// </summary>
    private async void ListNotesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NoteSearch.CanSearch || FindNoteCardRoot(sender) is not { } cardRoot)
        {
            return;
        }

        if (IsNoteSwitcherOpen(cardRoot))
        {
            CloseNoteSwitcher(cardRoot);
            return;
        }

        ClearNoteSearchBox(cardRoot);
        SetSearchResultsVisible(cardRoot, false, immediate: true);
        StatusText.Text = _resources.GetString("NoteListLoadingStatus");
        NoteListOperationResult result = await _noteList.LoadAllAsync(
            CancellationToken.None);
        if (!result.IsCurrentQuery)
        {
            return;
        }

        SetSearchResultsVisible(cardRoot, result.HasResults);
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

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || FindNoteCardRoot(sender) is not { } cardRoot)
        {
            return;
        }

        CloseNoteSwitcher(cardRoot);
        FindDescendantByName<TextBox>(cardRoot, "NoteBodyBox")
            ?.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox ||
            FindNoteCardRoot(textBox) is not { } cardRoot)
        {
            return;
        }

        string query = textBox.Text.Trim();
        if (query.Length == 0)
        {
            if (NoteSearch.Query.Length == 0)
            {
                // The box was cleared by the code that opens or closes the switcher, and the
                // listing it started is already the empty query. Searching for nothing
                // again here would outdate that listing and hide the list it was about to
                // show - which is exactly what reopening after a search used to do.
                return;
            }

            await _noteList.SearchAsync(query, CancellationToken.None);
            SetSearchResultsVisible(cardRoot, false);
            StatusText.Text = _resources.GetString("NoteSearchClearedStatus");
            return;
        }

        SetSearchResultsVisible(cardRoot, false, immediate: true);
        StatusText.Text = _resources.GetString("NoteSearchSearchingStatus");
        NoteListOperationResult result = await _noteList.SearchAsync(
            query,
            CancellationToken.None);
        if (!result.IsCurrentQuery)
        {
            return;
        }

        SetSearchResultsVisible(cardRoot, result.HasResults);
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
        if (sender is not FrameworkElement { Tag: string noteId } element)
        {
            return;
        }

        FrameworkElement? cardRoot = FindNoteCardRoot(element);
        bool loaded = await _noteList.LoadNoteAsync(
            noteId,
            CancellationToken.None);
        if (loaded && cardRoot is not null)
        {
            // Picking a note is the end of browsing. The switcher lives inside the card now,
            // so leaving it open would keep the editor the user just asked for pushed below
            // the fold - in the header it merely sat above the board and cost nothing.
            SetSearchResultsVisible(cardRoot, false);
        }

        StatusText.Text = _resources.GetString(
            loaded
                ? "NoteLoadedStatus"
                : NoteEditor.HasUnsavedChanges
                    ? "NoteLoadBlockedStatus"
                    : "NoteLoadFailedStatus");
    }

    /// <summary>
    /// Re-runs the current listing after a delete. The switcher keeps the state it had: one
    /// the user deleted from stays open with the row gone, one that was closed stays closed.
    /// Opening it uninvited after a delete from the overflow menu made the list button, which
    /// now toggles, close what the user had not asked to see.
    /// </summary>
    private async Task<bool> RefreshNoteResultsAfterDeleteAsync(
        FrameworkElement? cardRoot,
        bool keepOpen)
    {
        NoteListOperationResult result = await _noteList
            .RefreshAfterDeleteAsync(CancellationToken.None);
        if (!result.IsCurrentQuery)
        {
            return false;
        }

        if (cardRoot is not null)
        {
            SetSearchResultsVisible(cardRoot, keepOpen && result.HasResults);
        }

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

    // Retry, undo, redo and the preview toggles no longer narrate themselves on the panel's
    // status line: their result is on the card, in the footer or in the text, and a second
    // sentence saying so at the top of the panel was a duplicate.
    private async void RetryNoteSaveButton_Click(object sender, RoutedEventArgs e)
    {
        bool saved = await NoteEditor.RetrySaveAsync(CancellationToken.None);
        if (!saved)
        {
            StatusText.Text = _resources.GetString("NoteSaveRetryFailedStatus");
        }
    }

    private async void ReloadNoteButton_Click(object sender, RoutedEventArgs e)
    {
        bool reloaded = await NoteEditor.ReloadAsync(CancellationToken.None);
        StatusText.Text = _resources.GetString(
            reloaded ? "NoteReloadedStatus" : "NoteReloadFailedStatus");
    }

    private void UndoNoteButton_Click(object sender, RoutedEventArgs e) =>
        NoteEditor.Undo();

    private void RedoNoteButton_Click(object sender, RoutedEventArgs e) =>
        NoteEditor.Redo();

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
    }

    private void EditMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseNoteOverflow(sender);
        if (!NoteEditor.CanEdit)
        {
            return;
        }

        NoteEditor.ToggleMarkdownPreview();
    }

    /// <summary>
    /// Pops the current note into a window of its own, or brings the existing one forward.
    ///
    /// One window, not one per click: the pop-out shares the panel's editor, so a second
    /// window would be a second view of the same note competing for the same caret.
    /// </summary>
    private void PopOutNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_noteWindow is not null)
        {
            _noteWindow.BringToFront();
            return;
        }

        var window = new NoteWindow(
            NoteEditor,
            FormatNoteStatus,
            _resources.GetString("NoteWindowTitle"),
            RootGrid.ActualTheme);
        window.Dismissed += NoteWindow_Dismissed;
        _noteWindow = window;
        window.Activate();
        StatusText.Text = _resources.GetString("NoteWindowOpenedStatus");
    }

    private void NoteWindow_Dismissed(object? sender, EventArgs e)
    {
        if (sender is NoteWindow window)
        {
            window.Dismissed -= NoteWindow_Dismissed;
        }

        _noteWindow = null;
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

    private void NoteTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            NoteEditorKeyboard.TryHandle(NoteEditor, textBox, e);
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
            _cardFoldVisuals.Clear();
            _realizedCardRuntimes.Clear();
            _cardSurface.ClearViewportVisibility();
            CardItemsRepeater.ItemsSource = null;
            CardItemsRepeater.ItemsSource = _cardSurface.Items;
            // The clear above dropped every registration, and ElementPrepared only fires
            // again for elements the repeater actually rebuilds. A card the new membership
            // still contains can keep its element, so it would never be registered again -
            // and a card that is not registered is never reported in the viewport, which
            // stops its provider, leaves a local card unable to publish its own content, and
            // leaves it out of the fold. This is the panel's normal startup path, not an edge
            // case: the surface is bound to the built-in board in the constructor and then
            // replaced when the stored layout arrives.
            ReestablishRealizedCards();
            RequestCardSubscriptionRefresh();
        }
    }

    /// <summary>
    /// Re-registers whatever the repeater currently has realized. Idempotent with
    /// <see cref="CardItemsRepeater_ElementPrepared"/>: an element that does get prepared
    /// again simply registers the same values twice.
    /// </summary>
    private void ReestablishRealizedCards()
    {
        // The rebind above has not laid out yet, so nothing is realized until it does.
        CardItemsRepeater.UpdateLayout();

        int reestablished = 0;
        for (int index = 0; index < _cardSurface.Items.Count; index++)
        {
            if (CardItemsRepeater.TryGetElement(index) is not UIElement element)
            {
                continue;
            }

            CardSurfaceItem? item = _cardSurface.GetItemAt(index);
            if (item is null)
            {
                continue;
            }

            PrepareCardSurfaceDepth(element);
            _cardFoldVisuals.Register(item.InstanceId, element);
            _realizedCardRuntimes[element] = item.Runtime;
            _cardSurface.SetViewportVisibility(item.Runtime, true);
            reestablished++;
        }

        StartupTrace.Mark($"card-reestablished-{reestablished}");
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
        _cardFoldVisuals.Register(item.InstanceId, args.Element);
        TryQueueInitialOpenMotion();
        _realizedCardRuntimes[args.Element] = item.Runtime;
        _cardSurface.SetViewportVisibility(item.Runtime, true);
        RequestCardSubscriptionRefresh();
        // A notes card realized while the shared editor is already in preview would
        // otherwise come up showing the editor; its template only knows the default state.
        if (args.Element is FrameworkElement cardRoot &&
            string.Equals(
                item.CardTypeId,
                BuiltInCardCatalog.NotesCardTypeId,
                StringComparison.Ordinal))
        {
            ApplyNoteContentState(cardRoot);
        }
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
        _cardFoldVisuals.Unregister(args.Element);
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
        // Ready is the steady state, and a steady state carries no status line: the hint it
        // used to print sat on the card permanently, which the visual spec's status rules
        // (loading, stale, offline, error, empty only) exist to prevent.
        if (status is NoteEditorStatus.Ready)
        {
            return string.Empty;
        }

        string resourceKey = status switch
        {
            NoteEditorStatus.Unavailable => "NoteEditorUnavailableStatus",
            NoteEditorStatus.Loading => "NoteEditorLoadingStatus",
            NoteEditorStatus.PendingSave => "NoteEditorPendingSaveStatus",
            NoteEditorStatus.Saving => "NoteEditorSavingStatus",
            NoteEditorStatus.Saved => "NoteEditorSavedStatus",
            // Three errors, three sentences: a draft the store would not take as written, a
            // note that never arrived, and a save that can still be retried. "Save failed"
            // for all three told the user to retry the two a retry cannot fix.
            NoteEditorStatus.Error when string.Equals(
                NoteEditor.ErrorCode,
                "conflict.notes-revision",
                StringComparison.Ordinal) => "NoteEditorConflictStatus",
            NoteEditorStatus.Error when !NoteEditor.CanEdit => "NoteEditorLoadFailedStatus",
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

    private Task RunOnUiAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_uiDispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_uiDispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
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

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        TryQueueInitialOpenMotion();
    }

    private void TryQueueInitialOpenMotion()
    {
        if (!_initialOpenMotionPending ||
            _initialOpenMotionQueued ||
            !RootGrid.IsLoaded)
        {
            return;
        }

        _initialOpenMotionQueued = true;
        if (_uiDispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                StartInitialOpenMotion))
        {
            return;
        }

        _initialOpenMotionQueued = false;
        _initialOpenMotionPending = false;
        RequestOpenMotion();
    }

    private void StartInitialOpenMotion()
    {
        _initialOpenMotionQueued = false;
        if (!_initialOpenMotionPending)
        {
            return;
        }

        RootGrid.UpdateLayout();
        CardItemsRepeater.UpdateLayout();
        if (_cardSurface.Items.Count > 0 &&
            _cardFoldVisuals.RegisteredCount == 0 &&
            _initialOpenMotionDeferrals++ < 2)
        {
            TryQueueInitialOpenMotion();
            return;
        }

        _initialOpenMotionPending = false;
        StartupTrace.Mark(
            $"card-fold-initial-ready-{_cardFoldVisuals.RegisteredCount}");
        RequestOpenMotion();
    }

    private void RequestOpenMotion()
    {
        _cardSurface.SetPanelVisibility(true);
        RequestCardSubscriptionRefresh();
        PrepareCardFoldMotion();
        _motion.RequestOpen();
        StartupTrace.Mark(
            $"card-fold-open-animating-{_motion.IsCardFoldAnimating}");
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
        PrepareCardFoldMotion();
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

        // Leaving the panel abandons an unfinished edit. Editing is a mode, and a mode the
        // user cannot see is one they will not remember being in: the panel used to hide with
        // the mode still on, so the next time it opened - possibly hours later - it opened
        // into a half-finished arrangement nobody had asked for. Nothing is lost that was
        // ever committed; only this session's uncommitted moves go, which is what "close
        // without saving" means. A save already in flight owns the layout and is left alone.
        if (_cardEdit.IsEditing && !_isSavingLayout)
        {
            CancelLayoutEdit();
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

    private void PrepareCardFoldMotion()
    {
        if (!_cardFoldVisuals.IsPrepared)
        {
            RootGrid.UpdateLayout();
            CardItemsRepeater.UpdateLayout();
        }

        IReadOnlyList<string> ids = _cardFoldVisuals.Prepare(
            RootGrid,
            CalculateLauncherAnchor(),
            _cardSurface.Items.Select(item => item.InstanceId).ToArray());
        _motion.ConfigureCardFolds(ids);
        StartupTrace.Mark($"card-fold-prepared-{ids.Count}");
    }

    private MotionPoint CalculateLauncherAnchor()
    {
        if (!_launcherRect.IsValid)
        {
            return new MotionPoint(0, RootGrid.ActualHeight);
        }
        double toLogical = 96.0 / _placement.Dpi;
        return new MotionPoint(
            (_launcherRect.CenterX - _placement.WindowRect.Left) * toLogical,
            (_launcherRect.CenterY - _placement.WindowRect.Top) * toLogical);
    }

    private void ApplyCardFoldMotion(
        IReadOnlyList<CardFoldMotionValue> values) =>
        _cardFoldVisuals.Apply(values);

    private static void ReportMotionFrameTiming(MotionFrameTiming timing)
    {
        StartupTrace.Mark(string.Format(
            CultureInfo.InvariantCulture,
            "motion-frame-cadence count={0} averageMs={1:F2} maximumMs={2:F2}",
            timing.FrameCount,
            timing.AverageMilliseconds,
            timing.MaximumMilliseconds));
    }

    private void ApplyPanelMotion(PanelMotionValue value)
    {
        ApplyHeaderReveal(value.Opacity);

        if (_nativeOpacitySupported)
        {
            byte alpha = (byte)Math.Clamp(
                (int)Math.Round(value.Opacity * byte.MaxValue),
                0,
                byte.MaxValue);
            if (_lastNativeOpacityAlpha == alpha)
            {
                return;
            }
            if (NativeWindowStyles.TrySetOpacity(_windowHandle, alpha))
            {
                _lastNativeOpacityAlpha = alpha;
                return;
            }
            NativeWindowStyles.DisableLayeredOpacity(_windowHandle);
            _nativeOpacitySupported = false;
            _lastNativeOpacityAlpha = null;
            Debug.WriteLine(
                "WorkspacePanel native opacity unavailable; using XAML fallback.");
        }
        RootGrid.Opacity = value.Opacity;
    }

    private void ApplyHeaderReveal(double progress)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(HeaderBar);
        if (_reducedMotion ||
            progress >= 1 ||
            HeaderBar.ActualWidth <= 0 ||
            HeaderBar.ActualHeight <= 0)
        {
            // The clip must not outlive the reveal. HeaderBar hosts the note search
            // results, which grow it after the motion has settled; a clip still
            // sized to the collapsed header would cut them off, and UIA cannot see
            // that a composition clip is hiding them.
            visual.Clip = null;
            _headerRevealClip = null;
            return;
        }
        float width = (float)HeaderBar.ActualWidth;
        float height = (float)HeaderBar.ActualHeight;
        _headerRevealClip ??= visual.Compositor.CreateRectangleClip(
            0,
            0,
            width,
            height);
        _headerRevealClip.Right =
            width * (float)Math.Clamp(progress, 0, 1);
        _headerRevealClip.Bottom = height;
        visual.Clip = _headerRevealClip;
    }

    private int ToPhysicalPixels(double logicalPixels) =>
        (int)Math.Round(
            logicalPixels * _placement.Dpi / 96.0,
            MidpointRounding.AwayFromZero);

    /// <summary>
    /// How far from the frame's trailing edge a press still counts as the handle. It covers
    /// the grab area the frame template draws - 36 DIP reaching 30 back from the corner - with
    /// a little slack, because the only presses that arrive here are on the handle in the
    /// first place: the frame's middle takes no pointer input.
    /// </summary>
    private const double CardResizeHandleBand = 32;

    private void CardResizeFrame_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_cardEdit.IsEditing ||
            _isSavingLayout ||
            _cardResizePointerId is not null ||
            _demoNotesCardPointerId is not null ||
            sender is not FrameworkElement frame ||
            ResolveCurrentCardSurfaceItem(frame) is not CardSurfaceItem item ||
            !e.GetCurrentPoint(frame).Properties.IsLeftButtonPressed ||
            !_cardLayout.TryGetPlacement(item.InstanceId, out CardPlacement placement))
        {
            return;
        }

        CardResizeDirection direction = ResolveResizeDirection(
            frame,
            e.GetCurrentPoint(frame).Position);
        if (direction == CardResizeDirection.None)
        {
            return;
        }


        InterruptDemoNotesCardReturn();
        ResetCardDropPreview();
        _cardResizeFrame = frame;
        _cardResizeId = item.InstanceId;
        _cardResizeStartPlacement = placement;
        _cardResizeSize = placement.Size;
        _cardResizePointerId = e.Pointer.PointerId;
        _cardResizePressPoint = GetRootPointer(e);
        _cardResizeGhostOrigin = frame
            .TransformToVisual(CardGridHost)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        _cardResizeGhostWidth = frame.ActualWidth;
        _cardResizeGhostHeight = frame.ActualHeight;
        if (!frame.CapturePointer(e.Pointer))
        {
            ResetCardResize();
            return;
        }

        ShowCardResizeGhost(_cardResizeGhostWidth, _cardResizeGhostHeight);

        e.Handled = true;
    }

    private void CardResizeFrame_PointerMoved(
        object sender,
        PointerRoutedEventArgs e)
    {
        // Hovering is its own job: the pointer has to say which way a handle will stretch the
        // card before anyone presses it, otherwise the only way to find a handle is to try.
        if (_cardResizePointerId is null)
        {
            if (sender is CardResizeFrame hovered)
            {
                hovered.ShowResizeCursor(
                    ResolveResizeDirection(hovered, e.GetCurrentPoint(hovered).Position));
            }

            return;
        }

        if (_cardResizePointerId != e.Pointer.PointerId ||
            _cardResizeId is not string instanceId ||
            _cardResizeStartPlacement is not CardPlacement placement)
        {
            return;
        }

        DragPoint point = GetRootPointer(e);
        double availableWidth = CardItemsRepeater.ActualWidth > 0
            ? CardItemsRepeater.ActualWidth
            : CardGridHost.ActualWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return;
        }

        // The band follows the pointer on every move, not only when a threshold is crossed.
        // Snapping alone leaves the gesture dead between steps: the pointer travels half a
        // card with nothing responding, which reads as the drag having stopped working.
        ShowCardResizeGhost(
            _cardResizeGhostWidth + (point.X - _cardResizePressPoint.X),
            _cardResizeGhostHeight + (point.Y - _cardResizePressPoint.Y));

        CardSize target = CardResizeCalculator.GetResizeTarget(
            _cardGridLayout.ColumnCount,
            availableWidth,
            _cardGridLayout.ColumnGap,
            _cardGridLayout.RowHeight,
            _cardGridLayout.RowGap,
            placement,
            point.X - _cardResizePressPoint.X,
            point.Y - _cardResizePressPoint.Y,
            resizeColumns: true,
            resizeRows: true);
        e.Handled = true;
        if (target == _cardResizeSize)
        {
            return;
        }

        // The drag previews and the release commits. Applying each threshold as it is crossed
        // would push one undo entry per threshold, so taking back a single gesture would take
        // three undos.
        _cardResizeSize = target;
        if (_cardEdit.TryPreviewResize(
                instanceId,
                target,
                out IReadOnlyList<CardPlacement> placements))
        {
            ApplyCardResizePreview(placements);
        }
        else
        {
            ResetCardDropPreview();
        }
    }

    private void CardResizeFrame_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_cardResizePointerId != e.Pointer.PointerId)
        {
            return;
        }

        string? instanceId = _cardResizeId;
        CardSize target = _cardResizeSize;
        CardPlacement? start = _cardResizeStartPlacement;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        ResetCardResize();
        ResetCardDropPreview();
        if (instanceId is not null &&
            start is CardPlacement placement &&
            target != placement.Size &&
            !_isSavingLayout &&
            _cardEdit.TryResizeCard(instanceId, target))
        {
            StatusText.Text = _resources.GetString("CardResizedStatus");
        }

        e.Handled = true;
    }

    private void CardResizeFrame_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_cardResizePointerId != e.Pointer.PointerId)
        {
            return;
        }

        // Capture can be taken away mid-gesture; the size the user was aiming at was never
        // committed, so the board goes back to what it actually holds.
        ResetCardResize();
        ResetCardDropPreview();
    }

    private void CardResizeFrame_PointerCanceled(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_cardResizePointerId != e.Pointer.PointerId)
        {
            return;
        }

        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        ResetCardResize();
        ResetCardDropPreview();
    }

    /// <summary>
    /// Whether a point inside the frame is on the corner handle. The frame only takes pointer
    /// input there, so this is normally confirming what already happened; it still answers
    /// honestly for the rest of the frame, because the same reading drives the hover cursor.
    /// </summary>
    private static CardResizeDirection ResolveResizeDirection(
        FrameworkElement frame,
        Windows.Foundation.Point local)
    {
        return local.X >= frame.ActualWidth - CardResizeHandleBand &&
            local.Y >= frame.ActualHeight - CardResizeHandleBand
            ? CardResizeDirection.SouthEast
            : CardResizeDirection.None;
    }

    private void CardResizeFrame_PointerExited(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_cardResizePointerId is null && sender is CardResizeFrame frame)
        {
            frame.ShowResizeCursor(CardResizeDirection.None);
        }
    }

    private void ApplyCardResizePreview(IReadOnlyList<CardPlacement> placements)
    {
        if (_cardSurface.ApplyResizePreview(placements))
        {
            _cardGridLayout.InvalidatePlacements();
        }
    }

    /// <summary>
    /// Draws the rubber band at the card's origin with the size the pointer is asking for.
    /// The floor is a third of the card it started from, so dragging far past the smallest
    /// size stops shrinking the band instead of turning it inside out.
    /// </summary>
    private const double CardResizeGhostCornerSize = 20;

    private void ShowCardResizeGhost(double width, double height)
    {
        // The floor keeps the band a band. Dragging far past the smallest card would otherwise
        // take it through zero and turn it inside out.
        double minimum = 48;
        double bandWidth = Math.Max(minimum, width);
        double bandHeight = Math.Max(minimum, height);
        double left = _cardResizeGhostOrigin.X;
        double top = _cardResizeGhostOrigin.Y;

        Canvas.SetLeft(CardResizeGhost, left);
        Canvas.SetTop(CardResizeGhost, top);
        CardResizeGhost.Width = bandWidth;
        CardResizeGhost.Height = bandHeight;

        // The handle appears to come off the card and travel with the pointer. It is the part
        // of the band the eye is already following, so it is the one that has to keep up.
        Canvas.SetLeft(
            CardResizeGhostCorner,
            left + bandWidth - (CardResizeGhostCornerSize / 2));
        Canvas.SetTop(
            CardResizeGhostCorner,
            top + bandHeight - (CardResizeGhostCornerSize / 2));

        if (CardResizeGhostLayer.Visibility != Visibility.Visible)
        {
            CardResizeGhostLayer.Visibility = Visibility.Visible;
            FadeCardResizeGhost(1);
        }
    }

    /// <summary>
    /// Takes the band off screen. It fades rather than vanishing: the release already moves
    /// the card underneath it, and two things changing in the same frame reads as a flicker.
    /// The element is collapsed when the fade lands, so nothing is left composing.
    /// </summary>
    private void HideCardResizeGhost()
    {
        if (CardResizeGhostLayer.Visibility != Visibility.Visible)
        {
            return;
        }

        if (_reducedMotion)
        {
            CardResizeGhostLayer.Visibility = Visibility.Collapsed;
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(CardResizeGhostLayer);
        CompositionScopedBatch batch = visual.Compositor.CreateScopedBatch(
            CompositionBatchTypes.Animation);
        FadeCardResizeGhost(0);
        batch.Completed += (_, _) =>
        {
            // Another gesture may have started while this one was fading out; it owns the
            // band now, so the late completion must not pull it off screen.
            if (_cardResizePointerId is null)
            {
                CardResizeGhostLayer.Visibility = Visibility.Collapsed;
            }
        };
        batch.End();
    }

    /// <summary>
    /// Compositor-only, and short. The band itself never animates its position or size - a
    /// resize is direct manipulation and must track the pointer with no added latency - so
    /// the only thing that moves under an animation here is its opacity.
    /// </summary>
    private void FadeCardResizeGhost(float target)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(CardResizeGhostLayer);
        if (_reducedMotion)
        {
            visual.Opacity = target;
            return;
        }

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(target > 0 ? 90 : 120);
        animation.InsertKeyFrame(
            1,
            target,
            visual.Compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.23f, 1),
                new Vector2(0.32f, 1)));
        visual.StartAnimation(nameof(Visual.Opacity), animation);
    }

    private void ResetCardResize()
    {
        HideCardResizeGhost();
        _cardResizePointerId = null;
        _cardResizeFrame = null;
        _cardResizeId = null;
        _cardResizeStartPlacement = null;
    }

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

    // The resize gesture. It is deliberately separate from the move drag's state: the two
    // cannot run at once, and sharing one set of fields would make "which gesture is this
    // pointer in" a question the handlers have to answer instead of a fact they hold.
    private uint? _cardResizePointerId;
    private FrameworkElement? _cardResizeFrame;
    private string? _cardResizeId;
    private CardPlacement? _cardResizeStartPlacement;
    private DragPoint _cardResizePressPoint;
    // Where the card started and how big it was, in the grid's coordinate space. The rubber
    // band is drawn from these rather than from the card's live bounds: the board reflows
    // underneath while the gesture runs, and a band that re-read those bounds would chase the
    // snapping instead of following the pointer.
    private Windows.Foundation.Point _cardResizeGhostOrigin;
    private double _cardResizeGhostWidth;
    private double _cardResizeGhostHeight;
    private CardSize _cardResizeSize;

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
