using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Resolves a typed place name to coordinates through Open-Meteo's geocoding endpoint, so
/// the settings dialog can offer a list instead of asking the user for latitude and
/// longitude.
///
/// This is the second network endpoint in the product and the first one that carries text
/// the user typed, so it is deliberately narrow: same vendor, no account, no device or
/// installation identifier, a bounded query, a bounded response, and a short deadline. It is
/// called only while the settings dialog is open - never on a schedule, and never in the
/// background. See ADR-0027.
/// </summary>
public sealed class OpenMeteoGeocodingService : IWeatherGeocodingService
{
    public const string DataSourceKey = "geocoding-api.open-meteo.com";
    public const int MaxResponseBytes = 64 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    public OpenMeteoGeocodingService(HttpClient httpClient, Uri? endpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = ValidateEndpoint(endpoint ?? new Uri(
            "https://geocoding-api.open-meteo.com/v1/search",
            UriKind.Absolute));

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

        var builder = new UriBuilder(_endpoint)
        {
            Query = "name=" + Uri.EscapeDataString(normalized) +
                "&count=" + WeatherLocationSearchContract.MaxResults
                    .ToString(CultureInfo.InvariantCulture) +
                "&format=json",
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"Geocoding request failed with status {(int)response.StatusCode}.");
        }

        byte[] payload = await ReadBoundedAsync(response.Content, timeout.Token)
            .ConfigureAwait(false);
        return Parse(payload);
    }

    /// <summary>
    /// Turns a geocoding response body into pickable candidates. Public because it is the
    /// part worth testing directly: everything the endpoint can get wrong shows up here.
    /// </summary>
    public static IReadOnlyList<WeatherLocationCandidateDto> Parse(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("results", out JsonElement results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            // Open-Meteo omits "results" entirely when nothing matched; that is an empty
            // answer, not a failure.
            return Array.Empty<WeatherLocationCandidateDto>();
        }

        var candidates = new List<WeatherLocationCandidateDto>(
            WeatherLocationSearchContract.MaxResults);
        foreach (JsonElement result in results.EnumerateArray())
        {
            if (candidates.Count >= WeatherLocationSearchContract.MaxResults)
            {
                break;
            }

            if (result.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? name = ReadString(result, "name", WeatherLocationSearchContract.MaxNameLength);
            if (name is null ||
                !TryReadFiniteDouble(result, "latitude", out double latitude) ||
                !TryReadFiniteDouble(result, "longitude", out double longitude) ||
                !WeatherSettingsContract.IsValidCoordinates(latitude, longitude))
            {
                // A row we cannot use is skipped rather than shown half-filled: the user
                // would have no way to tell which entries actually resolve.
                continue;
            }

            candidates.Add(new WeatherLocationCandidateDto
            {
                Name = name,
                Region = ReadString(
                    result,
                    "admin1",
                    WeatherLocationSearchContract.MaxRegionLength) ?? string.Empty,
                Country = ReadString(
                    result,
                    "country",
                    WeatherLocationSearchContract.MaxRegionLength) ?? string.Empty,
                CountryCode = ReadString(
                    result,
                    "country_code",
                    WeatherLocationSearchContract.MaxCountryCodeLength) ?? string.Empty,
                Latitude = latitude,
                Longitude = longitude,
                Timezone = ReadString(
                    result,
                    "timezone",
                    WeatherLocationSearchContract.MaxTimezoneLength) ?? string.Empty,
            });
        }

        return candidates;
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

    private static bool TryReadFiniteDouble(
        JsonElement parent,
        string name,
        out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out JsonElement element) &&
            element.TryGetDouble(out value) &&
            double.IsFinite(value);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException(
                $"Geocoding response exceeds {MaxResponseBytes} bytes.");
        }

        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8 * 1024];
        while (true)
        {
            int read = await stream
                .ReadAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException(
                    $"Geocoding response exceeds {MaxResponseBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        // Only ever HTTPS, and only ever a real host: a plain-text or loopback endpoint
        // would silently widen where a typed place name can travel.
        if (!endpoint.IsAbsoluteUri ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The geocoding endpoint must be an absolute HTTPS URI.",
                nameof(endpoint));
        }

        return endpoint;
    }
}
