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
using UiDispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace WinWidgetBoard.WorkspacePanel;

public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private const string PersistedLayoutId = "primary-default";
    private const string PersistedDisplayId = "primary";
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
    private readonly ILayoutClient? _layoutClient;
    private readonly IWeatherSettingsClient? _weatherSettingsClient;
    private readonly CardSnapshotDispatcher _cardSnapshotDispatcher;
    private readonly CardSnapshotSubscriptionCoordinator?
        _cardSnapshotSubscription;
    private readonly CancellationTokenSource _cardSnapshotCancellation = new();
    private int _statusVersion;
    private readonly UiDispatcherQueueTimer _cardSubscriptionRefreshTimer;
    private readonly ResourceLoader _resources = new();
    private readonly CardDragController _demoNotesCardDrag = new();
    private readonly CardReturnMotionController _demoNotesCardReturnMotion;
    private readonly PanelMotionController _panelMotion;
    private readonly UiDispatcherQueue _uiDispatcherQueue;
    private readonly UiDispatcherQueueTimer _motionTimer;
    private readonly CardGridLayout _cardGridLayout;
    private readonly CardLayoutViewModel _cardLayout;
    private readonly CardLayoutEditViewModel _cardEdit;
    private readonly CardLayoutSurfaceViewModel _cardSurface;
    private readonly Dictionary<UIElement, CardRuntimeInstance>
        _realizedCardRuntimes = [];
    private readonly bool _keepOpenForAcceptance;
    private bool _cardItemsBound;
    private bool _nativeOpacitySupported;
    private int _modalScopeDepth;
    private bool _hasBeenActivated;
    private bool _closeWhenMotionSettles;
    private bool _allowNativeClose;
    private uint? _demoNotesCardPointerId;
    private CompositeTransform? _demoNotesCardTransform;
    private string? _draggedCardId;
    private CardPlacement? _dragStartPlacement;
    private int _layoutRevision;
    private bool _isSavingLayout;
    private bool _suppressNoteSearchTextChanged;
    private int _disposed;
    private bool _cardSubscriptionInitialized;
    private long _lastMotionTimestamp;

    public MainWindow(
        INoteClient? noteClient = null,
        ILayoutClient? layoutClient = null,
        bool keepOpenForAcceptance = false,
        CoreBrokerCardsClient? cardsClient = null,
        IWeatherSettingsClient? weatherSettingsClient = null)
    {
        _layoutClient = layoutClient;
        _weatherSettingsClient = weatherSettingsClient;
        _keepOpenForAcceptance = keepOpenForAcceptance;
        _uiDispatcherQueue = UiDispatcherQueue.GetForCurrentThread();
        _cardSnapshotDispatcher = new CardSnapshotDispatcher(
            DispatchSnapshotToUiAsync);
        _cardSnapshotSubscription = cardsClient is null
            ? null
            : new CardSnapshotSubscriptionCoordinator(
                cardsClient,
                _cardSnapshotDispatcher);
        NoteEditor = new NoteEditorViewModel(
            noteClient,
            dispatch: DispatchToUi);
        NoteSearch = new NoteSearchViewModel(
            noteClient,
            dispatch: DispatchToUi);
        InitializeComponent();
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
            ]);
        _cardEdit = new CardLayoutEditViewModel(_cardLayout);
        _cardSurface = new CardLayoutSurfaceViewModel(
            _cardEdit,
            NoteEditor,
            FormatNoteStatus,
            runtimeResourceResolver: key => _resources.GetString(
                key.Replace('.', '/')));
        _cardGridLayout = new CardGridLayout
        {
            ColumnCount = _cardLayout.ColumnCount,
        };
        CardItemsRepeater.Layout = _cardGridLayout;
        _cardLayout.PropertyChanged += CardLayout_PropertyChanged;
        _cardSurface.PropertyChanged += CardSurface_PropertyChanged;
        _cardEdit.PropertyChanged += CardEdit_PropertyChanged;

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
        bool reducedMotion = !new UISettings().AnimationsEnabled;
        _panelMotion = new PanelMotionController(
            transformOrigin.X < 0.5 ? -14 : 14,
            transformOrigin.Y < 0.5 ? -14 : 14,
            reducedMotion);
        _demoNotesCardReturnMotion = new CardReturnMotionController(reducedMotion);
        _motionTimer = Microsoft.UI.Dispatching.DispatcherQueue
            .GetForCurrentThread()
            .CreateTimer();
        _motionTimer.Interval = TimeSpan.FromMilliseconds(16);
        _motionTimer.Tick += PanelMotionTimer_Tick;
        _cardSubscriptionRefreshTimer = _uiDispatcherQueue.CreateTimer();
        _cardSubscriptionRefreshTimer.Interval = TimeSpan.FromMilliseconds(100);
        _cardSubscriptionRefreshTimer.Tick += CardSubscriptionRefreshTimer_Tick;
        _appWindow.Closing += AppWindow_Closing;

        ApplyPlacement();
        ApplyPanelMotion(_panelMotion.Value);
        DateText.Text = DateTime.Now.ToString("D", CultureInfo.CurrentCulture);
        ContextText.Text =
            $"{_placement.WindowRect.Width} × {_placement.WindowRect.Height} px · " +
            $"DPI {_placement.Dpi}";

        SynchronizeCardSnapshotRuntimes();
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
        _nativeOpacitySupported = NativeWindowStyles.EnableLayeredOpacity(_windowHandle);
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
        if (_panelMotion.State == PanelMotionState.Closing)
        {
            RequestOpenMotion();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _motionTimer.Stop();
        _cardSubscriptionRefreshTimer.Stop();
        _cardSnapshotCancellation.Cancel();
        _cardSurface.SetPanelVisibility(false);
        _realizedCardRuntimes.Clear();
        _appWindow.Closing -= AppWindow_Closing;
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

        _cardSubscriptionRefreshTimer.Stop();
        _cardSnapshotCancellation.Cancel();
        _cardSurface.SetPanelVisibility(false);
        _realizedCardRuntimes.Clear();
        _cardLayout.PropertyChanged -= CardLayout_PropertyChanged;
        _cardSurface.PropertyChanged -= CardSurface_PropertyChanged;
        _cardEdit.PropertyChanged -= CardEdit_PropertyChanged;
        _cardSubscriptionRefreshTimer.Tick -= CardSubscriptionRefreshTimer_Tick;
        CardItemsRepeater.ElementPrepared -= CardItemsRepeater_ElementPrepared;
        CardItemsRepeater.ElementClearing -= CardItemsRepeater_ElementClearing;
        if (_cardSnapshotSubscription is not null)
        {
            await _cardSnapshotSubscription.DisposeAsync();
        }

        await _cardSurface.DisposeAsync();
        await NoteEditor.DisposeAsync();
        await NoteSearch.DisposeAsync();
        _cardSnapshotCancellation.Dispose();
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

    private void ShellCommandButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = _resources.GetString("ShellPlaceholderStatus");
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
            : _resources.GetString("EditLayoutButton.Content");
        string automationName = _cardEdit.IsEditing
            ? _resources.GetString("FinishEditLayoutButtonAutomationName")
            : _resources.GetString("EditLayoutButton.Content");
        EditLayoutButton.Content = content;
        AutomationProperties.SetName(EditLayoutButton, automationName);
        UpdateLayoutHistoryButtons();
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
        UndoLayoutButton.Visibility = visibility;
        RedoLayoutButton.Visibility = visibility;
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
            sender is not FrameworkElement element ||
            element.Tag is not string instanceId ||
            !_cardLayout.TryGetPlacement(instanceId, out CardPlacement placement))
        {
            return;
        }

        int currentIndex = Array.IndexOf(EditableCardSizes, placement.Size);
        int nextIndex = currentIndex + direction;
        if (currentIndex < 0 ||
            nextIndex < 0 ||
            nextIndex >= EditableCardSizes.Length ||
            !_cardEdit.TryResizeCard(instanceId, EditableCardSizes[nextIndex]))
        {
            return;
        }

        StatusText.Text = _resources.GetString("CardResizedStatus");
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

    private async void NewNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!NoteEditor.CanLoadNote)
        {
            StatusText.Text = _resources.GetString("NoteCreateBlockedStatus");
            return;
        }

        NoteSearchResultsBorder.Visibility = Visibility.Collapsed;
        StatusText.Text = _resources.GetString("NoteCreatingStatus");
        bool created = await NoteEditor.CreateNoteAsync(CancellationToken.None);
        StatusText.Text = _resources.GetString(
            created ? "NoteCreatedStatus" : "NoteCreateFailedStatus");
    }

    private async void DeleteCurrentNoteButton_Click(
        object sender,
        RoutedEventArgs e)
    {
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

    private void OpenNotesButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        DependencyObject? cardRoot = source;
        while (cardRoot is not null && cardRoot is not Border)
        {
            cardRoot = VisualTreeHelper.GetParent(cardRoot);
        }

        if (NoteEditor.IsMarkdownPreviewVisible)
        {
            NoteEditor.ToggleMarkdownPreview();
        }

        FindDescendant<TextBox>(cardRoot, "NoteBodyBox")?.Focus(
            FocusState.Programmatic);
    }

    private void CopyNoteButton_Click(object sender, RoutedEventArgs e)
    {
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

        NoteSearchResultsBorder.Visibility = Visibility.Collapsed;
        StatusText.Text = _resources.GetString("NoteListLoadingStatus");
        bool completed = await NoteSearch.LoadAllAsync(CancellationToken.None);
        if (!completed || NoteSearch.Query.Length != 0)
        {
            return;
        }

        NoteSearchResultsBorder.Visibility = NoteSearch.HasResults
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusText.Text = NoteSearch.Status switch
        {
            NoteSearchStatus.Ready => string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("NoteListResultsStatus"),
                NoteSearch.Results.Count),
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
            await NoteSearch.SearchAsync(query, CancellationToken.None);
            NoteSearchResultsBorder.Visibility = Visibility.Collapsed;
            StatusText.Text = _resources.GetString("NoteSearchClearedStatus");
            return;
        }

        NoteSearchResultsBorder.Visibility = Visibility.Collapsed;
        StatusText.Text = _resources.GetString("NoteSearchSearchingStatus");
        bool completed = await NoteSearch.SearchAsync(query, CancellationToken.None);
        if (!completed || !string.Equals(
                NoteSearch.Query,
                query,
                StringComparison.Ordinal))
        {
            return;
        }

        NoteSearchResultsBorder.Visibility = NoteSearch.HasResults
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusText.Text = NoteSearch.Status switch
        {
            NoteSearchStatus.Ready => string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("NoteSearchResultsStatus"),
                NoteSearch.Results.Count),
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

        bool loaded = await NoteEditor.LoadNoteAsync(
            noteId,
            CancellationToken.None);
        StatusText.Text = _resources.GetString(
            loaded ? "NoteLoadedStatus" : "NoteLoadFailedStatus");
    }

    private async Task<bool> RefreshNoteResultsAfterDeleteAsync()
    {
        string query = NoteSearch.Query;
        bool completed = query.Length == 0
            ? await NoteSearch.LoadAllAsync(CancellationToken.None)
            : await NoteSearch.SearchAsync(query, CancellationToken.None);
        if (!completed || !string.Equals(
                NoteSearch.Query,
                query,
                StringComparison.Ordinal))
        {
            return false;
        }

        NoteSearchResultsBorder.Visibility = NoteSearch.HasResults
            ? Visibility.Visible
            : Visibility.Collapsed;
        return NoteSearch.Status is NoteSearchStatus.Ready or NoteSearchStatus.Empty;
    }

    private async Task LoadFirstListedNoteAsync()
    {
        NoteSearchResult? first = NoteSearch.Results.Count == 0
            ? null
            : NoteSearch.Results[0];
        if (first is not null && NoteEditor.CanLoadNote)
        {
            await NoteEditor.LoadNoteAsync(first.NoteId, CancellationToken.None);
        }
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
        if (_cardSnapshotSubscription is null)
        {
            return false;
        }

        return await RefreshCardSubscriptionAsync(
            markInitialized: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public void HandleBrokerReconnected(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !_cardSubscriptionInitialized)
        {
            return;
        }

        if (_uiDispatcherQueue.HasThreadAccess)
        {
            RequestCardSubscriptionRefresh();
            return;
        }

        _uiDispatcherQueue.TryEnqueue(RequestCardSubscriptionRefresh);
    }

    private async Task<bool> RefreshCardSubscriptionAsync(
        bool markInitialized,
        CancellationToken cancellationToken)
    {
        if (_cardSnapshotSubscription is null ||
            Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            CardSubscriptionState state =
                await CaptureCardSubscriptionStateAsync(cancellationToken)
                    .ConfigureAwait(false);
            await _cardSnapshotSubscription
                .SubscribeAsync(
                    state.Runtimes,
                    state.PanelVisible,
                    state.VisibleInstanceIds,
                    cancellationToken)
                .ConfigureAwait(false);
            _cardSubscriptionInitialized = true;
            return true;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            _cardSnapshotCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
            when (exception is CoreBrokerClientException or
                IOException or
                InvalidOperationException or
                TimeoutException)
        {
            Debug.WriteLine(
                $"WorkspacePanel card subscription failed: {exception.Message}");
            if (markInitialized)
            {
                _cardSubscriptionInitialized = false;
            }

            return false;
        }
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
        if (_cardSnapshotSubscription is null ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _cardSnapshotSubscription.SynchronizeRuntimes(
            _cardSurface.Items
                .Select(item => item.Runtime)
                .ToArray());
    }

    private void RequestCardSubscriptionRefresh()
    {
        if (_cardSnapshotSubscription is null ||
            !_cardSubscriptionInitialized ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _cardSubscriptionRefreshTimer.Start();
    }

    private async void CardSubscriptionRefreshTimer_Tick(
        UiDispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (Volatile.Read(ref _disposed) == 0)
        {
            await RefreshCardSubscriptionAsync(
                markInitialized: false,
                cancellationToken: _cardSnapshotCancellation.Token).ConfigureAwait(false);
        }
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

    private sealed record CardSubscriptionState(
        CardRuntimeInstance[] Runtimes,
        bool PanelVisible,
        string[] VisibleInstanceIds);

    public void MarkNoteEditorUnavailable(string errorCode = "transport.unavailable") =>
        NoteEditor.MarkUnavailable(errorCode);

    public async Task<bool> InitializeLayoutAsync(CancellationToken cancellationToken)
    {
        int statusVersion = Volatile.Read(ref _statusVersion);
        if (_layoutClient is null)
        {
            MarkLayoutUnavailable();
            return false;
        }

        try
        {
            LayoutDto? persisted = await _layoutClient
                .GetLayoutAsync(
                    PersistedLayoutId,
                    PersistedDisplayId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (persisted is null)
            {
                await RunOnUiAsync(() =>
                {
                    EnsureCardItemsBound();
                    if (Volatile.Read(ref _statusVersion) == statusVersion)
                    {
                        StatusText.Text = _resources.GetString("LayoutReadyToSaveStatus");
                    }
                }).ConfigureAwait(false);
                return true;
            }

            var loadedItems = new List<CardLayoutItem>(persisted.Items.Count);
            var loadedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (LayoutItemDto item in persisted.Items.OrderBy(item => item.Order))
            {
                if (!TryParseCardSize(item.SizeId, out CardSize size) ||
                    !loadedIds.Add(item.InstanceId))
                {
                    throw new InvalidDataException("The persisted layout contains an invalid card item.");
                }

                loadedItems.Add(new CardLayoutItem(
                    item.InstanceId,
                    size,
                    item.PreferredColumn,
                    item.PreferredRow));
            }

            await RunOnUiAsync(() =>
            {
                CardLayoutReplayResult replay =
                    CardLayoutReplaySanitizer.Normalize(
                        _cardLayout.ColumnCount,
                        loadedItems);
                _layoutRevision = persisted.Revision;
                _cardLayout.ReplaceItems(replay.Items);
                EnsureCardItemsBound();
                if (Volatile.Read(ref _statusVersion) == statusVersion)
                {
                    StatusText.Text = _resources.GetString(
                        replay.RecoveredItemCount > 0
                            ? "LayoutRecoveredStatus"
                            : "LayoutLoadedStatus");
                }

                if (replay.RecoveredItemCount > 0)
                {
                    Debug.WriteLine(
                        $"Recovered {replay.RecoveredItemCount} layout item(s) " +
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
        if (_layoutClient is null)
        {
            MarkLayoutUnavailable();
            return false;
        }

        LayoutItemDto[] items = _cardLayout.GetItemsInPlacementOrder()
            .Select((item, index) =>
            {
                CardSpan span = ResponsiveGridLayout.GetSpan(item.Size);
                if (!_cardLayout.TryGetPlacement(item.InstanceId, out CardPlacement placement))
                {
                    throw new InvalidOperationException(
                        $"The card '{item.InstanceId}' has no current placement.");
                }

                return new LayoutItemDto
                {
                    InstanceId = item.InstanceId,
                    Order = index,
                    ColumnSpan = span.Columns,
                    RowSpan = span.Rows,
                    SizeId = ToLayoutSizeId(item.Size),
                    PreferredColumn = placement.Column,
                    PreferredRow = placement.Row,
                };
            })
            .ToArray();

        try
        {
            LayoutDto saved = await _layoutClient
                .SaveLayoutAsync(
                    new LayoutSaveRequest
                    {
                        ClientOperationId = Guid.NewGuid(),
                        LayoutId = PersistedLayoutId,
                        DisplayId = PersistedDisplayId,
                        ExpectedRevision = _layoutRevision,
                        Items = items,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await RunOnUiAsync(() =>
            {
                _layoutRevision = saved.Revision;
            }).ConfigureAwait(false);
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

            // ItemsRepeater keeps a realized DataTemplate by index when an
            // observable source is reordered. Recycle through a null source so
            // timer/calendar/etc. templates are selected for their new items.
            // Placement-only changes never enter this path.
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

        _realizedCardRuntimes[args.Element] = item.Runtime;
        _cardSurface.SetViewportVisibility(item.Runtime, true);
        RequestCardSubscriptionRefresh();
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

    private static string ToLayoutSizeId(CardSize size) => size switch
    {
        CardSize.S => "s",
        CardSize.M => "m",
        CardSize.L => "l",
        CardSize.W => "w",
        CardSize.XL => "xl",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null),
    };

    private static bool TryParseCardSize(string? sizeId, out CardSize size)
    {
        size = sizeId switch
        {
            "s" => CardSize.S,
            "m" => CardSize.M,
            "l" => CardSize.L,
            "w" => CardSize.W,
            "xl" => CardSize.XL,
            _ => default,
        };
        return sizeId is "s" or "m" or "l" or "w" or "xl";
    }

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
        _closeWhenMotionSettles = false;
        _panelMotion.RequestOpen();
        ApplyPanelMotion(_panelMotion.Value);
        StartMotionTimer();
    }

    private void RequestCloseMotion()
    {
        if (_modalScopeDepth != 0)
        {
            return;
        }

        _cardSurface.SetPanelVisibility(false);
        RequestCardSubscriptionRefresh();
        _closeWhenMotionSettles = true;
        _panelMotion.RequestClose();
        ApplyPanelMotion(_panelMotion.Value);
        if (_panelMotion.State == PanelMotionState.Closed)
        {
            CloseAfterMotion();
            return;
        }

        StartMotionTimer();
    }

    private void StartMotionTimer()
    {
        if (!_panelMotion.IsAnimating && !_demoNotesCardReturnMotion.IsAnimating)
        {
            return;
        }

        _lastMotionTimestamp = Stopwatch.GetTimestamp();
        _motionTimer.Start();
    }

    private void PanelMotionTimer_Tick(UiDispatcherQueueTimer sender, object args)
    {
        long timestamp = Stopwatch.GetTimestamp();
        double seconds = (timestamp - _lastMotionTimestamp) /
            (double)Stopwatch.Frequency;
        _lastMotionTimestamp = timestamp;

        TimeSpan elapsed = TimeSpan.FromSeconds(seconds);
        if (_panelMotion.IsAnimating)
        {
            ApplyPanelMotion(_panelMotion.Step(elapsed));
        }

        if (_demoNotesCardReturnMotion.IsAnimating)
        {
            DragOffset returnOffset = _demoNotesCardReturnMotion.Step(elapsed);
            ApplyDemoNotesCardReturn(returnOffset);
            if (!_demoNotesCardReturnMotion.IsAnimating &&
                !_demoNotesCardDrag.IsActive)
            {
                _demoNotesCardDrag.SetOffset(returnOffset);
            }
        }

        if (_closeWhenMotionSettles &&
            !_panelMotion.IsAnimating &&
            _panelMotion.State == PanelMotionState.Closed)
        {
            _motionTimer.Stop();
            CloseAfterMotion();
            return;
        }

        if (_panelMotion.IsAnimating || _demoNotesCardReturnMotion.IsAnimating)
        {
            return;
        }

        _motionTimer.Stop();
    }

    private void CloseAfterMotion()
    {
        _closeWhenMotionSettles = false;
        _allowNativeClose = true;
        Close();
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
            surface.Tag is not string instanceId ||
            !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed ||
            IsInteractiveCardContent(e.OriginalSource as DependencyObject, surface) ||
            !_cardLayout.TryGetPlacement(instanceId, out CardPlacement placement))
        {
            return;
        }

        InterruptDemoNotesCardReturn();
        ResetCardDropPreview();
        _demoNotesCardTransform = FindNotesCardTransform(surface);
        _draggedCardId = instanceId;
        _dragStartPlacement = placement;
        _demoNotesCardPointerId = e.Pointer.PointerId;
        CardDragUpdate update = _demoNotesCardDrag.Press(GetRootPointer(e));
        if (!surface.CapturePointer(e.Pointer))
        {
            _demoNotesCardPointerId = null;
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
        _demoNotesCardReturnMotion.Start(currentOffset, targetOffset);
        ApplyDemoNotesCardReturn(_demoNotesCardReturnMotion.Value);
        if (!_demoNotesCardReturnMotion.IsAnimating &&
            !_demoNotesCardDrag.IsActive)
        {
            _demoNotesCardDrag.SetOffset(_demoNotesCardReturnMotion.Value);
        }
        StartMotionTimer();
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

        double scale = dragging ? 1.02 : 1.0;
        _demoNotesCardTransform.ScaleX = scale;
        _demoNotesCardTransform.ScaleY = scale;
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
        if (transform is not null)
        {
            transform.TranslateX = 0;
            transform.TranslateY = 0;
            transform.ScaleX = 1;
            transform.ScaleY = 1;
        }

        if (!_demoNotesCardDrag.IsActive)
        {
            _demoNotesCardDrag.SetOffset(DragOffset.Zero);
        }

        if (_demoNotesCardReturnMotion.IsAnimating)
        {
            _demoNotesCardReturnMotion.StopAt(DragOffset.Zero);
        }
    }

    private void InterruptDemoNotesCardReturn()
    {
        if (!_demoNotesCardReturnMotion.IsAnimating)
        {
            return;
        }

        DragOffset currentOffset = _demoNotesCardReturnMotion.Value;
        _demoNotesCardReturnMotion.StopAt(currentOffset);
        _demoNotesCardDrag.SetOffset(currentOffset);
        ApplyDemoNotesCardReturn(currentOffset);
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

    private static T? FindDescendant<T>(
        DependencyObject? root,
        string name)
        where T : FrameworkElement
    {
        if (root is null)
        {
            return null;
        }

        if (root is T element && element.Name == name)
        {
            return element;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            T? match = FindDescendant<T>(
                VisualTreeHelper.GetChild(root, index),
                name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
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

            _owner._modalScopeDepth = Math.Max(0, _owner._modalScopeDepth - 1);
            _owner = null;
        }
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
