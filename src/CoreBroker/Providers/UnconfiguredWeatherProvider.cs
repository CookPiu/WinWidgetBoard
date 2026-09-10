using System.Text.Json;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Stands in for a weather source the user has selected but not finished configuring - today
/// that means QWeather with no API host or no API key.
///
/// The alternative would be to leave the previous source registered, which would show the
/// card quietly reading from a vendor the user just switched away from, or to register
/// nothing, which would leave the card on its last snapshot with no explanation. Registering
/// a source that fails on purpose keeps the pipeline shape identical and puts the reason on
/// the card, where the settings dialog is one click away.
/// </summary>
public sealed class UnconfiguredWeatherProvider : IProviderRefreshSource
{
    public const string ProviderId = "app.winwidgetboard.weather.unconfigured";
    public const string Capability = "weather.current";
    public const string DataSourceKey = "local";

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string _errorCode;

    public UnconfiguredWeatherProvider(
        string errorCode,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        _errorCode = errorCode;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    public ScheduledProviderDescriptor Descriptor { get; } =
        new(
            ProviderId,
            Capability,
            minimumInterval: TimeSpan.FromMinutes(15),
            visibleInterval: TimeSpan.FromMinutes(15),
            hiddenInterval: TimeSpan.FromHours(1),
            powerSaverInterval: null,
            requestTimeout: TimeSpan.FromSeconds(10),
            // Nothing here touches the network, and saying otherwise would make the
            // scheduler hold this back while offline - which is exactly when the user most
            // needs to see why the card is empty.
            requiresNetwork: false,
            supportsManualRefresh: false,
            manualRefreshMinimumInterval: TimeSpan.FromMinutes(5),
            new ProviderBackoffOptions(
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(2),
                jitterRatio: 0.2));

    public static ProviderRequestKey CreateRequestKey(WeatherLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new ProviderRequestKey(
            ProviderId,
            Capability,
            DataSourceKey,
            location.ArgumentsFingerprint);
    }

    public ValueTask<ProviderRefreshResult> FetchAsync(
        ProviderRefreshRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _utcNow().ToUniversalTime();
        using JsonDocument empty = JsonDocument.Parse("{}");
        return ValueTask.FromResult(
            new ProviderRefreshResult(
                request.RequestId,
                ProviderRefreshResultKind.PermissionRequired,
                empty.RootElement.Clone(),
                now,
                now,
                _errorCode,
                retryAfter: null));
    }
}
