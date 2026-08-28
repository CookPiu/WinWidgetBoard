using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Token accounting for the agent sessions on this machine, read from the transcripts it
/// already writes.
///
/// Like the hardware monitor this provider reaches no network; unlike it, the readings are
/// cumulative rather than instantaneous, so a tick is an incremental read of what was appended
/// since the last one rather than a fresh measurement. That is what keeps a five-second cadence
/// affordable over tens of megabytes of history: the first tick reads a day of files, every
/// tick after it reads only the bytes that arrived.
/// </summary>
public sealed class TokenUsageProvider : IProviderRefreshSource
{
    public const string ProviderId = "app.winwidgetboard.tokenusage.transcript";
    public const string Capability = "agent.token-usage";
    public const string DataSourceKey = "local.transcripts";
    public const string CardTypeId = "builtin.tokenusage";
    public const string InstanceId = TokenUsageContract.DefaultInstanceId;

    /// <summary>
    /// Nothing about the reading is configurable, so the request key never moves and the
    /// registration is never rebuilt.
    /// </summary>
    private const string ArgumentsFingerprint = "local";

    private readonly TranscriptUsageScanner _scanner;
    private readonly TokenUsageAggregator _aggregator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();

    public TokenUsageProvider(
        Func<DateTimeOffset>? utcNow = null,
        string? transcriptRootDirectory = null,
        TimeZoneInfo? timeZone = null)
    {
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _scanner = new TranscriptUsageScanner(transcriptRootDirectory);
        _aggregator = new TokenUsageAggregator(timeZone);
    }

    public ScheduledProviderDescriptor Descriptor { get; } =
        new(
            ProviderId,
            Capability,
            minimumInterval: TimeSpan.FromSeconds(5),
            visibleInterval: TimeSpan.FromSeconds(5),
            // Null, so nothing is read while the card is off screen. Unlike the hardware
            // monitor there is no taskbar consumer that needs a reading with the panel closed,
            // and the transcripts keep accumulating whether or not anyone is watching - so a
            // resumed scan loses nothing by having skipped the intervening ticks.
            hiddenInterval: null,
            powerSaverInterval: TimeSpan.FromSeconds(30),
            requestTimeout: TimeSpan.FromSeconds(10),
            requiresNetwork: false,
            supportsManualRefresh: true,
            manualRefreshMinimumInterval: TimeSpan.FromSeconds(5),
            new ProviderBackoffOptions(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMinutes(2),
                jitterRatio: 0.2));

    public static JsonElement CreateArguments() =>
        JsonSerializer.SerializeToElement(
            new { source = DataSourceKey },
            ContractJson.Options);

    public static ProviderRequestKey CreateRequestKey() =>
        new(ProviderId, Capability, DataSourceKey, ArgumentsFingerprint);

    public ValueTask<ProviderRefreshResult> FetchAsync(
        ProviderRefreshRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset producedAtUtc = _utcNow().ToUniversalTime();
        TokenUsageAggregate aggregate;

        lock (_gate)
        {
            if (_aggregator.RecordCount == 0)
            {
                // Either the first tick, or the card has been off screen for longer than the
                // retention window and everything aged out while the watermarks stayed at end
                // of file. Re-reading the window costs one slow tick and is what makes a long
                // pause self-healing instead of permanently showing an empty day.
                _scanner.Reset();
            }

            IReadOnlyList<TranscriptUsageRecord> records = _scanner.Scan(
                TokenUsageAggregator.RetentionStart(producedAtUtc),
                cancellationToken);
            _aggregator.Ingest(records, producedAtUtc);
            aggregate = _aggregator.Compute(producedAtUtc);
        }

        JsonElement payload = JsonSerializer.SerializeToElement(
            TokenUsageFormatter.CreatePayload(aggregate, producedAtUtc),
            ContractJson.Options);

        return ValueTask.FromResult(
            new ProviderRefreshResult(
                request.RequestId,
                ProviderRefreshResultKind.Success,
                payload,
                producedAtUtc,
                // Good until the next tick: the numbers move whenever the agent writes, and
                // there is nothing worth serving stale.
                producedAtUtc.Add(Descriptor.VisibleInterval)));
    }
}
