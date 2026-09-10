using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// City search against QWeather's GeoAPI, the counterpart to
/// <see cref="OpenMeteoGeocodingService"/> for accounts using the QWeather source.
///
/// It carries the same constraints as the other search path (ADR-0027): the query is text the
/// user typed and submitted, it is bounded, the response is bounded, the deadline is short,
/// and it runs only while the settings dialog is open - never on a schedule. What it adds is
/// a credential, which travels as a request header and nowhere else.
/// </summary>
public sealed class QWeatherGeocodingService : IWeatherGeocodingService
{
    public const string ApiPath = "/geo/v2/city/lookup";
    public const int MaxResponseBytes = 64 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _httpClient;
    private readonly string _apiHost;
    private readonly string _apiKey;

    public QWeatherGeocodingService(HttpClient httpClient, string apiHost, string apiKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!WeatherSettingsContract.TryNormalizeApiHost(apiHost, out string normalizedHost) ||
            !WeatherSettingsContract.IsAllowedQWeatherHost(normalizedHost))
        {
            throw new ArgumentException(
                "The QWeather API host must be a host under qweatherapi.com or qweather.com.",
                nameof(apiHost));
        }

        if (!WeatherSettingsContract.IsValidApiKey(apiKey))
        {
            throw new ArgumentException(
                "The QWeather API key is not a usable credential.",
                nameof(apiKey));
        }

        _apiHost = normalizedHost;
        _apiKey = apiKey;

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("WinWidgetBoard", "0.1"));
        }
    }

    public async Task<IReadOnlyList<WeatherLocationCandidateDto>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (!WeatherLocationSearchContract.TryNormalizeQuery(
                query,
                out string? normalized) ||
            normalized is null)
        {
            throw new ArgumentException("Query is not a valid search term.", nameof(query));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        var uri = new Uri(
            $"https://{_apiHost}{ApiPath}?location=" +
            Uri.EscapeDataString(normalized) +
            "&number=" +
            WeatherLocationSearchContract.MaxResults.ToString(CultureInfo.InvariantCulture),
            UriKind.Absolute);

        using HttpRequestMessage request = QWeatherHttp.CreateRequest(uri, _apiKey);
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"Geocoding request failed with status {(int)response.StatusCode}.");
        }

        byte[] payload = await QWeatherHttp
            .ReadBoundedAsync(response.Content, MaxResponseBytes, timeout.Token)
            .ConfigureAwait(false);
        return Parse(payload);
    }

    /// <summary>
    /// Turns a GeoAPI body into pickable candidates. Public because it is the part worth
    /// testing directly - notably that coordinates arrive as strings here, unlike every other
    /// number this product reads out of a weather response.
    /// </summary>
    public static IReadOnlyList<WeatherLocationCandidateDto> Parse(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<WeatherLocationCandidateDto>();
        }

        // GeoAPI answers 200 OK with a status in the body. "404" is "nothing matched", which
        // is an empty answer rather than a failure; anything else that is not "200" is a
        // failure the dialog should show as "search unavailable".
        string? status = root.TryGetProperty("code", out JsonElement code) &&
            code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;
        if (string.Equals(status, "404", StringComparison.Ordinal))
        {
            return Array.Empty<WeatherLocationCandidateDto>();
        }

        if (!string.Equals(status, "200", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The QWeather geocoding response reported a failure status.");
        }

        if (!root.TryGetProperty("location", out JsonElement locations) ||
            locations.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<WeatherLocationCandidateDto>();
        }

        var candidates = new List<WeatherLocationCandidateDto>(
            WeatherLocationSearchContract.MaxResults);
        foreach (JsonElement location in locations.EnumerateArray())
        {
            if (candidates.Count >= WeatherLocationSearchContract.MaxResults)
            {
                break;
            }

            if (location.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? name = ReadString(
                location,
                "name",
                WeatherLocationSearchContract.MaxNameLength);
            if (name is null ||
                !TryReadCoordinate(location, "lat", out double latitude) ||
                !TryReadCoordinate(location, "lon", out double longitude) ||
                !WeatherSettingsContract.IsValidCoordinates(latitude, longitude))
            {
                // A row we cannot use is skipped rather than shown half-filled.
                continue;
            }

            candidates.Add(new WeatherLocationCandidateDto
            {
                Name = name,
                // adm1 is the first-level division, which is what tells two same-named
                // places apart - the same role admin1 plays in the other vendor's answer.
                Region = ReadString(
                    location,
                    "adm1",
                    WeatherLocationSearchContract.MaxRegionLength) ?? string.Empty,
                Country = ReadString(
                    location,
                    "country",
                    WeatherLocationSearchContract.MaxRegionLength) ?? string.Empty,
                // GeoAPI names the country but sends no ISO code for it.
                CountryCode = string.Empty,
                Latitude = latitude,
                Longitude = longitude,
                Timezone = ReadString(
                    location,
                    "tz",
                    WeatherLocationSearchContract.MaxTimezoneLength) ?? string.Empty,
            });
        }

        return candidates;
    }

    /// <summary>
    /// GeoAPI sends latitude and longitude as strings. Parsed with the invariant culture so a
    /// machine whose locale uses a decimal comma still reads "39.91755" as one number rather
    /// than as thirty-nine.
    /// </summary>
    private static bool TryReadCoordinate(
        JsonElement parent,
        string name,
        out double value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out JsonElement element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => double.TryParse(
                    element.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value) &&
                double.IsFinite(value),
            JsonValueKind.Number => element.TryGetDouble(out value) && double.IsFinite(value),
            _ => false,
        };
    }

    private static string? ReadString(JsonElement parent, string name, int maxLength)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
        {
            return null;
        }

        return text.Length > maxLength ? text[..maxLength] : text;
    }
}
