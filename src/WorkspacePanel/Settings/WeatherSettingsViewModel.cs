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
        ]);

    private readonly IWeatherSettingsClient? _client;
    private readonly Func<string, string> _text;
    private CardSettingsDraft? _draft;
    private string _locationLabel = WeatherSettingsContract.DefaultLabel;
    private string _latitudeText = WeatherSettingsContract.DefaultLatitude
        .ToString("0.#######", CultureInfo.InvariantCulture);
    private string _longitudeText = WeatherSettingsContract.DefaultLongitude
        .ToString("0.#######", CultureInfo.InvariantCulture);
    private string _statusText = string.Empty;
    private string _validationText = string.Empty;
    private int _revision;
    private bool _isBusy;
    private bool _wasSaved;

    public WeatherSettingsViewModel(
        IWeatherSettingsClient? client,
        Func<string, string>? textResolver = null)
    {
        _client = client;
        _text = textResolver ?? (static key => key);
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
        set => SetField(ref _latitudeText, value ?? string.Empty);
    }

    public string LongitudeText
    {
        get => _longitudeText;
        set => SetField(ref _longitudeText, value ?? string.Empty);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ValidationText
    {
        get => _validationText;
        private set => SetField(ref _validationText, value);
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

    public bool CanSave => _client is not null && _draft is not null && !IsBusy;

    public bool WasSaved
    {
        get => _wasSaved;
        private set => SetField(ref _wasSaved, value);
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
            CardSettingsCommitRequest commit = _draft.BuildCommitRequest();
            var request = new WeatherSettingsSaveRequest
            {
                ClientOperationId = Guid.NewGuid(),
                InstanceId = commit.InstanceId,
                Label = commit.Settings.GetProperty("label").GetString(),
                Latitude = commit.Settings.GetProperty("latitude").GetDouble(),
                Longitude = commit.Settings.GetProperty("longitude").GetDouble(),
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
            nameof(LongitudeText))
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
