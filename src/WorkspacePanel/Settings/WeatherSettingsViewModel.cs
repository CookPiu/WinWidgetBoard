using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// Settings-dialog state for the built-in weather instance. CardSettingsDraft
/// remains the local validation and revision gate; the IPC client is only used
/// after the draft has produced an immutable commit request.
/// </summary>
public sealed class WeatherSettingsViewModel : INotifyPropertyChanged
{
    private const string WeatherCardTypeId = "builtin.weather";
    private static readonly CardSettingsSchema SettingsSchema =
        new(
        [
            new CardSettingDefinition(
                "label",
                JsonValueKind.String,
                CardSettingWritePolicy.CommitOnly,
                value => WeatherSettingsContract.TryNormalizeLabel(
                    value.GetString(),
                    out _)),
            new CardSettingDefinition(
                "latitude",
                JsonValueKind.Number,
                CardSettingWritePolicy.CommitOnly,
                value => value.TryGetDouble(out double latitude) &&
                    latitude >= -90d &&
                    latitude <= 90d &&
                    double.IsFinite(latitude)),
            new CardSettingDefinition(
                "longitude",
                JsonValueKind.Number,
                CardSettingWritePolicy.CommitOnly,
                value => value.TryGetDouble(out double longitude) &&
                    longitude >= -180d &&
                    longitude <= 180d &&
                    double.IsFinite(longitude)),
            new CardSettingDefinition(
                "useDeviceLocation",
                [JsonValueKind.True, JsonValueKind.False],
                CardSettingWritePolicy.CommitOnly),
        ]);

    private readonly IWeatherSettingsClient? _client;
    private readonly IDeviceLocationProvider? _deviceLocationProvider;
    private readonly Func<string, string> _text;
    private CardSettingsDraft? _draft;
    private string _locationLabel = WeatherSettingsContract.DefaultLabel;
    private string _latitudeText = WeatherSettingsContract.DefaultLatitude
        .ToString("0.#######", CultureInfo.InvariantCulture);
    private string _longitudeText = WeatherSettingsContract.DefaultLongitude
        .ToString("0.#######", CultureInfo.InvariantCulture);
    private string _statusText = string.Empty;
    private string _validationText = string.Empty;
    private string _searchQuery = string.Empty;
    private CancellationTokenSource? _searchCancellation;
    private bool _isSearching;
    private bool _searchUnavailable;
    private int _revision;
    private bool _isBusy;
    private bool _isLocating;
    private bool _useDeviceLocation = true;
    private bool _wasSaved;

    public WeatherSettingsViewModel(
        IWeatherSettingsClient? client,
        Func<string, string>? textResolver = null,
        IDeviceLocationProvider? deviceLocationProvider = null)
    {
        _client = client;
        _deviceLocationProvider = deviceLocationProvider;
        _text = textResolver ?? (static key => key);
        // Subscribed rather than republished at each call site: SearchResults is cleared and
        // refilled from five places, and one missed site would leave both lists on screen at
        // once or neither.
        SearchResults.CollectionChanged += (_, _) => OnSearchResultsChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string LocationLabel
    {
        get => _locationLabel;
        set => SetField(ref _locationLabel, value ?? string.Empty);
    }

    public string LatitudeText
    {
        get => _latitudeText;
        set
        {
            if (SetField(ref _latitudeText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CoordinatesText));
            }
        }
    }

    public string LongitudeText
    {
        get => _longitudeText;
        set
        {
            if (SetField(ref _longitudeText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CoordinatesText));
            }
        }
    }

    /// <summary>
    /// The coordinates exactly as they will be saved. Shown read-only so what leaves the
    /// machine on the next weather request is visible without inviting hand-editing.
    /// </summary>
    public string CoordinatesText => string.Concat(LatitudeText, ", ", LongitudeText);

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>
    /// False while there is nothing to report. The line is collapsed rather than left blank:
    /// an empty row still takes its spacing, which opens a gap under the commit button that
    /// reads as a layout mistake.
    /// </summary>
    public bool HasValidationText => ValidationText.Length > 0;

    public string ValidationText
    {
        get => _validationText;
        private set
        {
            if (SetField(ref _validationText, value))
            {
                OnPropertyChanged(nameof(HasValidationText));
            }
        }
    }

    public int Revision
    {
        get => _revision;
        private set => SetField(ref _revision, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool CanSave =>
        _client is not null && _draft is not null && !IsBusy && !IsLocating;

    public bool UseDeviceLocation
    {
        get => _useDeviceLocation;
        set
        {
            if (SetField(ref _useDeviceLocation, value))
            {
                OnPropertyChanged(nameof(CanRefreshDeviceLocation));
            }
        }
    }

    public bool IsLocating
    {
        get => _isLocating;
        private set
        {
            if (SetField(ref _isLocating, value))
            {
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(CanRefreshDeviceLocation));
            }
        }
    }

    public bool CanRefreshDeviceLocation =>
        _client is not null &&
        _draft is not null &&
        _deviceLocationProvider is not null &&
        !IsBusy &&
        !IsLocating;

    public bool WasSaved
    {
        get => _wasSaved;
        private set => SetField(ref _wasSaved, value);
    }

    /// <summary>
    /// What the user typed into the place search. Setting it does not start a request; the
    /// view decides when to search so a keystroke cannot become a network call.
    /// </summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSearch));
            }
        }
    }

    public ObservableCollection<WeatherLocationOption> SearchResults { get; } = [];

    /// <summary>
    /// The places offered before anything has been searched for. Picking one is the same
    /// action as picking a search result, so nothing downstream has to tell them apart.
    /// </summary>
    public IReadOnlyList<WeatherLocationOption> CommonLocations { get; } =
        CommonWeatherLocations.All;

    /// <summary>
    /// The two lists never show at once: results answer a question the user just asked, and
    /// leaving the common list under them would put a second, unrelated set of places one
    /// scroll below the answer.
    /// </summary>
    public bool ShowCommonLocations => SearchResults.Count == 0;

    public bool ShowSearchResults => SearchResults.Count > 0;

    private void OnSearchResultsChanged()
    {
        OnPropertyChanged(nameof(ShowCommonLocations));
        OnPropertyChanged(nameof(ShowSearchResults));
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetField(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(CanSearch));
            }
        }
    }

    /// <summary>
    /// True once a search failed for a reason other than "no match": the broker has no
    /// search capability, or the request could not be completed. The dialog says so rather
    /// than leaving an empty list that looks like "no such place".
    /// </summary>
    public bool SearchUnavailable
    {
        get => _searchUnavailable;
        private set => SetField(ref _searchUnavailable, value);
    }

    public bool CanSearch =>
        _client is not null &&
        !IsSearching &&
        WeatherLocationSearchContract.TryNormalizeQuery(SearchQuery, out _);

    /// <summary>
    /// Runs one search, superseding any search still in flight. Results replace the previous
    /// list wholesale: a stale response must never be merged into a newer query's results.
    /// </summary>
    public async Task<bool> SearchAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            SearchResults.Clear();
            SearchUnavailable = true;
            SetStatus("WeatherSettingsUnavailableStatus");
            return false;
        }

        if (!WeatherLocationSearchContract.TryNormalizeQuery(
                SearchQuery,
                out string? query) ||
            query is null)
        {
            SearchResults.Clear();
            SearchUnavailable = false;
            return false;
        }

        CancellationTokenSource? previous = _searchCancellation;
        var current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _searchCancellation = current;
        previous?.Cancel();
        previous?.Dispose();

        IsSearching = true;
        SearchUnavailable = false;
        try
        {
            IReadOnlyList<WeatherLocationCandidateDto> results = await _client
                .SearchLocationsAsync(query, current.Token)
                .ConfigureAwait(true);
            if (!ReferenceEquals(_searchCancellation, current))
            {
                // A newer query started while this one was in flight.
                return false;
            }

            SearchResults.Clear();
            foreach (WeatherLocationCandidateDto candidate in results)
            {
                SearchResults.Add(WeatherLocationOption.FromCandidate(candidate));
            }

            SetStatus(
                SearchResults.Count == 0
                    ? "WeatherSettingsSearchEmptyStatus"
                    : "WeatherSettingsSearchReadyStatus");
            return SearchResults.Count > 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
            when (exception is CoreBrokerClientException or IOException or TimeoutException)
        {
            SearchResults.Clear();
            SearchUnavailable = true;
            SetStatus("WeatherSettingsSearchFailedStatus");
            return false;
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, current))
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>
    /// Adopts a searched place. The coordinates come from the search result, never from the
    /// user: that is the whole point of the list.
    /// </summary>
    public void SelectSearchResult(WeatherLocationOption? option)
    {
        if (option is null)
        {
            return;
        }

        LocationLabel = option.Label;
        LatitudeText = option.Latitude.ToString("0.#######", CultureInfo.InvariantCulture);
        LongitudeText = option.Longitude.ToString("0.#######", CultureInfo.InvariantCulture);
        UseDeviceLocation = false;
        ValidationText = string.Empty;
        SetStatus("WeatherSettingsSearchSelectedStatus");
    }

    public async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            SetStatus("WeatherSettingsUnavailableStatus");
            return false;
        }

        IsBusy = true;
        ValidationText = string.Empty;
        try
        {
            WeatherSettingsDto settings = await _client
                .GetWeatherSettingsAsync(
                    WeatherSettingsContract.DefaultInstanceId,
                    cancellationToken);
            ApplySettings(settings);
            SetStatus("WeatherSettingsLoadedStatus");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is CoreBrokerClientException or IOException or TimeoutException)
        {
            SetStatus("WeatherSettingsLoadFailedStatus");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return false;
        }

        if (_client is null || _draft is null)
        {
            SetStatus("WeatherSettingsUnavailableStatus");
            return false;
        }

        if (!TryParseCoordinate(LatitudeText, out double latitude) ||
            !TryParseCoordinate(LongitudeText, out double longitude) ||
            !WeatherSettingsContract.IsValidCoordinates(latitude, longitude))
        {
            SetValidation("WeatherSettingsInvalidLocationStatus");
            return false;
        }

        IsBusy = true;
        ValidationText = string.Empty;
        try
        {
            _draft.Set(
                "label",
                JsonSerializer.SerializeToElement(LocationLabel));
            _draft.Set(
                "latitude",
                JsonSerializer.SerializeToElement(latitude));
            _draft.Set(
                "longitude",
                JsonSerializer.SerializeToElement(longitude));
            _draft.Set(
                "useDeviceLocation",
                JsonSerializer.SerializeToElement(UseDeviceLocation));
            CardSettingsCommitRequest commit = _draft.BuildCommitRequest();
            var request = new WeatherSettingsSaveRequest
            {
                ClientOperationId = Guid.NewGuid(),
                InstanceId = commit.InstanceId,
                Label = commit.Settings.GetProperty("label").GetString(),
                Latitude = commit.Settings.GetProperty("latitude").GetDouble(),
                Longitude = commit.Settings.GetProperty("longitude").GetDouble(),
                UseDeviceLocation = commit.Settings
                    .GetProperty("useDeviceLocation")
                    .GetBoolean(),
                ExpectedRevision = checked((int)commit.ExpectedRevision),
            };
            WeatherSettingsDto saved = await _client
                .SaveWeatherSettingsAsync(request, cancellationToken);
            _ = _draft.AcceptCommit(commit, saved.Revision);
            ApplySettings(saved);
            WasSaved = true;
            SetStatus("WeatherSettingsSavedStatus");
            return true;
        }
        catch (CoreBrokerClientException exception)
            when (exception.Code == "conflict.weather-settings-revision")
        {
            SetValidation("WeatherSettingsConflictStatus");
            return false;
        }
        catch (ArgumentException)
        {
            SetValidation("WeatherSettingsInvalidLocationStatus");
            return false;
        }
        catch (OverflowException)
        {
            SetValidation("WeatherSettingsInvalidLocationStatus");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is CoreBrokerClientException or IOException or TimeoutException)
        {
            SetStatus("WeatherSettingsSaveFailedStatus");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> RefreshDeviceLocationAsync(
        CancellationToken cancellationToken)
    {
        if (!CanRefreshDeviceLocation || _deviceLocationProvider is null)
        {
            SetStatus("WeatherSettingsDeviceLocationUnavailableStatus");
            return false;
        }

        IsLocating = true;
        ValidationText = string.Empty;
        SetStatus("WeatherSettingsDeviceLocationReadingStatus");
        try
        {
            DeviceLocationResult result = await _deviceLocationProvider
                .GetCurrentLocationAsync(cancellationToken)
                .ConfigureAwait(true);
            if (result.Status == DeviceLocationStatus.Denied)
            {
                SetStatus("WeatherSettingsDeviceLocationDeniedStatus");
                return false;
            }

            if (result.Status != DeviceLocationStatus.Available ||
                !WeatherSettingsContract.IsValidCoordinates(
                    result.Latitude,
                    result.Longitude))
            {
                SetStatus("WeatherSettingsDeviceLocationUnavailableStatus");
                return false;
            }

            string automaticLabel = ResolveText("WeatherSettingsAutomaticLocationLabel");
            bool changed = !UseDeviceLocation ||
                !string.Equals(LocationLabel, automaticLabel, StringComparison.Ordinal) ||
                !TryParseCoordinate(LatitudeText, out double currentLatitude) ||
                !TryParseCoordinate(LongitudeText, out double currentLongitude) ||
                Math.Abs(currentLatitude - result.Latitude) >= 0.001d ||
                Math.Abs(currentLongitude - result.Longitude) >= 0.001d;
            UseDeviceLocation = true;
            LocationLabel = automaticLabel;
            LatitudeText = result.Latitude.ToString("0.#######", CultureInfo.InvariantCulture);
            LongitudeText = result.Longitude.ToString("0.#######", CultureInfo.InvariantCulture);
            if (changed && !await SaveAsync(cancellationToken))
            {
                return false;
            }

            SetStatus("WeatherSettingsDeviceLocationReadyStatus");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or TimeoutException)
        {
            SetStatus("WeatherSettingsDeviceLocationUnavailableStatus");
            return false;
        }
        finally
        {
            IsLocating = false;
        }
    }

    public void CancelDraft()
    {
        if (_draft?.State == CardSettingsDraftState.Active)
        {
            _draft.Cancel();
        }
    }

    private void ApplySettings(WeatherSettingsDto settings)
    {
        if (!string.Equals(
                settings.InstanceId,
                WeatherSettingsContract.DefaultInstanceId,
                StringComparison.Ordinal) ||
            !WeatherSettingsContract.TryNormalizeLabel(
                settings.Label,
                out string? label) ||
            label is null ||
            !WeatherSettingsContract.IsValidCoordinates(
                settings.Latitude,
                settings.Longitude))
        {
            throw new InvalidOperationException("CoreBroker returned invalid weather settings.");
        }

        LocationLabel = label;
        LatitudeText = settings.Latitude.ToString(
            "0.#######",
            CultureInfo.InvariantCulture);
        LongitudeText = settings.Longitude.ToString(
            "0.#######",
            CultureInfo.InvariantCulture);
        UseDeviceLocation = settings.UseDeviceLocation;
        Revision = settings.Revision;
        _draft = CreateDraft(settings, label);
        OnPropertyChanged(nameof(CanSave));
    }

    private static CardSettingsDraft CreateDraft(
        WeatherSettingsDto settings,
        string normalizedLabel)
    {
        JsonElement values = JsonSerializer.SerializeToElement(
            new
            {
                label = normalizedLabel,
                latitude = settings.Latitude,
                longitude = settings.Longitude,
                useDeviceLocation = settings.UseDeviceLocation,
            });
        var snapshot = new CardSettingsSnapshot(
            WeatherSettingsContract.DefaultInstanceId,
            WeatherCardTypeId,
            schemaVersion: 1,
            settings.Revision,
            values);
        return new CardSettingsDraft(
            snapshot,
            SettingsSchema,
            sessionId: Guid.NewGuid().ToString("N"));
    }

    private static bool TryParseCoordinate(
        string value,
        out double result)
    {
        const NumberStyles styles = NumberStyles.Float;
        return double.TryParse(
                value,
                styles,
                CultureInfo.CurrentCulture,
                out result) ||
            double.TryParse(
                value,
                styles,
                CultureInfo.InvariantCulture,
                out result);
    }

    private void SetStatus(string resourceKey) =>
        StatusText = ResolveText(resourceKey);

    private void SetValidation(string resourceKey) =>
        ValidationText = ResolveText(resourceKey);

    private string ResolveText(string resourceKey)
    {
        string resolved = _text(resourceKey);
        return string.IsNullOrEmpty(resolved) ? resourceKey : resolved;
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(LocationLabel) or
            nameof(LatitudeText) or
            nameof(LongitudeText) or
            nameof(UseDeviceLocation))
        {
            OnPropertyChanged(nameof(CanSave));
        }

        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}
