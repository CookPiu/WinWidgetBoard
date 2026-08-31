using System.Globalization;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Owns the single built-in weather registration and its local settings.
/// Saving a location is revision-protected in SQLite, then swaps the provider
/// request key and publishes a Loading snapshot before the new visible request
/// is scheduled. No weather payload is persisted here.
/// </summary>
public sealed class WeatherProviderRuntime : IDisposable
{
    private readonly object _gate = new();
    private readonly WeatherSettingsRepository _settingsRepository;
    private readonly ProviderRefreshHost _providerHost;
    private readonly ProviderRefreshVisibilityRegistry _visibilityRegistry;
    private readonly CardSnapshotSubscriptionHub _snapshotHub;
    private readonly HttpClient _httpClient;
    private readonly IProviderRefreshClock _clock;
    private long _sequence;
    private WeatherSettingsRecord _settings;
    private RegistrationState _registration;
    // Written from the publisher without taking _gate: a save already holds _gate while it
    // publishes, so reaching back for _gate from inside the publisher lock would invert the
    // lock order. The value is an immutable record, so a volatile reference is enough.
    private WeatherSummaryDto? _summary;
    private bool _disposed;

    public WeatherProviderRuntime(
        WeatherSettingsRepository settingsRepository,
        ProviderRefreshHost providerHost,
        ProviderRefreshVisibilityRegistry visibilityRegistry,
        CardSnapshotSubscriptionHub snapshotHub,
        HttpClient httpClient,
        IProviderRefreshClock clock)
    {
        _settingsRepository = settingsRepository ??
            throw new ArgumentNullException(nameof(settingsRepository));
        _providerHost = providerHost ??
            throw new ArgumentNullException(nameof(providerHost));
        _visibilityRegistry = visibilityRegistry ??
            throw new ArgumentNullException(nameof(visibilityRegistry));
        _snapshotHub = snapshotHub ??
            throw new ArgumentNullException(nameof(snapshotHub));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        _settings = _settingsRepository.Get(OpenMeteoWeatherProvider.InstanceId) ??
            CreateDefaultSettings();
        _registration = CreateRegistration(_settings);
        try
        {
            _visibilityRegistry.Register(
                OpenMeteoWeatherProvider.InstanceId,
                _registration.HostRegistration.SubscriptionId,
                keepWarmWithoutPanel: true);
        }
        catch
        {
            _registration.HostRegistration.Dispose();
            throw;
        }
    }

    public WeatherSettingsRecord GetSettings()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _settings;
        }
    }

    public WeatherSettingsRecord SaveSettings(
        string instanceId,
        string label,
        double latitude,
        double longitude,
        bool useDeviceLocation,
        string unitSystem,
        int expectedRevision)
    {
        if (!string.Equals(
                instanceId,
                OpenMeteoWeatherProvider.InstanceId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only the built-in weather instance can be configured.",
                nameof(instanceId));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            WeatherSettingsRecord saved = _settingsRepository.Save(
                instanceId,
                label,
                latitude,
                longitude,
                useDeviceLocation,
                unitSystem,
                expectedRevision,
                _clock.UtcNow);
            ApplyRegistrationLocked(saved);
            _settings = saved;
            return saved;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registration.GenerationPublisher.Deactivate();
            _registration.HostRegistration.Dispose();
        }
    }

    private void ApplyRegistrationLocked(WeatherSettingsRecord settings)
    {
        RegistrationState previous = _registration;
        previous.GenerationPublisher.Deactivate();
        RegistrationState next = CreateRegistration(settings);
        try
        {
            // Publish before Replace makes the new registration visible. The
            // following visibility update then schedules its first request.
            next.Adapter.PublishLoadingAsync(
                JsonSerializer.SerializeToElement(
                    new
                    {
                        unitSystem = settings.UnitSystem,
                        location = new
                        {
                            label = settings.Label,
                            latitude = settings.Latitude,
                            longitude = settings.Longitude,
                        },
                    },
                    ContractJson.Options))
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (!_visibilityRegistry.Replace(
                    OpenMeteoWeatherProvider.InstanceId,
                    next.HostRegistration.SubscriptionId))
            {
                throw new InvalidOperationException(
                    "The weather provider visibility registration was not found.");
            }

            _registration = next;
            previous.HostRegistration.Dispose();
        }
        catch
        {
            next.HostRegistration.Dispose();
            previous.GenerationPublisher.Activate();
            throw;
        }
    }

    private RegistrationState CreateRegistration(WeatherSettingsRecord settings)
    {
        var location = new WeatherLocation(
            settings.Label,
            settings.Latitude,
            settings.Longitude,
            settings.UnitSystem);
        var provider = new OpenMeteoWeatherProvider(
            _httpClient,
            utcNow: () => _clock.UtcNow);
        var generationPublisher = new GenerationSnapshotPublisher(
            _snapshotHub,
            CaptureSummary);
        var adapter = new ProviderCardSnapshotAdapter(
            OpenMeteoWeatherProvider.InstanceId,
            OpenMeteoWeatherProvider.CardTypeId,
            schemaVersion: 1,
            generationPublisher,
            readyActions: [CardsContract.RefreshActionId],
            failureActions:
            [
                CardsContract.RefreshActionId,
                CardsContract.OpenDiagnosticsActionId,
                CardsContract.DisableActionId,
            ],
            utcNow: () => _clock.UtcNow,
            sequenceProvider: NextSequence);
        ProviderRefreshHostRegistration hostRegistration =
            _providerHost.Register(
                new ProviderRefreshSubscription(
                    Guid.NewGuid(),
                    OpenMeteoWeatherProvider.CreateRequestKey(location),
                    OpenMeteoWeatherProvider.CreateArguments(location),
                    provider,
                    adapter,
                    new ProviderRefreshVisibility(
                        PanelVisible: false,
                        InViewport: false,
                        DisplayConnected: false)));
        return new RegistrationState(
            provider,
            adapter,
            generationPublisher,
            hostRegistration);
    }

    // The last good reading is kept in this process only, exactly like the card payload it
    // is projected from: nothing here is written to SQLite.
    private void CaptureSummary(CardStateSnapshot snapshot)
    {
        if (snapshot.Payload.ValueKind != JsonValueKind.Object ||
            !snapshot.Payload.TryGetProperty("location", out JsonElement location) ||
            !snapshot.Payload.TryGetProperty("current", out JsonElement current) ||
            location.ValueKind != JsonValueKind.Object ||
            current.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!location.TryGetProperty("label", out JsonElement label) ||
            label.ValueKind != JsonValueKind.String ||
            !current.TryGetProperty("temperatureC", out JsonElement temperature) ||
            !temperature.TryGetDouble(out double temperatureC) ||
            !double.IsFinite(temperatureC))
        {
            return;
        }

        // The payload's numbers are metric; the unitSystem tag says what the reader wants.
        bool imperial = snapshot.Payload.TryGetProperty(
                "unitSystem",
                out JsonElement unitSystem) &&
            unitSystem.ValueKind == JsonValueKind.String &&
            string.Equals(
                unitSystem.GetString(),
                WeatherSettingsContract.ImperialUnitSystem,
                StringComparison.Ordinal);
        double displayTemperature = imperial
            ? (temperatureC * 9d / 5d) + 32d
            : temperatureC;
        var summary = new WeatherSummaryDto
        {
            InstanceId = snapshot.InstanceId,
            Label = label.GetString() ?? string.Empty,
            TemperatureText =
                Math.Round(displayTemperature, MidpointRounding.AwayFromZero)
                    .ToString("0", CultureInfo.InvariantCulture),
            ConditionIconId = ReadConditionIconId(current),
            ObservedAtUtc = snapshot.GeneratedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            IsStale = snapshot.Freshness == CardSnapshotFreshness.Stale,
        };

        Volatile.Write(ref _summary, summary);
    }

    // The provider already reduced the WMO code to a token; only accept one this build
    // knows, so an unrecognised value degrades to Unknown instead of reaching the entry.
    private static string ReadConditionIconId(JsonElement current)
    {
        if (current.TryGetProperty("conditionIconId", out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            WeatherConditionContract.IsKnown(value.GetString()))
        {
            return value.GetString()!;
        }

        return WeatherConditionContract.Unknown;
    }

    public WeatherSummaryDto? TryGetSummary()
    {
        return Volatile.Read(ref _summary);
    }

    private long NextSequence() => checked(Interlocked.Increment(ref _sequence));

    private static WeatherSettingsRecord CreateDefaultSettings()
    {
        WeatherLocation location = OpenMeteoWeatherProvider.DefaultLocation;
        return new WeatherSettingsRecord(
            OpenMeteoWeatherProvider.InstanceId,
            location.Label,
            location.Latitude,
            location.Longitude,
            useDeviceLocation: true,
            WeatherSettingsContract.MetricUnitSystem,
            revision: 0,
            updatedAtUtc: null);
    }

    private sealed record RegistrationState(
        OpenMeteoWeatherProvider Provider,
        ProviderCardSnapshotAdapter Adapter,
        GenerationSnapshotPublisher GenerationPublisher,
        ProviderRefreshHostRegistration HostRegistration);

    private sealed class GenerationSnapshotPublisher : ICardSnapshotPublisher
    {
        private readonly object _gate = new();
        private readonly ICardSnapshotPublisher _inner;
        private readonly Action<CardStateSnapshot> _observe;
        private bool _active = true;

        public GenerationSnapshotPublisher(
            ICardSnapshotPublisher inner,
            Action<CardStateSnapshot> observe)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _observe = observe ?? throw new ArgumentNullException(nameof(observe));
        }

        public ValueTask PublishAsync(
            CardStateSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_active)
                {
                    return ValueTask.CompletedTask;
                }

                _observe(snapshot);
                return _inner.PublishAsync(snapshot, cancellationToken);
            }
        }

        public void Deactivate()
        {
            lock (_gate)
            {
                _active = false;
            }
        }

        public void Activate()
        {
            lock (_gate)
            {
                _active = true;
            }
        }
    }
}
