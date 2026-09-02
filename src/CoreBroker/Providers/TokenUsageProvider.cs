using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Token accounting across every enabled vendor, read from the session records they already
/// write on this machine.
///
/// Like the hardware monitor this provider reaches no network; unlike it, the readings are
/// cumulative rather than instantaneous, so a tick is an incremental read of what was appended
/// since the last one rather than a fresh measurement. That is what keeps a five-second cadence
/// affordable over hundreds of megabytes of history: the first tick reads a day of files, every
/// tick after it reads only the bytes that arrived.
/// </summary>
public sealed class TokenUsageProvider : IProviderRefreshSource
{
    public const string ProviderId = "app.winwidgetboard.tokenusage.sessions";
    public const string Capability = "agent.token-usage";
    public const string DataSourceKey = "local.sessions";
    public const string CardTypeId = "builtin.tokenusage";
    public const string InstanceId = TokenUsageContract.DefaultInstanceId;

    /// <summary>
    /// Which vendors are counted changes the payload's contents but not what a tick reads -
    /// every enabled source is scanned either way - so the request key never moves and the
    /// registration is never rebuilt.
    /// </summary>
    private const string ArgumentsFingerprint = "local";

    private readonly Dictionary<string, ITokenUsageSource> _sources;
    private readonly TokenUsageAggregator _aggregator;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeZoneInfo _timeZone;
    private readonly object _gate = new();
    private IReadOnlyList<string> _enabledVendors;

    public TokenUsageProvider(
        IReadOnlyList<ITokenUsageSource> sources,
        IReadOnlyList<string> enabledVendors,
        Func<DateTimeOffset>? utcNow = null,
        TimeZoneInfo? timeZone = null,
        Func<string?, TokenUsageRate?>? rateLookup = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(enabledVendors);
        _sources = sources.ToDictionary(
            source => source.VendorId,
            StringComparer.Ordinal);
        _enabledVendors = Normalize(enabledVendors);
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _aggregator = new TokenUsageAggregator(_timeZone, rateLookup);
    }

    /// <summary>
    /// The default set of sources: every vendor this build can read. Constructed here rather
    /// than by the caller so the provider and its smoke test cannot disagree about the list.
    /// </summary>
    public static IReadOnlyList<ITokenUsageSource> CreateDefaultSources(
        string? claudeRoot = null,
        string? codexRoot = null) =>
    [
        new ClaudeTokenUsageSource(claudeRoot),
        new CodexTokenUsageSource(codexRoot),
    ];

    public ScheduledProviderDescriptor Descriptor { get; } =
        new(
            ProviderId,
            Capability,
            minimumInterval: TimeSpan.FromSeconds(5),
            visibleInterval: TimeSpan.FromSeconds(5),
            // Null, so nothing is read while the card is off screen. Unlike the hardware
            // monitor there is no taskbar consumer that needs a reading with the panel closed,
            // and the session records keep accumulating whether or not anyone is watching - so
            // a resumed scan loses nothing by having skipped the intervening ticks.
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

    /// <summary>
    /// Which vendors this machine actually has records for right now. Read on demand rather
    /// than cached: a vendor can be installed or removed while the broker runs, and a list
    /// captured at startup would describe the machine as it used to be.
    /// </summary>
    public IReadOnlyList<TokenUsageVendorStatusDto> ListVendors() =>
        TokenUsageContract.VendorIds
            .Select(vendorId => new TokenUsageVendorStatusDto
            {
                VendorId = vendorId,
                IsAvailable = _sources.TryGetValue(vendorId, out ITokenUsageSource? source) &&
                    source.IsAvailable,
            })
            .ToArray();

    /// <summary>
    /// Changes which vendors are counted. A vendor being switched off is forgotten immediately
    /// rather than left to age out of retention, so the card stops including it on the next
    /// tick instead of a day later.
    /// </summary>
    public void SetEnabledVendors(IReadOnlyList<string> enabledVendors)
    {
        ArgumentNullException.ThrowIfNull(enabledVendors);
        string[] next = Normalize(enabledVendors);

        lock (_gate)
        {
            foreach (string vendorId in _enabledVendors)
            {
                if (!next.Contains(vendorId, StringComparer.Ordinal))
                {
                    _aggregator.Forget(vendorId);
                    // Its watermarks go too: re-enabling later must re-read the window rather
                    // than resume from an offset whose records were dropped.
                    if (_sources.TryGetValue(vendorId, out ITokenUsageSource? source))
                    {
                        source.Reset();
                    }
                }
            }

            _enabledVendors = next;
        }
    }

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
        TokenUsageReport report;

        lock (_gate)
        {
            IReadOnlyList<string> enabled = _enabledVendors;
            bool cold = _aggregator.RecordCount == 0;
            DateTimeOffset since = TokenUsageAggregator.RetentionStart(producedAtUtc);

            foreach (string vendorId in enabled)
            {
                if (!_sources.TryGetValue(vendorId, out ITokenUsageSource? source))
                {
                    continue;
                }

                if (cold)
                {
                    // Either the first tick, or the card has been off screen for longer than
                    // the retention window and everything aged out while the watermarks stayed
                    // at end of file. Re-reading the window costs one slow tick and is what
                    // makes a long pause self-healing rather than permanently empty.
                    source.Reset();
                }

                _aggregator.Ingest(source.Scan(since, cancellationToken), producedAtUtc);
            }

            report = _aggregator.Compute(producedAtUtc, enabled);
        }

        JsonElement payload = JsonSerializer.SerializeToElement(
            TokenUsageFormatter.CreatePayload(report, producedAtUtc, _timeZone),
            ContractJson.Options);

        return ValueTask.FromResult(
            new ProviderRefreshResult(
                request.RequestId,
                ProviderRefreshResultKind.Success,
                payload,
                producedAtUtc,
                // Good until the next tick: the numbers move whenever a vendor writes, and
                // there is nothing worth serving stale.
                producedAtUtc.Add(Descriptor.VisibleInterval)));
    }

    /// <summary>
    /// Reduces a requested selection to known vendors in contract order, so the card's pages
    /// are always laid out the same way regardless of the order they were saved in.
    /// </summary>
    private static string[] Normalize(IReadOnlyList<string> vendors) =>
        TokenUsageContract.VendorIds
            .Where(vendorId => vendors.Contains(vendorId, StringComparer.Ordinal))
            .ToArray();
}
