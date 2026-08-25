using System.Collections.ObjectModel;
using System.ComponentModel;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Layout;

public sealed class CardSurfaceItem : INotifyPropertyChanged, IDisposable
{
    private readonly CardLayoutEditViewModel _editMode;
    private readonly Func<NoteEditorStatus, string> _statusFormatter;
    private readonly Func<string, string?> _runtimeResourceResolver;
    // Snapshots reach this type from two directions: broker pushes arrive already marshalled
    // onto the UI dispatcher, but the local visibility scheduler completes on whichever thread
    // ran the tick. Only the metric list is a collection, and mutating one off-thread is what
    // WinUI actually refuses, so the merge - and nothing else - goes through here.
    private readonly Action<Action> _uiInvoker;
    private readonly CardRuntimeVisibilityScheduler? _visibilityScheduler;
    private readonly CardRuntimeRegistration? _visibilityRegistration;
    private CardRuntimeStatusPresentation _runtimePresentation;
    private WeatherCardProjection _weatherProjection;
    private SystemMonitorCardProjection _systemMonitorProjection;
    private bool _disposed;

    public CardSurfaceItem(
        CardPlacement placement,
        NoteEditorViewModel noteEditor,
        CardLayoutEditViewModel editMode,
        Func<NoteEditorStatus, string>? statusFormatter = null,
        CardRuntimeVisibilityScheduler? visibilityScheduler = null,
        Func<string, string?>? runtimeResourceResolver = null,
        Action<Action>? uiInvoker = null)
    {
        ArgumentNullException.ThrowIfNull(noteEditor);
        ArgumentNullException.ThrowIfNull(editMode);
        Placement = placement;
        NoteEditor = noteEditor;
        _editMode = editMode;
        _statusFormatter = statusFormatter ?? (status => status.ToString());
        _runtimeResourceResolver = runtimeResourceResolver ??
            (static key => key);
        _uiInvoker = uiInvoker ?? (static action => action());
        _visibilityScheduler = visibilityScheduler;
        Runtime = BuiltInCardRuntimeFactory.Create(
            placement.InstanceId,
            noteEditor);
        _runtimePresentation = CardRuntimeStatusPresentation.Create(
            Runtime.Snapshot,
            _runtimeResourceResolver);
        _weatherProjection = WeatherCardProjection.FromSnapshot(
            Runtime.Snapshot);
        _systemMonitorProjection = SystemMonitorCardProjection.FromSnapshot(
            Runtime.Snapshot,
            _runtimeResourceResolver);
        SystemMonitorMetrics = [];
        SystemMonitorMetricListMerger.Merge(
            SystemMonitorMetrics,
            _systemMonitorProjection.Metrics);
        _visibilityRegistration = visibilityScheduler?.Register(
            Runtime,
            RefreshSnapshotAsync);
        NoteEditor.PropertyChanged += NoteEditor_PropertyChanged;
        _editMode.PropertyChanged += EditMode_PropertyChanged;
        Runtime.PropertyChanged += Runtime_PropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CardPlacement Placement { get; private set; }

    public string InstanceId => Placement.InstanceId;

    public NoteEditorViewModel NoteEditor { get; }

    public CardRuntimeInstance Runtime { get; }

    public string CardTypeId => Runtime.Definition.CardTypeId;

    public CardRuntimeSnapshot RuntimeSnapshot => Runtime.Snapshot;

    public CardRuntimeStatus RuntimeStatus => RuntimeSnapshot.Status;

    public CardRuntimeStatusPresentation RuntimePresentation =>
        Volatile.Read(ref _runtimePresentation);

    public WeatherCardProjection WeatherProjection =>
        Volatile.Read(ref _weatherProjection);

    public string WeatherLocationLabel => WeatherProjection.LocationLabel;

    public string WeatherTemperatureText => WeatherProjection.TemperatureText;

    public string WeatherApparentTemperatureText =>
        WeatherProjection.ApparentTemperatureText;

    public string WeatherConditionText => WeatherProjection.ConditionText;

    public string WeatherConditionIconId => WeatherProjection.ConditionIconId;

    public string WeatherHumidityText => WeatherProjection.HumidityText;

    public string WeatherWindText => WeatherProjection.WindText;

    public string WeatherObservedAtText => WeatherProjection.ObservedAtText;

    public string WeatherAttributionText => WeatherProjection.AttributionText;

    public bool HasWeatherData => WeatherProjection.HasData;

    public IReadOnlyList<WeatherHourProjection> WeatherHours => WeatherProjection.Hours;

    public IReadOnlyList<WeatherDayProjection> WeatherDays => WeatherProjection.Days;

    /// <summary>
    /// The forecast is disclosed by card size, not merely by whether it arrived. A card is a
    /// fixed number of grid rows tall, so content that does not fit is clipped rather than
    /// scrolled - showing the trend on a card that cannot hold it produced a row of times with
    /// their glyphs and temperatures cut off, which is worse than not showing it.
    /// </summary>
    /// Gated on rows rather than on the size order: W is four columns but only one row, so
    /// it is exactly as short as M and clips the same way.
    public bool HasWeatherHours =>
        WeatherProjection.HasHours &&
        Placement.Size is CardSize.L or CardSize.XL;

    /// Three more rows on top of the trend, each needing room for a weekday, a glyph and two
    /// temperatures - that is the widest and tallest size only.
    public bool HasWeatherDays =>
        WeatherProjection.HasDays && Placement.Size is CardSize.XL;

    /// <summary>
    /// False before the first reading names a place. The card leaves the location line out
    /// rather than showing a placeholder where a city name belongs.
    /// </summary>
    public bool HasWeatherLocation => WeatherProjection.LocationLabel.Length > 0;

    public SystemMonitorCardProjection SystemMonitorProjection =>
        Volatile.Read(ref _systemMonitorProjection);

    /// <summary>
    /// A stable collection updated in place. Replacing it every tick would rebuild each row's
    /// visuals twice a second on a card whose entire purpose is to sit still and change
    /// numbers.
    /// </summary>
    public ObservableCollection<SystemMonitorMetricViewModel> SystemMonitorMetrics { get; }

    public bool HasSystemMonitorData => SystemMonitorProjection.HasData;

    public string WeatherAutomationSummary =>
        string.Join(
            " · ",
            RuntimePresentation.AutomationSummary,
            WeatherTemperatureText);

    public bool SetViewportVisibility(bool isInViewport) =>
        _visibilityScheduler?.SetViewportVisibility(Runtime, isInViewport)
            ?? false;

    public bool IsEditing => _editMode.IsEditing;

    public string NoteStatusText => _statusFormatter(NoteEditor.Status);

    internal bool UpdatePlacement(CardPlacement placement)
    {
        if (!string.Equals(
                placement.InstanceId,
                InstanceId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A surface item can only accept a placement for the same card.",
                nameof(placement));
        }

        if (Placement == placement)
        {
            return false;
        }

        CardSize previousSize = Placement.Size;
        Placement = placement;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(Placement)));
        if (previousSize != placement.Size)
        {
            // Anything disclosed by card size has to be republished here. Resizing raises
            // Placement only, so a size-gated section would keep whatever visibility it had
            // when the snapshot last arrived - a card grown to fit the forecast would sit
            // there without one until the next refresh happened to land.
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherHours)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherDays)));
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _visibilityRegistration?.Dispose();
        NoteEditor.PropertyChanged -= NoteEditor_PropertyChanged;
        _editMode.PropertyChanged -= EditMode_PropertyChanged;
        Runtime.PropertyChanged -= Runtime_PropertyChanged;
        Runtime.Dispose();
    }

    private ValueTask<CardRuntimeSnapshot> RefreshSnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CardRuntimeSnapshot snapshot =
            BuiltInCardRuntimeFactory.CreateFreshSnapshot(
                Runtime,
                NoteEditor);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(snapshot);
    }

    private void NoteEditor_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteEditorViewModel.Status)
            or nameof(NoteEditorViewModel.ErrorCode))
        {
            if (string.Equals(
                    CardTypeId,
                    BuiltInCardCatalog.NotesCardTypeId,
                    StringComparison.Ordinal) &&
                (_visibilityScheduler is null ||
                    Runtime.LifecycleState ==
                        CardLifecycleState.Visible))
            {
                BuiltInCardRuntimeFactory.SynchronizeNote(
                    Runtime,
                    NoteEditor);
            }

            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(NoteStatusText)));
        }
    }

    private void EditMode_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardLayoutEditViewModel.IsEditing))
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    private void Runtime_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardRuntimeInstance.Snapshot))
        {
            CardRuntimeStatusPresentation presentation =
                CardRuntimeStatusPresentation.Create(
                    Runtime.Snapshot,
                    _runtimeResourceResolver);
            Interlocked.Exchange(ref _runtimePresentation, presentation);
            Interlocked.Exchange(
                ref _weatherProjection,
                WeatherCardProjection.FromSnapshot(Runtime.Snapshot));
            SystemMonitorCardProjection systemMonitor =
                SystemMonitorCardProjection.FromSnapshot(
                    Runtime.Snapshot,
                    _runtimeResourceResolver);
            Interlocked.Exchange(ref _systemMonitorProjection, systemMonitor);
            _uiInvoker(() => SystemMonitorMetricListMerger.Merge(
                SystemMonitorMetrics,
                systemMonitor.Metrics));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(RuntimeSnapshot)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(RuntimeStatus)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(RuntimePresentation)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherProjection)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherLocationLabel)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherTemperatureText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherApparentTemperatureText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherConditionText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherConditionIconId)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherHumidityText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherWindText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherObservedAtText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherAttributionText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherData)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherHours)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherDays)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherHours)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherDays)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherLocation)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherAutomationSummary)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SystemMonitorProjection)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasSystemMonitorData)));
        }
    }
}
