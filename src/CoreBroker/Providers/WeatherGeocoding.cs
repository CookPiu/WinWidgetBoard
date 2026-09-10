using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Resolves a typed place name to pickable coordinates. There are two implementations because
/// there are two weather sources, and a user who cannot reach one vendor's weather endpoint
/// cannot reach its search endpoint either - offering a source whose city search never
/// answers would be offering half a feature.
/// </summary>
public interface IWeatherGeocodingService
{
    Task<IReadOnlyList<WeatherLocationCandidateDto>> SearchAsync(
        string query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Sends the search to whichever vendor the weather card is currently reading from, falling
/// back to the keyless one when the selected source has no usable credential.
///
/// The choice is read per call rather than captured once: the settings dialog can change the
/// source and search again without closing, and a router bound to the old choice would send
/// the next query to the vendor the user just switched away from.
/// </summary>
public sealed class WeatherGeocodingRouter : IWeatherGeocodingService
{
    private readonly Func<WeatherGeocodingSelection> _selection;
    private readonly IWeatherGeocodingService _fallback;
    private readonly Func<string, string, IWeatherGeocodingService> _createQWeather;

    public WeatherGeocodingRouter(
        Func<WeatherGeocodingSelection> selection,
        IWeatherGeocodingService fallback,
        Func<string, string, IWeatherGeocodingService> createQWeather)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _createQWeather = createQWeather ??
            throw new ArgumentNullException(nameof(createQWeather));
    }

    public Task<IReadOnlyList<WeatherLocationCandidateDto>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        WeatherGeocodingSelection selection = _selection();
        if (!string.Equals(
                selection.ProviderId,
                WeatherSettingsContract.QWeatherProviderId,
                StringComparison.Ordinal) ||
            selection.ApiKey is null ||
            !WeatherSettingsContract.IsAllowedQWeatherHost(selection.ApiHost))
        {
            return _fallback.SearchAsync(query, cancellationToken);
        }

        return _createQWeather(selection.ApiHost, selection.ApiKey)
            .SearchAsync(query, cancellationToken);
    }
}

/// <summary>
/// What the router needs to know about the current settings. The key is present because the
/// search call carries it; it is read straight from the broker's own settings record and goes
/// no further than the request header.
/// </summary>
public sealed record WeatherGeocodingSelection(
    string ProviderId,
    string ApiHost,
    string? ApiKey);
