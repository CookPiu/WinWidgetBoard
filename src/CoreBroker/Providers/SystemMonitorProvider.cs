using System.Globalization;
using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// The local hardware monitor. Unlike the weather provider this one reaches no network and
/// keeps no cache: every tick is a fresh measurement of the machine it runs on, and a reading
/// two minutes old is worth nothing, so there is nothing to serve stale.
/// </summary>
public sealed class SystemMonitorProvider : IProviderRefreshSource, IDisposable
{
    public const string ProviderId = "app.winwidgetboard.sysmon.local";
    public const string Capability = "system.metrics";
    public const string DataSourceKey = "local.hardware";
    public const string CardTypeId = "builtin.sysmon";
    public const string InstanceId = SystemMonitorContract.DefaultInstanceId;

    /// <summary>
    /// Sampling does not vary with configuration - one tick reads the whole machine - so the
    /// request key never changes and the registration is never rebuilt.
    /// </summary>
    private const string ArgumentsFingerprint = "local";

    private readonly SystemMetricSampler _sampler;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IReadOnlyList<SystemMonitorItemDto>> _cardItemsProvider;
    private readonly object _sampleGate = new();
    // The recent window the taskbar sparkline is drawn from, oldest first. Bounded by the
    // contract's own cap, so the memory this costs is a fixed handful of small records rather
    // than something that grows with uptime.
    private readonly Queue<SystemMetricSample> _recentSamples = new();
    private SystemMetricSample? _lastSample;
    private bool _disposed;

    public SystemMonitorProvider(
        Func<IReadOnlyList<SystemMonitorItemDto>> cardItemsProvider,
        Func<DateTimeOffset>? utcNow = null)
    {
        _cardItemsProvider = cardItemsProvider ??
            throw new ArgumentNullException(nameof(cardItemsProvider));
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _sampler = new SystemMetricSampler();
    }

    public ScheduledProviderDescriptor Descriptor { get; } =
        new(
            ProviderId,
            Capability,
            minimumInterval: TimeSpan.FromSeconds(2),
            visibleInterval: TimeSpan.FromSeconds(2),
            // The taskbar entry can show readings with no panel open, so the hidden cadence
            // matches the visible one rather than backing off. What keeps this bounded is not
            // a slower interval but the fact that nothing samples at all unless the card is on
            // screen or the entry asked for a summary in the last few seconds.
            hiddenInterval: TimeSpan.FromSeconds(2),
            powerSaverInterval: null,
            requestTimeout: TimeSpan.FromSeconds(5),
            requiresNetwork: false,
            supportsManualRefresh: false,
            manualRefreshMinimumInterval: TimeSpan.FromSeconds(2),
            new ProviderBackoffOptions(
                TimeSpan.FromSeconds(4),
                TimeSpan.FromMinutes(1),
                jitterRatio: 0.2));

    /// <summary>
    /// The most recent tick, for composing the taskbar summary without sampling a second time.
    /// Null until the first tick.
    /// </summary>
    public SystemMetricSample? LastSample
    {
        get
        {
            lock (_sampleGate)
            {
                return _lastSample;
            }
        }
    }

    /// <summary>
    /// The recent samples, oldest first. A copy: the caller formats at its own pace and must
    /// not see the window shift underneath it.
    /// </summary>
    public IReadOnlyList<SystemMetricSample> RecentSamples
    {
        get
        {
            lock (_sampleGate)
            {
                return _recentSamples.ToArray();
            }
        }
    }

    public static JsonElement CreateArguments() =>
        JsonSerializer.SerializeToElement(
            new { source = DataSourceKey },
            ContractJson.Options);

    public static ProviderRequestKey CreateRequestKey() =>
        new(ProviderId, Capability, DataSourceKey, ArgumentsFingerprint);

    /// <summary>
    /// Drops the deltas so the next tick starts a fresh measurement window. Called when the
    /// provider has been idle: the counters kept running while nobody watched, and a delta
    /// spanning the whole idle period describes no moment in particular.
    /// </summary>
    public void ResetBaseline()
    {
        lock (_sampleGate)
        {
            if (!_disposed)
            {
                _sampler.ResetBaseline();
                // The window would otherwise splice a fresh measurement onto samples from
                // before the idle gap, drawing a continuous line across a hole in time.
                _recentSamples.Clear();
            }
        }
    }

    public ValueTask<ProviderRefreshResult> FetchAsync(
        ProviderRefreshRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset producedAtUtc = _utcNow().ToUniversalTime();
        SystemMetricSample sample;
        lock (_sampleGate)
        {
            if (_disposed)
            {
                return ValueTask.FromResult(
                    new ProviderRefreshResult(
                        request.RequestId,
                        ProviderRefreshResultKind.Unavailable,
                        default,
                        producedAtUtc,
                        errorCode: "sysmon.disposed"));
            }

            sample = _sampler.Sample(producedAtUtc);
            _lastSample = sample;
            _recentSamples.Enqueue(sample);
            while (_recentSamples.Count > SystemMonitorContract.MaxHistorySamples)
            {
                _recentSamples.Dequeue();
            }
        }

        JsonElement payload = CreatePayload(sample, producedAtUtc);
        return ValueTask.FromResult(
            new ProviderRefreshResult(
                request.RequestId,
                ProviderRefreshResultKind.Success,
                payload,
                producedAtUtc,
                // A reading is only good until the next tick; there is no useful cache window.
                producedAtUtc.Add(Descriptor.VisibleInterval)));
    }

    public void Dispose()
    {
        lock (_sampleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sampler.Dispose();
        }
    }

    private JsonElement CreatePayload(
        SystemMetricSample sample,
        DateTimeOffset producedAtUtc)
    {
        IReadOnlyList<SystemMonitorItemDto> items = _cardItemsProvider();
        var metrics = new List<SystemMonitorMetricDto>(items.Count);
        foreach (SystemMonitorItemDto item in items)
        {
            if (item.MetricId is null)
            {
                continue;
            }

            metrics.Add(
                SystemMonitorFormatter.FormatMetric(sample, item.MetricId, item.Detail));
        }

        return JsonSerializer.SerializeToElement(
            new SystemMonitorCardPayloadDto
            {
                Metrics = metrics,
                SampledAtUtc = producedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            },
            ContractJson.Options);
    }
}
