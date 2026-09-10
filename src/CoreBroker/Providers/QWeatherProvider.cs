using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// The second production weather source, added because the first one is not dependable from
/// every network the product runs on (ADR-0038). It reaches the same card payload as
/// <see cref="OpenMeteoWeatherProvider"/> - same field names, same WMO codes, same units - so
/// the panel, the taskbar entry and the snapshot pipeline cannot tell the two apart.
///
/// Unlike Open-Meteo it needs an account: the caller supplies the developer's own API host
/// and API key. The key is sent as a request header on the messages this provider builds and
/// is never put on a URL, in a request key, or in any error text.
/// </summary>
public sealed class QWeatherProvider : IProviderRefreshSource
{
    public const string ProviderId = "app.winwidgetboard.weather.qweather";
    public const string Capability = "weather.current";

    /// <summary>
    /// A constant, not the account's own host. The request key travels through scheduling and
    /// diagnostics, and the per-account host is part of how a request authenticates - it is
    /// not something to spread around for a value whose only job is to group requests by
    /// vendor.
    /// </summary>
    public const string DataSourceKey = "qweatherapi.com";

    public const string CardTypeId = "builtin.weather";
    public const string InstanceId = WeatherSettingsContract.DefaultInstanceId;
    public const string AttributionUrl = "https://www.qweather.com/";
    public const string AttributionText = "Weather data by QWeather";

    /// <summary>Bounds the decompressed body, which is what the parser actually walks.</summary>
    public const int MaxResponseBytes = 128 * 1024;

    public const string CredentialsMissingErrorCode = "weather.credentials-missing";

    private static readonly TimeSpan CacheFreshness = TimeSpan.FromMinutes(15);

    /// <summary>Today plus three, matching what the card shows.</summary>
    private const int ForecastDays = 4;

    private const int PublishedHourlyCount = 12;

    private const int MaxPublishedDailyCount = 3;

    private readonly HttpClient _httpClient;
    private readonly string _apiHost;
    private readonly string _apiKey;
    private readonly Func<DateTimeOffset> _utcNow;
    private long _lastAccessedUtcTicks;
    private WeatherCachedResponse? _cachedResponse;

    public QWeatherProvider(
        HttpClient httpClient,
        string apiHost,
        string apiKey,
        Func<DateTimeOffset>? utcNow = null)
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
            hiddenInterval: TimeSpan.FromHours(1),
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

    public static ProviderRequestKey CreateRequestKey(WeatherLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new ProviderRequestKey(
            ProviderId,
            Capability,
            DataSourceKey,
            location.ArgumentsFingerprint);
    }

    public static JsonElement CreateArguments(WeatherLocation location) =>
        OpenMeteoWeatherProvider.CreateArguments(location);

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

        DateTimeOffset requestStartedAtUtc = _utcNow().ToUniversalTime();
        Interlocked.Exchange(
            ref _lastAccessedUtcTicks,
            requestStartedAtUtc.UtcDateTime.Ticks);

        try
        {
            // The three endpoints are independent, so they go out together rather than one
            // after another: the descriptor allows ten seconds for the whole refresh, and
            // three sequential round trips would spend most of it waiting.
            Task<byte[]> currentTask = GetAsync(
                BuildUri("current", location, null),
                cancellationToken);
            Task<byte[]?> hourlyTask = TryGetAsync(
                BuildUri(
                    "hourly",
                    location,
                    "hours=" + PublishedHourlyCount.ToString(CultureInfo.InvariantCulture)),
                cancellationToken);
            Task<byte[]?> dailyTask = TryGetAsync(
                BuildUri(
                    "daily",
                    location,
                    "days=" + ForecastDays.ToString(CultureInfo.InvariantCulture)),
                cancellationToken);

            byte[] currentBytes;
            try
            {
                currentBytes = await currentTask.ConfigureAwait(false);
            }
            finally
            {
                // Awaited even when the current reading already failed, so a later fault on
                // one of the supplementary calls cannot surface as an unobserved exception.
                await Task.WhenAll(hourlyTask, dailyTask).ConfigureAwait(false);
            }

            byte[]? hourlyBytes = await hourlyTask.ConfigureAwait(false);
            byte[]? dailyBytes = await dailyTask.ConfigureAwait(false);

            QWeatherApiResponse parsed = ParseResponse(
                currentBytes,
                hourlyBytes,
                dailyBytes,
                _utcNow().ToUniversalTime());
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
        catch (QWeatherRequestException exception)
        {
            return CreateFailureResult(
                request,
                exception.Kind,
                exception.ErrorCode,
                exception.RetryAfter);
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

    /// <summary>
    /// Turns the three bodies into one reading. Public so the parsing - which is where every
    /// difference between the two vendors actually lives - can be tested without a network.
    /// </summary>
    public static QWeatherApiResponse ParseResponse(
        byte[] currentBytes,
        byte[]? hourlyBytes,
        byte[]? dailyBytes,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentBytes);

        List<QWeatherDay> days = ReadDays(dailyBytes);
        List<QWeatherHour> hours = ReadHours(hourlyBytes, nowUtc);

        // The vendor sends no time zone of its own, so the location's offset is taken from
        // whichever forecast came back with timestamps. Without either, the card simply
        // shows no observation time rather than the machine's own clock, which would be the
        // wrong time for any location the user is not standing in.
        TimeSpan? localOffset = hours.Count > 0
            ? hours[0].ForecastTime.Offset
            : days.Count > 0
                ? days[0].Start.Offset
                : null;

        using JsonDocument document = JsonDocument.Parse(currentBytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("QWeather current response is not an object.");
        }

        double temperatureC = ReadTemperatureCelsius(root, "temperature");
        double apparentTemperatureC = ReadTemperatureCelsius(root, "feelsLike");
        double humidityPercent = ReadRequiredFraction(root, "humidity") * 100d;
        double windSpeedKmh = ReadWindSpeedKmh(root);
        int? weatherCode = QWeatherConditionCodes.ToWeatherCode(
            ReadConditionCode(root));
        bool isDay = ResolveIsDay(days, nowUtc);
        string? observedAtLocal = localOffset is { } offset
            ? FormatLocalTime(nowUtc.ToOffset(offset))
            : null;

        return new QWeatherApiResponse(
            observedAtLocal,
            temperatureC,
            apparentTemperatureC,
            humidityPercent,
            windSpeedKmh,
            weatherCode,
            isDay,
            hours,
            days,
            days.Count > 0 ? days[0] : null);
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

    private Uri BuildUri(string resource, WeatherLocation location, string? extraQuery)
    {
        // Coordinates are the path, not a query: the vendor documents at most two decimals,
        // and rounding here keeps the same place from producing several distinct URLs.
        string query = "localTime=true";
        if (extraQuery is not null)
        {
            query += "&" + extraQuery;
        }

        return new Uri(
            $"https://{_apiHost}/weather/v1/{resource}/" +
            $"{FormatCoordinate(location.Latitude)}/{FormatCoordinate(location.Longitude)}" +
            $"?{query}",
            UriKind.Absolute);
    }

    private async Task<byte[]> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = QWeatherHttp.CreateRequest(uri, _apiKey);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new QWeatherRequestException(
                ProviderRefreshResultKind.RateLimited,
                "weather.rate-limited",
                ReadRetryAfter(response));
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or
            HttpStatusCode.PaymentRequired)
        {
            // The account, not the network: re-running the same request cannot fix it, so it
            // is reported as needing the user rather than as a transient failure.
            throw new QWeatherRequestException(
                ProviderRefreshResultKind.PermissionRequired,
                "weather.permission-required",
                null);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new QWeatherRequestException(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                    ? ProviderRefreshResultKind.Unavailable
                    : ProviderRefreshResultKind.Failed,
                $"weather.http-{(int)response.StatusCode}",
                null);
        }

        return await QWeatherHttp.ReadBoundedAsync(
            response.Content,
            MaxResponseBytes,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The forecast calls, which are supplementary. A failure here yields null and the card
    /// keeps the current reading; only the current call can fail the whole refresh.
    /// </summary>
    private async Task<byte[]?> TryGetAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            return await GetAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is QWeatherRequestException or
                HttpRequestException or
                IOException or
                InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// The hours the card can use, from the next full hour onwards. Entries already in the
    /// past are dropped rather than shown: the vendor's window starts at the current hour,
    /// and a trend whose first column is behind the reading above it reads as a mistake.
    /// </summary>
    private static List<QWeatherHour> ReadHours(byte[]? hourlyBytes, DateTimeOffset nowUtc)
    {
        if (hourlyBytes is null)
        {
            return [];
        }

        List<QWeatherHour> hours = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(hourlyBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryReadArray(document.RootElement, "hours", out JsonElement entries))
            {
                return [];
            }

            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (hours.Count >= PublishedHourlyCount)
                {
                    break;
                }

                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryReadTimestamp(entry, "forecastTime", out DateTimeOffset forecastTime) ||
                    !TryReadTemperatureCelsius(entry, "temperature", out double temperature))
                {
                    // One malformed hour ends the trend rather than leaving a hole in it.
                    break;
                }

                if (forecastTime < nowUtc.AddHours(-1))
                {
                    continue;
                }

                hours.Add(
                    new QWeatherHour(
                        forecastTime,
                        temperature,
                        QWeatherConditionCodes.ToWeatherCode(ReadConditionCode(entry))));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return hours;
    }

    /// <summary>
    /// The daily forecast, index 0 being today. Sunrise and sunset are kept because they are
    /// the only thing in any of the three responses that says whether it is currently day at
    /// the queried location - the vendor sends no is-day flag of its own.
    /// </summary>
    private static List<QWeatherDay> ReadDays(byte[]? dailyBytes)
    {
        if (dailyBytes is null)
        {
            return [];
        }

        List<QWeatherDay> days = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(dailyBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryReadArray(document.RootElement, "days", out JsonElement entries))
            {
                return [];
            }

            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (days.Count > MaxPublishedDailyCount)
                {
                    break;
                }

                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryReadTimestamp(entry, "forecastStartTime", out DateTimeOffset start) ||
                    !TryReadTemperatureCelsius(entry, "temperatureMax", out double high) ||
                    !TryReadTemperatureCelsius(entry, "temperatureMin", out double low))
                {
                    break;
                }

                JsonElement? daytime =
                    entry.TryGetProperty("daytime", out JsonElement daytimeValue) &&
                    daytimeValue.ValueKind == JsonValueKind.Object
                        ? daytimeValue
                        : null;
                days.Add(
                    new QWeatherDay(
                        start,
                        high,
                        low,
                        // The forecast rows summarise a day, so the day half's condition is
                        // the one they show; a night glyph on a weekday row reads as
                        // "tonight" rather than as that day.
                        QWeatherConditionCodes.ToWeatherCode(
                            daytime is null ? null : ReadConditionCode(daytime.Value)),
                        ReadOptionalTimestamp(entry, "sunrise"),
                        ReadOptionalTimestamp(entry, "sunset")));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return days;
    }

    /// <summary>
    /// Daylight at the queried location, from today's sunrise and sunset. A location inside a
    /// polar day or night has one or both missing, and any day without both falls back to the
    /// location's own clock rather than to the machine's.
    /// </summary>
    private static bool ResolveIsDay(
        List<QWeatherDay> days,
        DateTimeOffset nowUtc)
    {
        foreach (QWeatherDay day in days)
        {
            if (day.Sunrise is { } sunrise && day.Sunset is { } sunset)
            {
                if (nowUtc >= sunrise && nowUtc < sunset)
                {
                    return true;
                }

                // Only today can answer; a later day's window says nothing about now.
                if (nowUtc < sunset)
                {
                    return false;
                }
            }
        }

        if (days.Count > 0)
        {
            int localHour = nowUtc.ToOffset(days[0].Start.Offset).Hour;
            return localHour is >= 6 and < 18;
        }

        return true;
    }

    private static JsonElement CreatePayload(
        WeatherLocation location,
        QWeatherApiResponse response) =>
        JsonSerializer.SerializeToElement(
            new
            {
                source = ProviderId,
                sourceDomain = DataSourceKey,
                attribution = AttributionText,
                attributionUrl = AttributionUrl,
                unitSystem = location.UnitSystem,
                location = new
                {
                    label = location.Label,
                    latitude = location.Latitude,
                    longitude = location.Longitude,
                    // The vendor names no time zone in a weather response, and an invented
                    // one would be worse than none: the card only uses it as a label.
                    timezone = string.Empty,
                },
                current = new
                {
                    observedAtLocal = response.ObservedAtLocal,
                    temperatureC = response.TemperatureC,
                    apparentTemperatureC = response.ApparentTemperatureC,
                    relativeHumidityPercent = response.HumidityPercent,
                    windSpeedKmh = response.WindSpeedKmh,
                    weatherCode = response.WeatherCode,
                    isDay = response.IsDay,
                    conditionIconId = ResolveConditionIconId(
                        response.WeatherCode,
                        response.IsDay),
                    todayHighTemperatureC = response.Today?.HighTemperatureC,
                    todayLowTemperatureC = response.Today?.LowTemperatureC,
                },
                hourly = response.Hourly.Select(entry => new
                {
                    timeLocal = FormatLocalTime(entry.ForecastTime),
                    temperatureC = entry.TemperatureC,
                    weatherCode = entry.WeatherCode,
                    isDay = true,
                    conditionIconId = ResolveConditionIconId(entry.WeatherCode, isDay: true),
                }).ToArray(),
                daily = response.Daily
                    .Skip(1)
                    .Take(MaxPublishedDailyCount)
                    .Select(entry => new
                    {
                        dateLocal = FormatLocalDate(entry.Start),
                        highTemperatureC = entry.HighTemperatureC,
                        lowTemperatureC = entry.LowTemperatureC,
                        weatherCode = entry.WeatherCode,
                        conditionIconId = ResolveConditionIconId(
                            entry.WeatherCode,
                            isDay: true),
                    })
                    .ToArray(),
            },
            ContractJson.Options);

    private static string ResolveConditionIconId(int? weatherCode, bool isDay) =>
        weatherCode is { } code
            ? WeatherConditionContract.FromWeatherCode(code, isDay)
            : WeatherConditionContract.Unknown;

    /// <summary>
    /// Local wall-clock without the offset, which is the shape the other provider publishes
    /// and the shape the card parses. Keeping the offset would make the card render the
    /// location's time in the machine's own zone.
    /// </summary>
    private static string FormatLocalTime(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private static string FormatLocalDate(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatCoordinate(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string? ReadConditionCode(JsonElement parent)
    {
        if (!parent.TryGetProperty("condition", out JsonElement condition) ||
            condition.ValueKind != JsonValueKind.Object ||
            !condition.TryGetProperty("code", out JsonElement code))
        {
            return null;
        }

        return code.ValueKind switch
        {
            JsonValueKind.String => code.GetString(),
            JsonValueKind.Number => code.TryGetInt32(out int numeric)
                ? numeric.ToString(CultureInfo.InvariantCulture)
                : null,
            _ => null,
        };
    }

    /// <summary>
    /// A measured value with its unit. The account's own unit preference is a console
    /// setting, so the unit that comes back is read rather than assumed - the payload's field
    /// names promise Celsius, and silently publishing Fahrenheit under them would show a
    /// plausible wrong number instead of an obvious one.
    /// </summary>
    private static double ReadTemperatureCelsius(JsonElement parent, string propertyName)
    {
        if (!TryReadTemperatureCelsius(parent, propertyName, out double value))
        {
            throw new InvalidDataException(
                $"QWeather response property '{propertyName}' is invalid.");
        }

        return value;
    }

    private static bool TryReadTemperatureCelsius(
        JsonElement parent,
        string propertyName,
        out double celsius)
    {
        celsius = 0;
        if (!TryReadMeasurement(
                parent,
                propertyName,
                out double value,
                out string? unit))
        {
            return false;
        }

        celsius = unit is not null && unit.Contains('F', StringComparison.OrdinalIgnoreCase)
            ? (value - 32d) * 5d / 9d
            : value;
        return true;
    }

    private static double ReadWindSpeedKmh(JsonElement parent)
    {
        if (!parent.TryGetProperty("wind", out JsonElement wind) ||
            wind.ValueKind != JsonValueKind.Object ||
            !TryReadMeasurement(wind, "speed", out double value, out string? unit))
        {
            throw new InvalidDataException("QWeather response property 'wind' is invalid.");
        }

        return unit switch
        {
            null => value,
            _ when unit.Equals("km/h", StringComparison.OrdinalIgnoreCase) => value,
            _ when unit.Equals("mph", StringComparison.OrdinalIgnoreCase) =>
                value * 1.609344d,
            // The documented default, and the safest reading of anything unrecognised: the
            // vendor states wind speed in metres per second unless the account says otherwise.
            _ => value * 3.6d,
        };
    }

    private static bool TryReadMeasurement(
        JsonElement parent,
        string propertyName,
        out double value,
        out string? unit)
    {
        value = 0;
        unit = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement measurement) ||
            measurement.ValueKind != JsonValueKind.Object ||
            !measurement.TryGetProperty("value", out JsonElement numeric) ||
            numeric.ValueKind != JsonValueKind.Number ||
            !numeric.TryGetDouble(out value) ||
            !double.IsFinite(value))
        {
            value = 0;
            return false;
        }

        if (measurement.TryGetProperty("unit", out JsonElement unitValue) &&
            unitValue.ValueKind == JsonValueKind.String)
        {
            unit = unitValue.GetString();
        }

        return true;
    }

    /// <summary>Relative humidity and cloud cover arrive as a fraction of one.</summary>
    private static double ReadRequiredFraction(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double fraction) ||
            !double.IsFinite(fraction))
        {
            throw new InvalidDataException(
                $"QWeather response property '{propertyName}' is invalid.");
        }

        return fraction;
    }

    private static bool TryReadTimestamp(
        JsonElement parent,
        string propertyName,
        out DateTimeOffset value)
    {
        value = default;
        return parent.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);
    }

    /// <summary>Sunrise and sunset live under "astro" and are absent at high latitudes.</summary>
    private static DateTimeOffset? ReadOptionalTimestamp(
        JsonElement day,
        string propertyName)
    {
        if (!day.TryGetProperty("astro", out JsonElement astro) ||
            astro.ValueKind != JsonValueKind.Object ||
            !TryReadTimestamp(astro, propertyName, out DateTimeOffset value))
        {
            return null;
        }

        return value;
    }

    private static bool TryReadArray(
        JsonElement parent,
        string name,
        out JsonElement array)
    {
        array = default;
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        array = value;
        return true;
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

    /// <summary>
    /// Carries the failure kind and error code an HTTP status maps to, so the three calls can
    /// report a status the same way whether they are the required one or a supplementary one.
    /// </summary>
    private sealed class QWeatherRequestException : Exception
    {
        public QWeatherRequestException(
            ProviderRefreshResultKind kind,
            string errorCode,
            TimeSpan? retryAfter)
            : base($"QWeather request failed: {errorCode}.")
        {
            Kind = kind;
            ErrorCode = errorCode;
            RetryAfter = retryAfter;
        }

        public ProviderRefreshResultKind Kind { get; }

        public string ErrorCode { get; }

        public TimeSpan? RetryAfter { get; }
    }
}

public sealed record QWeatherApiResponse(
    string? ObservedAtLocal,
    double TemperatureC,
    double ApparentTemperatureC,
    double HumidityPercent,
    double WindSpeedKmh,
    int? WeatherCode,
    bool IsDay,
    IReadOnlyList<QWeatherHour> Hourly,
    IReadOnlyList<QWeatherDay> Daily,
    QWeatherDay? Today);

public sealed record QWeatherHour(
    DateTimeOffset ForecastTime,
    double TemperatureC,
    int? WeatherCode);

public sealed record QWeatherDay(
    DateTimeOffset Start,
    double HighTemperatureC,
    double LowTemperatureC,
    int? WeatherCode,
    DateTimeOffset? Sunrise,
    DateTimeOffset? Sunset);
