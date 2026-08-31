using System.Collections.ObjectModel;
using System.ComponentModel;
using WinWidgetBoard.Contracts.Protocol;
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
    /// <summary>
    /// Everything the token-usage card re-reads when its page or its data changes. Listed once
    /// rather than at each call site, because the page switch and the snapshot arrival have to
    /// notify exactly the same set or one path leaves a stale binding on screen.
    /// </summary>
    private static readonly string[] TokenUsageChangeNotifications =
    [
        nameof(TokenUsageProjection),
        nameof(TokenUsageCurrentPage),
        nameof(HasTokenUsageData),
        nameof(IsTokenUsageTrendVisible),
        nameof(TokenUsageCostText),
        nameof(IsTokenUsageCostVisible),
        nameof(IsTokenUsagePageSwitcherVisible),
        nameof(TokenUsageMetricLimit),
    ];

    private readonly CardRuntimeVisibilityScheduler? _visibilityScheduler;
    private readonly CardRuntimeRegistration? _visibilityRegistration;
    private CardRuntimeStatusPresentation _runtimePresentation;
    private WeatherCardProjection _weatherProjection;
    private SystemMonitorCardProjection _systemMonitorProjection;
    private TokenUsageCardProjection _tokenUsageProjection;
    // The chosen page is panel-local view state, deliberately not persisted: the card opens on
    // the overview every time rather than on whatever was last looked at.
    private string _tokenUsagePageId = TokenUsageContract.OverviewPageId;
    private bool _disposed;

    public CardSurfaceItem(
        CardPlacement placement,
        NoteEditorViewModel noteEditor,
        NoteSearchViewModel noteSearch,
        CardLayoutEditViewModel editMode,
        Func<NoteEditorStatus, string>? statusFormatter = null,
        CardRuntimeVisibilityScheduler? visibilityScheduler = null,
        Func<string, string?>? runtimeResourceResolver = null,
        Action<Action>? uiInvoker = null)
    {
        ArgumentNullException.ThrowIfNull(noteEditor);
        ArgumentNullException.ThrowIfNull(noteSearch);
        ArgumentNullException.ThrowIfNull(editMode);
        NoteSearch = noteSearch;
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
        ApplySystemMonitorMetricLimit();
        _tokenUsageProjection = TokenUsageCardProjection.FromSnapshot(
            Runtime.Snapshot,
            _runtimeResourceResolver);
        TokenUsagePages = [];
        TokenUsageMetrics = [];
        TokenUsageTrend = [];
        MergeTokenUsage(_tokenUsageProjection);
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

    /// <summary>
    /// The note list, for the card's own switcher. Browsing notes used to live in the panel
    /// header, above every card including the ones it had nothing to do with; it belongs to
    /// the card that owns notes.
    /// </summary>
    public NoteSearchViewModel NoteSearch { get; }

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

    public string WeatherHighTemperatureText =>
        WeatherProjection.HighTemperatureText;

    public string WeatherLowTemperatureText =>
        WeatherProjection.LowTemperatureText;

    /// <summary>
    /// Today's range earns its line on every size. It is the one secondary reading that
    /// changes what the reader does next - a 32-degree afternoon that drops to 18 tonight is
    /// a different day from one that stays at 30 - and it is a single short line, so even the
    /// one-cell card can carry it under the condition.
    /// </summary>
    public bool HasWeatherHighLow => WeatherProjection.HasHighLow;

    /// <summary>
    /// The condition, localized. The projection is WinUI-free and names the string rather
    /// than resolving it; the English description it also carries is the fallback for a code
    /// that has no entry, so the line degrades to "WMO 82" rather than to blank.
    /// </summary>
    public string WeatherConditionText
    {
        get
        {
            WeatherCardProjection projection = WeatherProjection;
            if (projection.ConditionResourceKey.Length == 0)
            {
                return projection.ConditionText;
            }

            string? localized = _runtimeResourceResolver(
                projection.ConditionResourceKey);
            return string.IsNullOrEmpty(localized)
                ? projection.ConditionText
                : localized;
        }
    }

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
    /// Gated on rows and columns, not on the size order. W is four columns but only one row,
    /// so it is exactly as short as M - it earns the trend by putting it beside the reading
    /// rather than under it, which is what <see cref="IsWeatherWideLayout"/> arranges.
    public bool HasWeatherHours =>
        WeatherProjection.HasHours &&
        Placement.Size is CardSize.L or CardSize.W or CardSize.XL;

    /// Three more rows under the trend, each needing room for a weekday, a glyph and two
    /// temperatures. That needs a second grid row, which rules out S, M and W.
    public bool HasWeatherDays =>
        WeatherProjection.HasDays &&
        Placement.Size is CardSize.L or CardSize.XL;

    /// <summary>
    /// The secondary readings - felt temperature, humidity, wind. They need the second grid
    /// row, and the arithmetic says why: a one-row card has about 98 DIP of content, and the
    /// reading (40), the condition (20) and today's range (20) already spend 80 of it. These
    /// cost 34 more, so on a one-row card they were drawn through the card's own clip - which
    /// is what adding the range line did to the M card without re-checking the ladder it was
    /// joining.
    /// </summary>
    public bool HasWeatherSecondary =>
        HasWeatherData && Placement.Size is CardSize.L or CardSize.XL;

    /// <summary>
    /// Whether today's range names its two ends. One cell is about 170 DIP wide and
    /// "最高 28.9 °C  最低 16.6 °C" does not fit across it; the two values alone, separated by
    /// a slash, do. The labels come back as soon as the card is two cells wide.
    /// </summary>
    public bool IsWeatherHighLowLabelVisible =>
        HasWeatherHighLow && Placement.Size is not CardSize.S;

    /// <summary>The separator that stands in for the labels on the narrowest card.</summary>
    public bool IsWeatherHighLowSeparatorVisible =>
        HasWeatherHighLow && Placement.Size is CardSize.S;

    /// <summary>
    /// When the observation time and the attribution have a line to sit on. Both are context
    /// rather than content, so they are the first thing a smaller card drops.
    /// </summary>
    public bool HasWeatherFooter =>
        HasWeatherData && Placement.Size is CardSize.L or CardSize.XL;

    /// <summary>
    /// True for the four-column sizes, where the forecast goes beside the current reading
    /// instead of under it. W is four cells wide but one tall: stacking there clips the trend,
    /// and putting it in the empty right half is the only way it fits at all.
    /// </summary>
    public bool IsWeatherWideLayout =>
        Placement.Size is CardSize.W or CardSize.XL;

    /// <summary>The forecast's grid cell, so one template serves both arrangements.</summary>
    public int WeatherForecastColumn => IsWeatherWideLayout ? 1 : 0;

    public int WeatherForecastRow => IsWeatherWideLayout ? 0 : 1;

    /// <summary>
    /// Stacked, the forecast spans both columns. Without this it sits in the Auto column
    /// that sizes itself to the current reading, and the day rows - whose temperatures are
    /// in a star column - are measured against infinity and arranged past the card's clip.
    /// The weekday and its glyph showed; the high and the low did not.
    /// </summary>
    public int WeatherForecastColumnSpan => IsWeatherWideLayout ? 1 : 2;

    /// <summary>
    /// The current reading takes the whole width when nothing sits beside it. Without this
    /// the stacked arrangement would leave the second column empty and squeeze the reading
    /// into half a card.
    /// </summary>
    public int WeatherCurrentColumnSpan => IsWeatherWideLayout ? 1 : 2;

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

    public TokenUsageCardProjection TokenUsageProjection =>
        Volatile.Read(ref _tokenUsageProjection);

    /// <summary>
    /// Stable collections updated in place, for the same reason the hardware monitor's are:
    /// the card exists to sit still and change numbers, and rebuilding the sources on every
    /// tick would rebuild every row's visuals with them.
    /// </summary>
    public ObservableCollection<TokenUsagePageTabViewModel> TokenUsagePages { get; }

    public ObservableCollection<TokenUsageMetricViewModel> TokenUsageMetrics { get; }

    public ObservableCollection<TokenUsageTrendBarViewModel> TokenUsageTrend { get; }

    /// <summary>
    /// The page currently shown. Falls back to the first available page when the selected one
    /// disappears - which is what switching a vendor off does.
    /// </summary>
    public TokenUsagePage? TokenUsageCurrentPage
    {
        get
        {
            IReadOnlyList<TokenUsagePage> pages = TokenUsageProjection.Pages;
            if (pages.Count == 0)
            {
                return null;
            }

            foreach (TokenUsagePage page in pages)
            {
                if (string.Equals(page.PageId, _tokenUsagePageId, StringComparison.Ordinal))
                {
                    return page;
                }
            }

            return pages[0];
        }
    }

    public bool HasTokenUsageData => TokenUsageCurrentPage?.HasData == true;

    /// <summary>
    /// The height this card has for content: a grid row is 160 DIP with an 8 DIP gap, so two
    /// rows are 328, and 12 DIP of padding twice, a 32 DIP header and 6 DIP of spacing leave
    /// 266. One row leaves 98.
    /// </summary>
    private double TokenUsageContentBudget => Placement.Size switch
    {
        CardSize.S or CardSize.M or CardSize.W => 98,
        _ => 266,
    };

    /// <summary>
    /// How many readings are left over once the parts that outrank them have been paid for.
    ///
    /// A card clips what does not fit rather than scrolling it, so a reading past this point
    /// is not cramped - it is invisible, and so is everything below it. The order of payment
    /// is the order of the card's purpose: the day's spend first, then the page tabs that say
    /// whose spend it is, then the trend, and the readings take what is left. That is a change
    /// from counting two readings at every small size and showing the spend only at two rows:
    /// the readings were being paid for before the number the card exists to show.
    /// </summary>
    public int TokenUsageMetricLimit
    {
        get
        {
            double budget = TokenUsageContentBudget;
            if (IsTokenUsageCostVisible)
            {
                budget -= 54;
            }

            if (IsTokenUsagePageSwitcherVisible)
            {
                budget -= 30;
            }

            if (IsTokenUsageTrendVisible)
            {
                budget -= 38;
            }

            // A reading costs about 24 DIP - a label and its value on one line.
            return Math.Clamp(
                (int)(budget / 24),
                0,
                TokenUsageContract.MetricIds.Count);
        }
    }

    /// <summary>
    /// The day's spend, shown as the card's headline rather than as one row among the
    /// readings - it is the number the card is read for, and a table gives every figure the
    /// same weight. Empty when nothing on this page could be priced.
    /// </summary>
    public string TokenUsageCostText => TokenUsageCurrentPage?.CostText ?? string.Empty;

    /// <summary>
    /// The day's spend shows at every size. It is the number the card is read for - a usage
    /// card without it is a table of counts - so it is what the height is spent on first and
    /// the readings are what give way, not the other way round.
    /// </summary>
    public bool IsTokenUsageCostVisible => TokenUsageCostText.Length > 0;

    /// <summary>
    /// One grid row tall. The trend is the first thing to go: it is the tallest single block
    /// and the only one whose absence costs no number.
    /// </summary>
    public bool IsTokenUsageTrendVisible =>
        TokenUsageCurrentPage?.IsTrendVisible == true &&
        Placement.Size is CardSize.L or CardSize.XL;

    /// <summary>
    /// The smallest card has no room for tabs. It shows the overview and nothing else, which
    /// is why the page it shows is the first one rather than whatever was last selected.
    /// </summary>
    public bool IsTokenUsagePageSwitcherVisible =>
        TokenUsageProjection.IsPageSwitcherVisible &&
        Placement.Size is not CardSize.S;

    /// <summary>
    /// Switches the card to another page. Purely local: no IPC, no persistence, and the next
    /// broker snapshot re-renders whichever page is selected at that moment.
    /// </summary>
    public void SelectTokenUsagePage(string pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        if (string.Equals(_tokenUsagePageId, pageId, StringComparison.Ordinal))
        {
            return;
        }

        _tokenUsagePageId = pageId;
        MergeTokenUsage(TokenUsageProjection);
        RaiseTokenUsageChanged();
    }

    public string WeatherAutomationSummary =>
        string.Join(
            " · ",
            RuntimePresentation.AutomationSummary,
            WeatherTemperatureText);

    public bool SetViewportVisibility(bool isInViewport) =>
        _visibilityScheduler?.SetViewportVisibility(Runtime, isInViewport)
            ?? false;

    public bool IsEditing => _editMode.IsEditing;

    /// <summary>
    /// A card's own actions - its settings button, the note tools - are hidden while the
    /// layout is being edited. Edit mode is about where a card sits and how big it is, not
    /// about what it is configured to show, and the two sets of controls were competing for
    /// the same corner: the remove badge sits exactly where a card puts its gear.
    /// </summary>
    public bool AreCardActionsVisible => !_editMode.IsEditing;

    /// <summary>
    /// The height a card of this size has for readings, after its padding, header and the
    /// spacing between them. Same budget the token usage card works from.
    /// </summary>
    private double SystemMonitorContentBudget => Placement.Size switch
    {
        CardSize.S or CardSize.M or CardSize.W => 98,
        _ => 266,
    };

    /// <summary>
    /// A reading's height, measured off the real card rather than guessed: rows sit about
    /// 29 DIP apart, and a second line of detail - memory's "21.8 GB / 31.4 GB" - adds about
    /// 18 more. A meter costs nothing extra; it is a thin bar inside the row it belongs to.
    ///
    /// This is per row rather than a count because the rows differ. A flat count of three put
    /// CPU, memory and GPU on a one-row card and drew the third half inside the clip; costing
    /// the meter separately then held back a reading a two-row card had room for.
    /// </summary>
    private static double SystemMonitorRowCost(SystemMonitorMetricViewModel metric) =>
        29 + (metric.SecondaryText.Length > 0 ? 18 : 0);

    /// <summary>
    /// The note tools - list, pop out, new, more - on a card with room for a row of them. The
    /// one-cell card is the note itself and nothing else; the tools are all reachable from a
    /// larger card, and none of them is the reason the card is on the board.
    /// </summary>
    public bool AreNoteToolsVisible =>
        AreCardActionsVisible && Placement.Size is not CardSize.S;


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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherSecondary)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsWeatherHighLowLabelVisible)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsWeatherHighLowSeparatorVisible)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherFooter)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsWeatherWideLayout)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherForecastColumn)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherForecastRow)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurrentColumnSpan)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherForecastColumnSpan)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AreNoteToolsVisible)));
            // The hardware readings are held back per row rather than by rebuilding the list:
            // the rows are updated in place twice a second, and replacing them on a resize
            // would throw away that identity for no gain.
            ApplySystemMonitorMetricLimit();
            // The token usage card discloses by size too, and its reading count is one of the
            // things that changes - so the bound collection is rebuilt, not just re-announced.
            MergeTokenUsage(TokenUsageProjection);
            RaiseTokenUsageChanged();
        }

        return true;
    }

    /// <summary>
    /// Spends the card's height down the priority-ordered list and stops at the first reading
    /// that does not fit. Everything after it is held back too, even if it would have fitted:
    /// the order is the user's ranking, and skipping over a tall reading to show a short one
    /// behind it would quietly re-rank their list.
    /// </summary>
    private void ApplySystemMonitorMetricLimit()
    {
        double budget = SystemMonitorContentBudget;
        double used = 0;
        bool exhausted = false;
        foreach (SystemMonitorMetricViewModel metric in SystemMonitorMetrics)
        {
            double cost = SystemMonitorRowCost(metric);
            if (exhausted || used + cost > budget)
            {
                exhausted = true;
                metric.IsWithinCardLimit = false;
                continue;
            }

            used += cost;
            metric.IsWithinCardLimit = true;
        }
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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AreCardActionsVisible)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AreNoteToolsVisible)));
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
            _uiInvoker(() =>
            {
                SystemMonitorMetricListMerger.Merge(
                    SystemMonitorMetrics,
                    systemMonitor.Metrics);
                ApplySystemMonitorMetricLimit();
            });
            TokenUsageCardProjection tokenUsage =
                TokenUsageCardProjection.FromSnapshot(
                    Runtime.Snapshot,
                    _runtimeResourceResolver);
            Interlocked.Exchange(ref _tokenUsageProjection, tokenUsage);
            _uiInvoker(() => MergeTokenUsage(tokenUsage));
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
                new PropertyChangedEventArgs(nameof(WeatherHighTemperatureText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherLowTemperatureText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherHighLow)));
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
                new PropertyChangedEventArgs(nameof(HasWeatherSecondary)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherFooter)));
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
            RaiseTokenUsageChanged();
        }
    }

    private void MergeTokenUsage(TokenUsageCardProjection projection)
    {
        TokenUsagePage? page = TokenUsageCurrentPage;
        TokenUsageListMerger.MergePages(
            TokenUsagePages,
            projection.Pages,
            page?.PageId ?? _tokenUsagePageId);
        TokenUsageListMerger.MergeMetrics(
            TokenUsageMetrics,
            TakeVisibleMetrics(page));
        TokenUsageListMerger.MergeTrend(
            TokenUsageTrend,
            page?.Trend ?? Array.Empty<TokenUsageTrendBar>());
    }

    /// <summary>
    /// The prefix of the page's readings this card is tall enough to show. Truncated here
    /// rather than hidden in XAML so the rows that are not shown are never built at all.
    /// </summary>
    private IReadOnlyList<TokenUsageMetricRow> TakeVisibleMetrics(TokenUsagePage? page)
    {
        IReadOnlyList<TokenUsageMetricRow> metrics =
            page?.Metrics ?? Array.Empty<TokenUsageMetricRow>();
        int limit = TokenUsageMetricLimit;
        if (metrics.Count <= limit)
        {
            return metrics;
        }

        var visible = new TokenUsageMetricRow[limit];
        for (int index = 0; index < limit; index++)
        {
            visible[index] = metrics[index];
        }

        return visible;
    }

    private void RaiseTokenUsageChanged()
    {
        foreach (string propertyName in TokenUsageChangeNotifications)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
