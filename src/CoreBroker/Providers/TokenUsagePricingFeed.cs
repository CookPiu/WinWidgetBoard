using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Reads the LiteLLM community price list - the one machine-readable, MIT-licensed feed that
/// tracks the vendors' published rates - and keeps only what this card can use: first-party
/// Anthropic and OpenAI model ids with their per-token prices, converted to the per-million
/// shape the built-in table uses.
///
/// The feed is two megabytes covering thousands of models across a hundred providers. It is
/// streamed with <see cref="Utf8JsonReader"/> and reduced to a few dozen rows before anything
/// is kept, so neither the raw document nor the other providers' entries ever reach memory as
/// objects or the database as rows (ADR-0035).
/// </summary>
public static class TokenUsagePricingFeed
{
    public const string DataSourceKey = "raw.githubusercontent.com/BerriAI/litellm";

    public static readonly Uri DefaultEndpoint = new(
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json",
        UriKind.Absolute);

    /// <summary>
    /// The feed was 2.0 MB on 2026-09-02 and grows with every model release; four times that
    /// leaves years of headroom while still refusing anything that is clearly not the feed.
    /// </summary>
    public const int MaxResponseBytes = 8 * 1024 * 1024;

    private const double PerMillion = 1_000_000d;

    /// <summary>
    /// Filters and converts the feed. Only the exact first-party ids are kept - the feed also
    /// lists every routed alias ("bedrock/...", "openrouter/...") and those bill differently,
    /// which is the same reason the built-in table refuses prefix matching.
    /// </summary>
    public static IReadOnlyDictionary<string, TokenUsageRate> Parse(ReadOnlySpan<byte> payload)
    {
        var rates = new Dictionary<string, TokenUsageRate>(StringComparer.OrdinalIgnoreCase);
        var reader = new Utf8JsonReader(
            payload,
            new JsonReaderOptions { AllowTrailingCommas = true, MaxDepth = 8 });

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The pricing feed is not a JSON object.");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string model = reader.GetString() ?? string.Empty;
            if (!reader.Read())
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            if (!IsCandidateModel(model))
            {
                reader.Skip();
                continue;
            }

            if (TryReadEntry(ref reader, out TokenUsageRate rate))
            {
                rates[model] = rate;
            }
        }

        return rates;
    }

    /// <summary>
    /// Only ids shaped like a first-party model name. Anything with a provider prefix or an
    /// unexpected character is an alias or a routing entry and is not what a transcript
    /// records as the model.
    /// </summary>
    public static bool IsCandidateModel(string model)
    {
        if (model.Length is 0 or > 64 ||
            !(model.StartsWith("claude-", StringComparison.Ordinal) ||
                model.StartsWith("gpt-", StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (char character in model)
        {
            if (!(char.IsAsciiLetterLower(character) ||
                    char.IsAsciiDigit(character) ||
                    character is '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadEntry(ref Utf8JsonReader reader, out TokenUsageRate rate)
    {
        rate = default;
        string? provider = null;
        double? input = null;
        double? output = null;
        double? cacheWrite = null;
        double? cacheWrite1h = null;
        double? cacheRead = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("litellm_provider"u8))
            {
                reader.Read();
                provider = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("input_cost_per_token"u8))
            {
                input = ReadNumber(ref reader);
            }
            else if (reader.ValueTextEquals("output_cost_per_token"u8))
            {
                output = ReadNumber(ref reader);
            }
            else if (reader.ValueTextEquals("cache_creation_input_token_cost"u8))
            {
                cacheWrite = ReadNumber(ref reader);
            }
            else if (reader.ValueTextEquals("cache_creation_input_token_cost_above_1hr"u8))
            {
                cacheWrite1h = ReadNumber(ref reader);
            }
            else if (reader.ValueTextEquals("cache_read_input_token_cost"u8))
            {
                cacheRead = ReadNumber(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (provider is not ("anthropic" or "openai") ||
            input is not { } inputRate ||
            output is not { } outputRate ||
            inputRate < 0 ||
            outputRate < 0)
        {
            return false;
        }

        // The vendors' own multipliers stand in for a missing cache field: a 5-minute write is
        // 1.25x base input at both, and a cache read is 0.1x. OpenAI publishes no 1-hour tier,
        // so its 1-hour write is its only write.
        double write5m = cacheWrite is { } explicitWrite && explicitWrite >= 0
            ? explicitWrite
            : inputRate * 1.25d;
        double write1h = cacheWrite1h is { } explicitWrite1h && explicitWrite1h >= 0
            ? explicitWrite1h
            : write5m;
        double read = cacheRead is { } explicitRead && explicitRead >= 0
            ? explicitRead
            : inputRate * 0.1d;

        rate = new TokenUsageRate(
            ToPerMillion(inputRate),
            ToPerMillion(outputRate),
            ToPerMillion(write5m),
            ToPerMillion(write1h),
            ToPerMillion(read));
        return true;
    }

    private static double? ReadNumber(ref Utf8JsonReader reader)
    {
        reader.Read();
        return reader.TokenType == JsonTokenType.Number &&
            reader.TryGetDouble(out double value) &&
            double.IsFinite(value)
            ? value
            : null;
    }

    private static decimal ToPerMillion(double perToken) =>
        Math.Round((decimal)(perToken * PerMillion), 6, MidpointRounding.AwayFromZero);
}

public enum TokenUsagePricingFetchKind
{
    Updated,
    NotModified,
}

/// <summary>The outcome of one attempt, whether scheduled or asked for by the user.</summary>
public enum TokenUsagePricingAttemptResult
{
    Updated,
    NotModified,
    Failed,
}

public sealed record TokenUsagePricingFetchResult(
    TokenUsagePricingFetchKind Kind,
    IReadOnlyDictionary<string, TokenUsageRate> Rates,
    string? ETag);

/// <summary>
/// One GET a day against the feed, conditional on the ETag from last time so an unchanged
/// document costs a 304 rather than two megabytes. No account, no identifier, nothing about
/// this machine leaves with the request; the response is bounded and parsed to a few dozen
/// rates before it is kept.
/// </summary>
public sealed class TokenUsagePricingFeedClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    public TokenUsagePricingFeedClient(HttpClient httpClient, Uri? endpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? TokenUsagePricingFeed.DefaultEndpoint;
        if (!_endpoint.IsAbsoluteUri ||
            !string.Equals(_endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The pricing feed endpoint must be an absolute HTTPS URI.",
                nameof(endpoint));
        }

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("WinWidgetBoard", "0.1"));
        }
    }

    public async Task<TokenUsagePricingFetchResult> FetchAsync(
        string? etag,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        if (!string.IsNullOrEmpty(etag) &&
            EntityTagHeaderValue.TryParse(etag, out EntityTagHeaderValue? parsed))
        {
            request.Headers.IfNoneMatch.Add(parsed);
        }

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new TokenUsagePricingFetchResult(
                TokenUsagePricingFetchKind.NotModified,
                new Dictionary<string, TokenUsageRate>(),
                etag);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"Pricing feed request failed with status {(int)response.StatusCode}.");
        }

        byte[] payload = await ReadBoundedAsync(response.Content, timeout.Token)
            .ConfigureAwait(false);
        IReadOnlyDictionary<string, TokenUsageRate> rates = TokenUsagePricingFeed.Parse(payload);
        if (rates.Count == 0)
        {
            // A document with none of the models this card prices is not an update, it is a
            // changed feed; keeping the previous rates is safer than replacing them with none.
            throw new InvalidDataException("The pricing feed contained no usable rates.");
        }

        return new TokenUsagePricingFetchResult(
            TokenUsagePricingFetchKind.Updated,
            rates,
            response.Headers.ETag?.ToString());
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > TokenUsagePricingFeed.MaxResponseBytes)
        {
            throw new InvalidDataException(
                $"Pricing feed exceeds {TokenUsagePricingFeed.MaxResponseBytes} bytes.");
        }

        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > TokenUsagePricingFeed.MaxResponseBytes)
            {
                throw new InvalidDataException(
                    $"Pricing feed exceeds {TokenUsagePricingFeed.MaxResponseBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>
/// The daily cadence. Runs as one background loop in the broker: apply what the database
/// remembers, then fetch once a day while the user has the sync switched on, retrying an
/// hour after a failure. A toggle turned on wakes it immediately so the first fetch does not
/// wait for tomorrow.
///
/// Cadence is a pure function of timestamps so it can be tested against a fake clock.
/// </summary>
public sealed class TokenUsagePricingSyncer : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly Func<string?, CancellationToken, Task<TokenUsagePricingFetchResult>> _fetch;
    private readonly Persistence.TokenUsagePricingCacheRepository _cache;
    private readonly TokenUsageRateBook _rateBook;
    private readonly IProviderRefreshClock _clock;
    private readonly Func<bool> _isEnabled;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    // One attempt at a time: a click on "sync now" while the daily tick is mid-fetch waits
    // for it rather than starting a second download of the same document.
    private readonly SemaphoreSlim _attemptGate = new(1, 1);
    private CancellationTokenSource _wake = new();
    private string? _etag;
    private DateTimeOffset? _lastAttemptUtc;
    private bool _lastAttemptFailed;
    private bool _disposed;

    public TokenUsagePricingSyncer(
        Func<string?, CancellationToken, Task<TokenUsagePricingFetchResult>> fetch,
        Persistence.TokenUsagePricingCacheRepository cache,
        TokenUsageRateBook rateBook,
        IProviderRefreshClock clock,
        Func<bool> isEnabled,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _rateBook = rateBook ?? throw new ArgumentNullException(nameof(rateBook));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _delay = delay ?? Task.Delay;

        // What the database remembers is applied before the first tick, so a build that
        // starts offline still prices with the last feed it saw rather than the built-in
        // table alone.
        Persistence.TokenUsagePricingCacheRecord? cached = _cache.Get();
        if (cached is not null)
        {
            _etag = cached.ETag;
            _rateBook.ApplySynced(cached.Rates, cached.FetchedAtUtc);
        }
    }

    /// <summary>Failures are surfaced for diagnostics, not shown to the user.</summary>
    public int FailureCount { get; private set; }

    /// <summary>
    /// When the next attempt is due. Never fetched: now. Fetched: a day after. Failed: an
    /// hour after the failure, so a flaky connection retries within the day rather than
    /// waiting for the next one.
    /// </summary>
    public static DateTimeOffset NextDueUtc(
        DateTimeOffset? lastSuccessUtc,
        DateTimeOffset? lastAttemptUtc,
        bool lastAttemptFailed,
        DateTimeOffset nowUtc)
    {
        if (lastAttemptFailed && lastAttemptUtc is { } failedAt)
        {
            return failedAt + RetryInterval;
        }

        return lastSuccessUtc is { } success ? success + RefreshInterval : nowUtc;
    }

    /// <summary>Wakes the loop, for a toggle that has just been switched on.</summary>
    public void Trigger()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _wake.Cancel();
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            CancellationTokenSource wake;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_wake.IsCancellationRequested)
                {
                    _wake.Dispose();
                    _wake = new CancellationTokenSource();
                }

                wake = _wake;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                wake.Token);

            if (!_isEnabled())
            {
                await WaitAsync(RefreshInterval, linked.Token).ConfigureAwait(false);
                continue;
            }

            DateTimeOffset now = _clock.UtcNow;
            DateTimeOffset due = NextDueUtc(
                _rateBook.SyncedAtUtc,
                _lastAttemptUtc,
                _lastAttemptFailed,
                now);
            if (due > now)
            {
                await WaitAsync(due - now, linked.Token).ConfigureAwait(false);
                if (linked.IsCancellationRequested)
                {
                    continue;
                }
            }

            await AttemptAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<TokenUsagePricingAttemptResult> AttemptAsync(
        CancellationToken cancellationToken)
    {
        await _attemptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset startedAt = _clock.UtcNow;
            _lastAttemptUtc = startedAt;
            try
            {
                TokenUsagePricingFetchResult result = await _fetch(_etag, cancellationToken)
                    .ConfigureAwait(false);
                DateTimeOffset finishedAt = _clock.UtcNow;
                _lastAttemptFailed = false;
                if (result.Kind == TokenUsagePricingFetchKind.Updated)
                {
                    _etag = result.ETag;
                    _cache.Save(new Persistence.TokenUsagePricingCacheRecord(
                        result.Rates,
                        result.ETag,
                        finishedAt));
                    _rateBook.ApplySynced(result.Rates, finishedAt);
                    return TokenUsagePricingAttemptResult.Updated;
                }

                _cache.Touch(finishedAt);
                _rateBook.MarkChecked(finishedAt);
                return TokenUsagePricingAttemptResult.NotModified;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                    IOException or
                    JsonException or
                    InvalidDataException or
                    OperationCanceledException or
                    Persistence.SqliteException)
            {
                _lastAttemptFailed = true;
                FailureCount++;
                return TokenUsagePricingAttemptResult.Failed;
            }
        }
        finally
        {
            _attemptGate.Release();
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
            _wake.Cancel();
            _wake.Dispose();
        }

        _attemptGate.Dispose();
    }

    private async Task WaitAsync(TimeSpan duration, CancellationToken token)
    {
        try
        {
            await _delay(duration, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either the broker is stopping (the loop checks its own token next) or a toggle
            // woke us; both are answered by going round again.
        }
    }
}
