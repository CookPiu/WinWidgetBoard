using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// The first production weather source. It deliberately accepts coordinates
/// as provider arguments so automatic location never becomes an implicit
/// permission or privacy dependency.
/// </summary>
public sealed class OpenMeteoWeatherProvider : IProviderRefreshSource
{
    public const string ProviderId = "app.winwidgetboard.weather.open-meteo";
    public const string Capability = "weather.current";
    public const string DataSourceKey = "api.open-meteo.com";
    public const string CardTypeId = "builtin.weather";
    public const string InstanceId = "demo.weather";
    public const string AttributionUrl = "https://open-meteo.com/";
    public const string AttributionText = "Weather data by Open-Meteo.com";
    public const int MaxResponseBytes = 64 * 1024;

    private static readonly TimeSpan CacheFreshness =
        TimeSpan.FromMinutes(15);

    private static readonly string CurrentVariables = string.Join(
        ",",
        [
            "temperature_2m",
            "relative_humidity_2m",
            "apparent_temperature",
            "weather_code",
            "wind_speed_10m",
        ]);

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly Func<DateTimeOffset> _utcNow;
    private long _lastAccessedUtcTicks;
    private WeatherCachedResponse? _cachedResponse;

    public OpenMeteoWeatherProvider(
        HttpClient httpClient,
        Uri? endpoint = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = ValidateEndpoint(endpoint ?? new Uri(
            "https://api.open-meteo.com/v1/forecast",
            UriKind.Absolute));
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("WinWidgetBoard", "0.1"));
        }
    }

    public ScheduledProviderDescriptor Descriptor { get; } =
        new(
            ProviderId,
            Capability,
            minimumInterval: TimeSpan.FromMinutes(15),
            visibleInterval: TimeSpan.FromMinutes(15),
            hiddenInterval: null,
            powerSaverInterval: null,
            requestTimeout: TimeSpan.FromSeconds(10),
            requiresNetwork: true,
            supportsManualRefresh: false,
            manualRefreshMinimumInterval: TimeSpan.FromMinutes(5),
            new ProviderBackoffOptions(
                TimeSpan.FromMinutes(15),
                TimeSpan.FromHours(2),
                jitterRatio: 0.2));

    public DateTimeOffset? LastAccessedAtUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastAccessedUtcTicks);
            return ticks == 0
                ? null
                : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public static WeatherLocation DefaultLocation { get; } =
        new("Singapore", 1.3521, 103.8198);

    public static JsonElement CreateArguments(WeatherLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return JsonSerializer.SerializeToElement(
            new
            {
                label = location.Label,
                latitude = location.Latitude,
                longitude = location.Longitude,
                units = "metric",
            },
            ContractJson.Options);
    }

    public static ProviderRequestKey CreateRequestKey(
        WeatherLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new ProviderRequestKey(
            ProviderId,
            Capability,
            DataSourceKey,
            location.ArgumentsFingerprint);
    }

    public async ValueTask<ProviderRefreshResult> FetchAsync(
        ProviderRefreshRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!WeatherLocation.TryParse(request.Arguments, out WeatherLocation? location) ||
            location is null)
        {
            return CreateFailureResult(
                request,
                ProviderRefreshResultKind.Failed,
                "weather.invalid-arguments");
        }

        Uri requestUri = BuildRequestUri(location);
        DateTimeOffset requestStartedAtUtc = _utcNow().ToUniversalTime();
        Interlocked.Exchange(
            ref _lastAccessedUtcTicks,
            requestStartedAtUtc.UtcDateTime.Ticks);

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return CreateFailureResult(
                    request,
                    ProviderRefreshResultKind.RateLimited,
                    "weather.rate-limited",
                    ReadRetryAfter(response));
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden)
            {
                return CreateFailureResult(
                    request,
                    ProviderRefreshResultKind.PermissionRequired,
                    "weather.permission-required");
            }

            if (!response.IsSuccessStatusCode)
            {
                return CreateFailureResult(
                    request,
                    response.StatusCode is HttpStatusCode.NotFound or
                        HttpStatusCode.Gone
                        ? ProviderRefreshResultKind.Unavailable
                        : ProviderRefreshResultKind.Failed,
                    $"weather.http-{(int)response.StatusCode}");
            }

            byte[] responseBytes = await ReadResponseBytesAsync(
                response.Content,
                cancellationToken).ConfigureAwait(false);
            WeatherApiResponse parsed = ParseResponse(responseBytes);
            DateTimeOffset producedAtUtc = _utcNow().ToUniversalTime();
            DateTimeOffset validUntilUtc = producedAtUtc.Add(CacheFreshness);
            JsonElement payload = CreatePayload(location, parsed);
            var cached = new WeatherCachedResponse(
                location.ArgumentsFingerprint,
                payload.Clone(),
                producedAtUtc,
                validUntilUtc);
            Interlocked.Exchange(ref _cachedResponse, cached);

            return new ProviderRefreshResult(
                request.RequestId,
                ProviderRefreshResultKind.Success,
                payload,
                producedAtUtc,
                validUntilUtc);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return CreateFailureResult(
                request,
                ProviderRefreshResultKind.Offline,
                "weather.network");
        }
        catch (IOException)
        {
            return CreateFailureResult(
                request,
                ProviderRefreshResultKind.Offline,
                "weather.network");
        }
        catch (InvalidDataException)
        {
            return CreateFailureResult(
                request,
                ProviderRefreshResultKind.Failed,
                "weather.invalid-response");
        }
        catch (JsonException)
        {
            return CreateFailureResult(
                request,
                ProviderRefreshResultKind.Failed,
                "weather.invalid-response");
        }
    }

    private ProviderRefreshResult CreateFailureResult(
        ProviderRefreshRequest request,
        ProviderRefreshResultKind kind,
        string errorCode,
        TimeSpan? retryAfter = null)
    {
        DateTimeOffset now = _utcNow().ToUniversalTime();
        WeatherCachedResponse? cached = Volatile.Read(ref _cachedResponse);
        if (!WeatherLocation.TryParse(request.Arguments, out WeatherLocation? location) ||
            location is null ||
            cached is null ||
            !string.Equals(
                cached.ArgumentsFingerprint,
                location.ArgumentsFingerprint,
                StringComparison.Ordinal))
        {
            return new ProviderRefreshResult(
                request.RequestId,
                kind,
                EmptyPayload(),
                now,
                now,
                errorCode,
                retryAfter);
        }

        return new ProviderRefreshResult(
            request.RequestId,
            kind,
            cached.Payload,
            cached.ProducedAtUtc,
            cached.ValidUntilUtc,
            errorCode,
            retryAfter);
    }

    private Uri BuildRequestUri(WeatherLocation location)
    {
        string query = string.Join(
            "&",
            [
                $"latitude={FormatDouble(location.Latitude)}",
                $"longitude={FormatDouble(location.Longitude)}",
                $"current={Uri.EscapeDataString(CurrentVariables)}",
                "timezone=auto",
                "temperature_unit=celsius",
                "wind_speed_unit=kmh",
            ]);
        string separator = _endpoint.Query.Length == 0 ? "?" : "&";
        return new Uri(
            _endpoint.AbsoluteUri + separator + query,
            UriKind.Absolute);
    }

    private static WeatherApiResponse ParseResponse(byte[] responseBytes)
    {
        using JsonDocument document = JsonDocument.Parse(responseBytes);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("current", out JsonElement current) ||
            current.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Open-Meteo response has no current object.");
        }

        string observedAtLocal = ReadRequiredString(current, "time");
        double temperatureC = ReadRequiredFiniteDouble(
            current,
            "temperature_2m");
        double apparentTemperatureC = ReadRequiredFiniteDouble(
            current,
            "apparent_temperature");
        double humidityPercent = ReadRequiredFiniteDouble(
            current,
            "relative_humidity_2m");
        double windSpeedKmh = ReadRequiredFiniteDouble(
            current,
            "wind_speed_10m");
        int weatherCode = ReadRequiredInt32(current, "weather_code");
        string timezone = root.TryGetProperty("timezone", out JsonElement timezoneValue) &&
            timezoneValue.ValueKind == JsonValueKind.String
            ? timezoneValue.GetString() ?? ""
            : "";

        return new WeatherApiResponse(
            observedAtLocal,
            temperatureC,
            apparentTemperatureC,
            humidityPercent,
            windSpeedKmh,
            weatherCode,
            timezone);
    }

    private static JsonElement CreatePayload(
        WeatherLocation location,
        WeatherApiResponse response) =>
        JsonSerializer.SerializeToElement(
            new
            {
                source = ProviderId,
                sourceDomain = DataSourceKey,
                attribution = AttributionText,
                attributionUrl = AttributionUrl,
                location = new
                {
                    label = location.Label,
                    latitude = location.Latitude,
                    longitude = location.Longitude,
                    timezone = response.Timezone,
                },
                current = new
                {
                    observedAtLocal = response.ObservedAtLocal,
                    temperatureC = response.TemperatureC,
                    apparentTemperatureC = response.ApparentTemperatureC,
                    relativeHumidityPercent = response.HumidityPercent,
                    windSpeedKmh = response.WindSpeedKmh,
                    weatherCode = response.WeatherCode,
                },
            },
            ContractJson.Options);

    private static async Task<byte[]> ReadResponseBytesAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException(
                $"Weather response exceeds {MaxResponseBytes} bytes.");
        }

        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(
                chunk,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException(
                    $"Weather response exceeds {MaxResponseBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            TimeSpan delay = date - DateTimeOffset.UtcNow;
            return delay >= TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri ||
            (!string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Weather endpoint must be an absolute HTTP(S) URI.",
                nameof(endpoint));
        }

        return endpoint;
    }

    private static double ReadRequiredFiniteDouble(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
        {
            throw new InvalidDataException(
                $"Open-Meteo response property '{propertyName}' is invalid.");
        }

        return result;
    }

    private static int ReadRequiredInt32(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            !value.TryGetInt32(out int result))
        {
            throw new InvalidDataException(
                $"Open-Meteo response property '{propertyName}' is invalid.");
        }

        return result;
    }

    private static string ReadRequiredString(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Open-Meteo response property '{propertyName}' is invalid.");
        }

        return value.GetString()!;
    }

    private static string FormatDouble(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static JsonElement EmptyPayload()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private sealed record WeatherCachedResponse(
        string ArgumentsFingerprint,
        JsonElement Payload,
        DateTimeOffset ProducedAtUtc,
        DateTimeOffset ValidUntilUtc);

    private sealed record WeatherApiResponse(
        string ObservedAtLocal,
        double TemperatureC,
        double ApparentTemperatureC,
        double HumidityPercent,
        double WindSpeedKmh,
        int WeatherCode,
        string Timezone);
}

public sealed record WeatherLocation
{
    public WeatherLocation(string label, double latitude, double longitude)
    {
        if (string.IsNullOrWhiteSpace(label) ||
            label.Length > 80 ||
            label.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Weather location label must be 1-80 characters without control characters.",
                nameof(label));
        }

        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitude),
                latitude,
                "Latitude must be a finite value between -90 and 90.");
        }

        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(
                nameof(longitude),
                longitude,
                "Longitude must be a finite value between -180 and 180.");
        }

        Label = label.Trim();
        Latitude = latitude;
        Longitude = longitude;
    }

    public string Label { get; }

    public double Latitude { get; }

    public double Longitude { get; }

    public string ArgumentsFingerprint
    {
        get
        {
            string canonical = string.Join(
                "|",
                [
                    Label,
                    Latitude.ToString("0.######", CultureInfo.InvariantCulture),
                    Longitude.ToString("0.######", CultureInfo.InvariantCulture),
                ]);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
        }
    }

    public static bool TryParse(
        JsonElement arguments,
        out WeatherLocation? location)
    {
        location = null;
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("label", out JsonElement labelValue) ||
            labelValue.ValueKind != JsonValueKind.String ||
            !arguments.TryGetProperty("latitude", out JsonElement latitudeValue) ||
            !arguments.TryGetProperty("longitude", out JsonElement longitudeValue) ||
            !latitudeValue.TryGetDouble(out double latitude) ||
            !longitudeValue.TryGetDouble(out double longitude))
        {
            return false;
        }

        try
        {
            location = new WeatherLocation(
                labelValue.GetString() ?? string.Empty,
                latitude,
                longitude);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
