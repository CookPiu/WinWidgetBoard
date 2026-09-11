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
        nameof(TokenUsageSpendCurve),
        nameof(IsTokenUsageSpendCurveVisible),
        nameof(TokenUsageCostText),
        nameof(IsTokenUsageCostVisible),
        nameof(TokenUsageCostNoteText),
        nameof(IsTokenUsageCostNoteVisible),
        nameof(IsTokenUsagePageSwitcherVisible),
        nameof(IsTokenUsageWideLayout),
        nameof(TokenUsageHeadlineColumnSpan),
        nameof(TokenUsageHeadlineMaxWidth),
        nameof(TokenUsageKpiRow),
        nameof(TokenUsageKpiColumn),
        nameof(TokenUsageKpiColumnSpan),
        nameof(TokenUsageKpiMaxWidth),
        nameof(TokenUsageMetricsRow),
        nameof(TokenUsageMetricsColumn),
        nameof(TokenUsageMetricsColumnSpan),
        nameof(IsTokenUsageBreakdownVisible),
        nameof(IsTokenUsageKpiVisible),
        nameof(TokenUsageTotalTokensText),
        nameof(TokenUsageTotalTokensNoteText),
        nameof(TokenUsageRequestsText),
        nameof(TokenUsageRequestsNoteText),
        nameof(IsTokenUsageSplitVisible),
        nameof(TokenUsageSplitLeadText),
        nameof(TokenUsageSplitTrailText),
        nameof(IsTokenUsageSplitTrailVisible),
        nameof(TokenUsageSplitPercent),
        nameof(TokenUsageMetricLimit),
    ];

    private readonly CardRuntimeVisibilityScheduler? _visibilityScheduler;
    private readonly CardRuntimeRegistration? _visibilityRegistration;
    private CardRuntimeStatusPresentation _runtimePresentation;
    private WeatherCardProjection _weatherProjection;
    private SystemMonitorCardProjection _systemMonitorProjection;
    private SystemMonitorMetricViewModel? _systemMonitorHeadline;
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
        MergeSystemMonitor(_systemMonitorProjection);
        _tokenUsageProjection = TokenUsageCardProjection.FromSnapshot(
            Runtime.Snapshot,
            _runtimeResourceResolver);
        TokenUsagePages = [];
        TokenUsageMetrics = [];
        TokenUsageBreakdown = [];
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

    /// <summary>
    /// Who produced the reading, in the panel's language. Resolved the same way the
    /// condition is: the projection names the string, this item looks it up, and the
    /// vendor's own English phrase is the fallback rather than the default. It is bound
    /// rather than stated in the template because the card now has more than one possible
    /// source, and both vendors require attribution - crediting the wrong one is not a
    /// cosmetic error.
    /// </summary>
    public string WeatherAttributionText
    {
        get
        {
            WeatherCardProjection projection = WeatherProjection;
            if (projection.AttributionResourceKey.Length == 0)
            {
                return projection.AttributionText;
            }

            string? localized = _runtimeResourceResolver(
                projection.AttributionResourceKey);
            return string.IsNullOrEmpty(localized)
                ? projection.AttributionText
                : localized;
        }
    }

    /// <summary>
    /// The licence link that goes with <see cref="WeatherAttributionText"/>. Null when the
    /// payload's URL is not an absolute HTTP(S) address, so a malformed value leaves the
    /// text without a link instead of handing the shell something to launch.
    /// </summary>
    public Uri? WeatherAttributionUrl =>
        Uri.TryCreate(WeatherProjection.AttributionUrl, UriKind.Absolute, out Uri? parsed) &&
        (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            ? parsed
            : null;

    public bool HasWeatherData => WeatherProjection.HasData;

    /// <summary>
    /// How many hours of the trend a card this wide can separate. The provider publishes a
    /// full day and the card slices it here, because the broker cannot see a card's size: a
    /// payload cut to one size would starve the other. Two columns cannot tell twenty-four
    /// points apart - they would land 14 DIP from each other - so the narrow cards take the
    /// nearer half.
    /// </summary>
    private int WeatherHourBudget => Placement.Size switch
    {
        CardSize.W or CardSize.XL => 24,
        _ => 12,
    };

    /// <summary>
    /// How many forecast days fit. Only the widest card has the height for five; the rest keep
    /// three, which is what the two-column budget affords once the trend and footer are paid
    /// for. Content that does not fit is clipped rather than scrolled, so this is a budget and
    /// not a preference.
    /// </summary>
    private int WeatherDayBudget => Placement.Size == CardSize.XL ? 5 : 3;

    public IReadOnlyList<WeatherHourProjection> WeatherHours =>
        Slice(WeatherProjection.Hours, WeatherHourBudget);

    /// <summary>
    /// The trend as the curve control wants it: already worded, already in the reader's
    /// units. The control positions and draws and never formats or looks anything up, because
    /// this item is the only place that holds the panel's resource loader.
    /// </summary>
    public IReadOnlyList<WeatherCurvePoint> WeatherCurvePoints =>
        WeatherHours
            .Select(hour => new WeatherCurvePoint(
                hour.TemperatureCelsius,
                hour.TimeText,
                hour.TemperatureText,
                ResolveConditionText(hour.ConditionResourceKey, hour.ConditionText),
                hour.ConditionIconId,
                hour.PrecipitationProbabilityPercent))
            .ToArray();

    /// <summary>
    /// The localized condition name, with the projection's English description as the
    /// fallback - the same arrangement <see cref="WeatherConditionText"/> uses for the
    /// current reading, so a code with no entry degrades to "WMO 82" rather than to blank.
    /// </summary>
    private string ResolveConditionText(string resourceKey, string fallback)
    {
        if (resourceKey.Length == 0)
        {
            return fallback;
        }

        string? localized = _runtimeResourceResolver(resourceKey);
        return string.IsNullOrEmpty(localized) ? fallback : localized;
    }

    /// <summary>
    /// The forecast rows, placed on the scale the visible days share. Normalising after the
    /// slice, not before, is what lets a three-day card and a five-day one each use their
    /// full width instead of leaving room for days they do not show.
    /// </summary>
    public IReadOnlyList<WeatherDayRow> WeatherDays =>
        WeatherDayScale.Place(Slice(WeatherProjection.Days, WeatherDayBudget));

    private static IReadOnlyList<T> Slice<T>(IReadOnlyList<T> source, int count) =>
        source.Count <= count
            ? source
            : source.Take(count).ToArray();

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
    /// How tall the trend gets. Stated rather than stretched: the control is in a stack, which
    /// offers infinite height along its own axis, and a curve that measured itself would feed
    /// its stroke back into the card's height. The four-column sizes can afford more because
    /// they show a full day of hours and have the width to separate them.
    /// </summary>
    public double WeatherCurveHeight => Placement.Size switch
    {
        CardSize.XL => 106d,
        CardSize.W => 84d,
        _ => 76d,
    };

    /// <summary>
    /// Three marks under the trend: where it starts, where it is halfway, where it ends. The
    /// hover readout names any hour exactly, so these exist to say how far the curve reaches -
    /// without them a descending line could be the next three hours or the next two days.
    /// </summary>
    public bool HasWeatherCurveAxis => HasWeatherHours && WeatherCurvePoints.Count >= 3;

    public string WeatherCurveAxisStartText =>
        _runtimeResourceResolver("WeatherCurveNow") is { Length: > 0 } localized
            ? localized
            : WeatherCurvePoints.Count > 0
                ? WeatherCurvePoints[0].TimeText
                : string.Empty;

    public string WeatherCurveAxisMidText
    {
        get
        {
            IReadOnlyList<WeatherCurvePoint> points = WeatherCurvePoints;
            return points.Count >= 3 ? points[points.Count / 2].TimeText : string.Empty;
        }
    }

    public string WeatherCurveAxisEndText
    {
        get
        {
            IReadOnlyList<WeatherCurvePoint> points = WeatherCurvePoints;
            return points.Count > 0 ? points[^1].TimeText : string.Empty;
        }
    }

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
    /// numbers. It holds everything under the headline; the headline itself is
    /// <see cref="SystemMonitorHeadline"/>, which is a different shape on the card.
    /// </summary>
    public ObservableCollection<SystemMonitorMetricViewModel> SystemMonitorMetrics { get; }

    /// <summary>
    /// The reading the card leads with, updated in place like the rows under it and replaced
    /// only when a different metric takes the lead - which happens when the user re-orders
    /// their list, not on a tick.
    /// </summary>
    public SystemMonitorMetricViewModel? SystemMonitorHeadline => _systemMonitorHeadline;

    public bool HasSystemMonitorHeadline => _systemMonitorHeadline is not null;

    public bool HasSystemMonitorData => SystemMonitorProjection.HasData;

    /// <summary>
    /// True for the four-column sizes, where the headline sits beside the readings instead of
    /// above them - the same arrangement, and the same reason, as the token card's wide
    /// layout. W is four cells across and one tall: stacked, the headline would eat more than
    /// half of the 98 DIP and leave room for a single reading on the widest one-row card there
    /// is. Beside it, the readings get the card's whole height and the headline costs them
    /// nothing.
    /// </summary>
    public bool IsSystemMonitorWideLayout =>
        Placement.Size is CardSize.W or CardSize.XL;

    /// <summary>
    /// The headline's grid cell, so one template serves both arrangements. Stacked it spans
    /// both columns, which is also what gives the value a width to be measured against.
    /// </summary>
    public int SystemMonitorHeadlineColumnSpan => IsSystemMonitorWideLayout ? 1 : 2;

    /// <summary>
    /// What the headline's lane may grow to when it has one beside the readings. A 28 DIP
    /// figure and its caption need about 150; the bound is what stops a long secondary line
    /// ("21.8 GB / 31.4 GB") from taking the lane the readings live in.
    /// </summary>
    public double SystemMonitorHeadlineMaxWidth =>
        IsSystemMonitorWideLayout ? 196d : double.PositiveInfinity;

    public int SystemMonitorMetricsRow => IsSystemMonitorWideLayout ? 0 : 1;

    public int SystemMonitorMetricsColumn => IsSystemMonitorWideLayout ? 1 : 0;

    public int SystemMonitorMetricsColumnSpan => IsSystemMonitorWideLayout ? 1 : 2;

    public TokenUsageCardProjection TokenUsageProjection =>
        Volatile.Read(ref _tokenUsageProjection);

    /// <summary>
    /// Stable collections updated in place, for the same reason the hardware monitor's are:
    /// the card exists to sit still and change numbers, and rebuilding the sources on every
    /// tick would rebuild every row's visuals with them.
    /// </summary>
    public ObservableCollection<TokenUsagePageTabViewModel> TokenUsagePages { get; }

    public ObservableCollection<TokenUsageMetricViewModel> TokenUsageMetrics { get; }

    /// <summary>
    /// The split's slices as their own rows. Only the four-by-two card has a band to put them
    /// under; everywhere else the same slices are the two ends of one meter.
    /// </summary>
    public ObservableCollection<TokenUsageBreakdownViewModel> TokenUsageBreakdown { get; }

    /// <summary>
    /// The day's spend curve behind the headline. A list rather than a merged collection: the
    /// curve is one geometry rebuilt from all of its points, so there is no per-point visual
    /// to keep, and a snapshot replaces it whole.
    /// </summary>
    public IReadOnlyList<TokenUsageSpendPoint> TokenUsageSpendCurve =>
        TokenUsageCurrentPage?.SpendCurve ?? Array.Empty<TokenUsageSpendPoint>();

    /// <summary>
    /// Shown at every size: it is a backdrop and costs no height. Hidden, rather than drawn
    /// flat, when the page has nothing today.
    /// </summary>
    public bool IsTokenUsageSpendCurveVisible =>
        TokenUsageCurrentPage?.IsSpendCurveVisible == true;

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
    /// True for the four-column sizes, where the card's blocks sit beside each other rather
    /// than under each other - the same arrangement, and the same reason, as the weather
    /// card's wide layout. W is four cells across but only one tall, so stacked it left the
    /// readings the 14 DIP the amount and the tabs had not spent - fewer than the one-cell
    /// card showed. Beside the amount they get the card's whole height, and the tiles cost
    /// the amount nothing at all.
    /// </summary>
    public bool IsTokenUsageWideLayout =>
        Placement.Size is CardSize.W or CardSize.XL;

    /// <summary>
    /// The amount's grid cell, so one template serves both arrangements. Stacked it spans
    /// every column, which is also what gives the unpriced note a width to wrap against: in
    /// an Auto lane of its own it would be measured against infinity and take the whole card.
    /// </summary>
    public int TokenUsageHeadlineColumnSpan => IsTokenUsageWideLayout ? 1 : 3;

    /// <summary>
    /// What the amount's lane may grow to when it has one. 28 DIP digits and their caption
    /// need about 130; the bound is what keeps the unpriced note out of the lanes beside it.
    /// </summary>
    public double TokenUsageHeadlineMaxWidth =>
        IsTokenUsageWideLayout ? 184d : double.PositiveInfinity;

    public int TokenUsageKpiRow => IsTokenUsageWideLayout ? 0 : 1;

    public int TokenUsageKpiColumn => IsTokenUsageWideLayout ? 1 : 0;

    public int TokenUsageKpiColumnSpan => IsTokenUsageWideLayout ? 1 : 3;

    /// <summary>Same bound, same reason, for the tiles' own Auto lane.</summary>
    public double TokenUsageKpiMaxWidth =>
        IsTokenUsageWideLayout ? 224d : double.PositiveInfinity;

    public int TokenUsageMetricsRow => IsTokenUsageWideLayout ? 0 : 2;

    public int TokenUsageMetricsColumn => IsTokenUsageWideLayout ? 2 : 0;

    public int TokenUsageMetricsColumnSpan => IsTokenUsageWideLayout ? 1 : 3;

    /// <summary>
    /// What the expanded breakdown takes out of the band above it - a row per slice, plus the
    /// spacing between the two blocks. Zero wherever the breakdown is not shown.
    /// </summary>
    private double TokenUsageBreakdownHeight =>
        IsTokenUsageBreakdownVisible && TokenUsageCurrentPage is TokenUsagePage page
            ? (page.Breakdown.Count * 24) + 6
            : 0d;

    /// <summary>
    /// The height the readings themselves have.
    ///
    /// Stacked, they are last in line: the day's spend first, then the two tiles and the
    /// split meter a two-row card has room for, and the readings take what is left. Beside
    /// the amount they have a lane to themselves and pay for nothing above them, which is the
    /// whole point of the wide arrangement - the only thing held back from them there is the
    /// band the expanded breakdown needs underneath.
    ///
    /// The page tabs are not charged at any size any more: they sit in the card's header row
    /// beside the settings gear, which is a row the card already has.
    /// </summary>
    private double TokenUsageMetricBudget
    {
        get
        {
            if (IsTokenUsageWideLayout)
            {
                return TokenUsageContentBudget - TokenUsageBreakdownHeight;
            }

            double budget = TokenUsageContentBudget;
            if (IsTokenUsageCostVisible)
            {
                budget -= 54;
            }

            if (IsTokenUsageKpiVisible)
            {
                // A caption, an 18 DIP figure and a caption under it, plus the spacing.
                budget -= 60;
            }

            if (IsTokenUsageSplitVisible)
            {
                // A caption line and the 3 DIP meter under it, plus the spacing.
                budget -= 25;
            }

            return budget;
        }
    }

    /// <summary>
    /// How many readings fit in that height. A card clips what does not fit rather than
    /// scrolling it, so a reading past this point is not cramped - it is invisible, and so is
    /// everything below it.
    /// </summary>
    public int TokenUsageMetricLimit =>
        // A reading costs about 24 DIP - a label and its value on one line.
        Math.Clamp(
            (int)(TokenUsageMetricBudget / 24),
            0,
            TokenUsageContract.MetricIds.Count);

    /// <summary>
    /// The one-cell card has about 130 DIP for a reading row, which the row's proportions
    /// split into 55/37/37: the name only just fits and the secondary figure does not, so
    /// there the reading takes both number lanes instead of being trimmed into one.
    /// </summary>
    private bool HasTokenUsageSecondaryColumn => Placement.Size is not CardSize.S;

    /// <summary>
    /// The two tiles - every token, and responses - need a block of their own 60 DIP tall.
    /// Stacked that is a second grid row, which rules out S and M; beside the amount it costs
    /// the amount nothing, which is how the one-row W card earns them.
    /// </summary>
    public bool IsTokenUsageKpiVisible =>
        TokenUsageCurrentPage?.HasKpi == true &&
        Placement.Size is not (CardSize.S or CardSize.M);

    public string TokenUsageTotalTokensText =>
        TokenUsageCurrentPage?.TotalTokensText ?? string.Empty;

    public string TokenUsageTotalTokensNoteText =>
        TokenUsageCurrentPage?.TotalTokensNoteText ?? string.Empty;

    public string TokenUsageRequestsText =>
        TokenUsageCurrentPage?.RequestsText ?? string.Empty;

    public string TokenUsageRequestsNoteText =>
        TokenUsageCurrentPage?.RequestsNoteText ?? string.Empty;

    /// <summary>
    /// The split meter: how the page's spend divides between its two largest slices, vendors
    /// on the overview and models on a vendor page. It needs a second grid row, for the same
    /// reason as the tiles, and only says anything where the page has something to split.
    ///
    /// L only, because that is the size where one meter is all the room there is - which is
    /// what ADR-0036 chose it for. XL has a band under its lanes and shows the same slices as
    /// rows instead, so drawing both there would state the split twice.
    /// </summary>
    public bool IsTokenUsageSplitVisible =>
        TokenUsageCurrentPage?.Breakdown.Count > 0 &&
        HasTokenUsageData &&
        Placement.Size is CardSize.L;

    /// <summary>
    /// The same slices as rows, each with its own share meter. Only the four-by-two card has
    /// the band for them: at every other size a row per slice is the breakdown table that was
    /// drawn off the bottom of the card, which is why the single meter exists at all.
    /// </summary>
    public bool IsTokenUsageBreakdownVisible =>
        TokenUsageCurrentPage?.Breakdown.Count > 0 &&
        HasTokenUsageData &&
        Placement.Size is CardSize.XL;

    public string TokenUsageSplitLeadText =>
        TokenUsageCurrentPage?.Breakdown.Count > 0
            ? TokenUsageCurrentPage.Breakdown[0].SplitLabel
            : string.Empty;

    public string TokenUsageSplitTrailText =>
        TokenUsageCurrentPage?.Breakdown.Count > 1
            ? TokenUsageCurrentPage.Breakdown[1].SplitLabel
            : string.Empty;

    public bool IsTokenUsageSplitTrailVisible => TokenUsageSplitTrailText.Length > 0;

    /// <summary>The largest slice's share, 0..100 for the meter.</summary>
    public double TokenUsageSplitPercent =>
        TokenUsageCurrentPage?.Breakdown.Count > 0
            ? TokenUsageCurrentPage.Breakdown[0].MeterPercent
            : 0d;

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
    /// What stands in for the headline when nothing on the page could be priced: the note
    /// naming how many models have no rate. A page with usage but no figure at all read as
    /// "the card lost its cost", which is exactly what happened the day a new model id
    /// arrived - the note says why instead of saying nothing.
    /// </summary>
    public string TokenUsageCostNoteText => TokenUsageCurrentPage?.CostNoteText ?? string.Empty;

    public bool IsTokenUsageCostNoteVisible =>
        !IsTokenUsageCostVisible &&
        TokenUsageCostNoteText.Length > 0 &&
        TokenUsageCurrentPage?.UnpricedModelCount > 0;

    /// <summary>
    /// The tabs sit in the card's header row, beside the settings gear: they select which
    /// page the whole card shows, which is card chrome rather than a block of its content,
    /// and in the content flow they cost 30 DIP - exactly the reading the two-cell card was
    /// showing before they were added to it.
    ///
    /// The smallest card has no room for them even there: about 114 DIP is left beside the
    /// title, and three tabs need some 156. It shows the overview and nothing else, which is
    /// why the page it shows is the first one rather than whatever was last selected. They
    /// also give way in edit mode, with the gear they now sit next to.
    /// </summary>
    public bool IsTokenUsagePageSwitcherVisible =>
        TokenUsageProjection.IsPageSwitcherVisible &&
        AreCardActionsVisible &&
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

    /// <summary>
    /// What the trend says, for a reader who cannot see it. The backdrop illustration
    /// deliberately carries no automation name because the card's text already states the
    /// same facts; the curve is the opposite case - twelve hours of readings appear nowhere
    /// else on the card, so leaving it unnamed would hide them.
    /// </summary>
    public string WeatherCurveAutomationName
    {
        get
        {
            IReadOnlyList<WeatherCurvePoint> points = WeatherCurvePoints;
            if (points.Count == 0)
            {
                return string.Empty;
            }

            string? format = _runtimeResourceResolver("WeatherCurveAutomationNameFormat");
            string first = points[0].TemperatureText;
            string last = points[^1].TemperatureText;
            return string.IsNullOrEmpty(format)
                ? string.Join(" · ", first, last)
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    format,
                    points.Count,
                    first,
                    last);
        }
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
    /// What the rows under the headline actually have.
    ///
    /// Stacked, the headline is charged against them: a 28 DIP figure with its caption above
    /// is 54, and 6 more separates the block from the first row. Beside them it costs nothing,
    /// which is the whole point of the wide arrangement. The curve behind the headline is not
    /// charged at all - it is a backdrop inside the same 54, exactly as the token card's is.
    ///
    /// Any new block on this card has to be added to this account. The reading it would push
    /// past the budget does not get cramped; it disappears, and so does everything under it.
    /// </summary>
    private double SystemMonitorMetricBudget =>
        IsSystemMonitorWideLayout || !HasSystemMonitorHeadline
            ? SystemMonitorContentBudget
            : SystemMonitorContentBudget - 60;

    /// <summary>
    /// A reading's height, measured off the real card rather than guessed: rows sit about
    /// 29 DIP apart, and a second line of detail - memory's "21.8 GB / 31.4 GB" - adds about
    /// 18 more. A meter costs nothing extra; it is a thin bar inside the row it belongs to,
    /// and neither does the curve that replaces it - that one is behind the row's own text.
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

    /// <summary>
    /// On a one-row card the note switcher has the card to itself while it is open: there is
    /// about 106 DIP of content height, and a search box plus a list of any use fills it. A
    /// taller card keeps the editor in view underneath, so the note just picked can be read
    /// without closing anything.
    /// </summary>
    public bool IsNoteBrowsingExclusive =>
        Placement.Size is CardSize.S or CardSize.M or CardSize.W;

    /// <summary>
    /// How tall the switcher's result list may grow before it scrolls inside itself: what is
    /// left of a one-row card after the search box, or a comfortable few results on a taller
    /// one. Unbounded, the list pushed the editor out of the card's viewport.
    /// </summary>
    public double NoteSwitcherListMaxHeight => IsNoteBrowsingExclusive ? 64 : 180;


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
            // The two lists are now sliced by card size, so a resize that does not change the
            // snapshot still changes what they contain. Without these the card would keep the
            // previous size's slice until the next refresh.
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherHours)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurvePoints)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherCurveAxis)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurveAxisStartText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurveAxisMidText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurveAxisEndText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurveAutomationName)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherDays)));
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
                new PropertyChangedEventArgs(nameof(WeatherCurveHeight)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherForecastColumnSpan)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AreNoteToolsVisible)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsNoteBrowsingExclusive)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(NoteSwitcherListMaxHeight)));
            // The hardware readings are held back per row rather than by rebuilding the list:
            // the rows are updated in place twice a second, and replacing them on a resize
            // would throw away that identity for no gain. The cells the headline and the rows
            // occupy do change with the size, and a card that only re-announced them on the
            // next snapshot would sit in the wrong arrangement for up to two seconds.
            ApplySystemMonitorMetricLimit();
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsSystemMonitorWideLayout)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SystemMonitorHeadlineColumnSpan)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SystemMonitorHeadlineMaxWidth)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SystemMonitorMetricsRow)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SystemMonitorMetricsColumn)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SystemMonitorMetricsColumnSpan)));
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
        double budget = SystemMonitorMetricBudget;
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

    /// <summary>
    /// Brings the headline and the rows under it in line with a new projection. The headline
    /// is updated in place wherever it is still the same metric - the card's largest figure
    /// would otherwise be rebuilt every two seconds - and replaced only when the lead changes,
    /// which is a configuration change rather than a tick.
    /// </summary>
    private void MergeSystemMonitor(SystemMonitorCardProjection projection)
    {
        SystemMonitorMetricRow? headline = projection.Headline;
        if (headline is null)
        {
            if (_systemMonitorHeadline is not null)
            {
                _systemMonitorHeadline = null;
                RaiseSystemMonitorHeadlineChanged();
            }
        }
        else if (_systemMonitorHeadline is null ||
            !_systemMonitorHeadline.Apply(headline))
        {
            // Apply refuses a row for another metric, which is exactly the case that needs a
            // new view model rather than an update.
            _systemMonitorHeadline = new SystemMonitorMetricViewModel(headline);
            RaiseSystemMonitorHeadlineChanged();
        }

        SystemMonitorMetricListMerger.Merge(
            SystemMonitorMetrics,
            projection.TrailingMetrics);
        ApplySystemMonitorMetricLimit();
    }

    private void RaiseSystemMonitorHeadlineChanged()
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(SystemMonitorHeadline)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(HasSystemMonitorHeadline)));
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
            // The token usage tabs now sit beside that gear and give way with it.
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsTokenUsagePageSwitcherVisible)));
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
            _uiInvoker(() => MergeSystemMonitor(systemMonitor));
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
                new PropertyChangedEventArgs(nameof(WeatherAttributionUrl)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherData)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherHours)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurvePoints)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasWeatherCurveAxis)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurveAxisStartText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurveAxisMidText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurveAxisEndText)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(WeatherCurveAutomationName)));
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
        TokenUsageListMerger.MergeBreakdown(
            TokenUsageBreakdown,
            IsTokenUsageBreakdownVisible && page is not null
                ? page.Breakdown
                : Array.Empty<TokenUsageBreakdownRow>());
    }

    /// <summary>
    /// The prefix of the page's readings this card is tall enough to show. Truncated here
    /// rather than hidden in XAML so the rows that are not shown are never built at all.
    /// Responses leave the rows when the tile shows them: the same number twice on one card
    /// is the tile's, not the row's.
    /// </summary>
    private TokenUsageMetricRow[] TakeVisibleMetrics(TokenUsagePage? page)
    {
        IReadOnlyList<TokenUsageMetricRow> metrics =
            page?.Metrics ?? Array.Empty<TokenUsageMetricRow>();
        if (IsTokenUsageKpiVisible)
        {
            metrics = metrics
                .Where(metric => !string.Equals(
                    metric.MetricId,
                    TokenUsageContract.TodayRequests,
                    StringComparison.Ordinal))
                .ToArray();
        }

        int limit = Math.Min(TokenUsageMetricLimit, metrics.Count);
        var visible = new TokenUsageMetricRow[limit];
        bool secondaryColumn = HasTokenUsageSecondaryColumn;
        for (int index = 0; index < limit; index++)
        {
            visible[index] = secondaryColumn
                ? metrics[index]
                : metrics[index] with { HasSecondaryColumn = false };
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
