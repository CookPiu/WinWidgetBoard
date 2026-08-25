using System.Globalization;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Owns the single built-in hardware monitor registration and its local settings.
///
/// Unlike the weather runtime this one never rebuilds its registration: one tick reads the
/// whole machine, so changing which readings are displayed changes the payload's contents but
/// not what gets sampled, and the request key never moves.
///
/// It also decides when sampling is allowed to happen at all. A two-second cadence that ran
/// whenever the broker was alive would contradict the product's "run on demand" principle, so
/// the provider stays warm only while the taskbar entry is actively asking for summaries, and
/// otherwise falls back to the card's own visibility.
/// </summary>
public sealed class SystemMonitorRuntime : IDisposable
{
    /// <summary>
    /// How long a summary request keeps the provider warm. The entry polls every two seconds,
    /// so this tolerates a few missed polls without flapping, and stops sampling within a few
    /// seconds of the entry leaving monitor mode or exiting.
    /// </summary>
    private static readonly TimeSpan KeepWarmWindow = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly SystemMonitorSettingsRepository _settingsRepository;
    private readonly ProviderRefreshVisibilityRegistry _visibilityRegistry;
    private readonly IProviderRefreshClock _clock;
    private readonly SystemMonitorProvider _provider;
    private readonly ProviderRefreshHostRegistration _hostRegistration;
    private readonly Timer _keepWarmTimer;
    private SystemMonitorSettingsRecord _settings;
    private DateTimeOffset? _lastSummaryRequestUtc;
    private bool _keptWarm;
    private long _sequence;
    private bool _disposed;

    public SystemMonitorRuntime(
        SystemMonitorSettingsRepository settingsRepository,
        ProviderRefreshHost providerHost,
        ProviderRefreshVisibilityRegistry visibilityRegistry,
        CardSnapshotSubscriptionHub snapshotHub,
        IProviderRefreshClock clock)
    {
        _settingsRepository = settingsRepository ??
            throw new ArgumentNullException(nameof(settingsRepository));
        ArgumentNullException.ThrowIfNull(providerHost);
        _visibilityRegistry = visibilityRegistry ??
            throw new ArgumentNullException(nameof(visibilityRegistry));
        ArgumentNullException.ThrowIfNull(snapshotHub);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        _settings = _settingsRepository.Get(SystemMonitorProvider.InstanceId) ??
            CreateDefaultSettings();
        _provider = new SystemMonitorProvider(
            () => GetSettings().CardItems,
            () => _clock.UtcNow);
        _provider.SetNetworkInterfaceId(_settings.NetworkInterfaceId);

        var adapter = new ProviderCardSnapshotAdapter(
            SystemMonitorProvider.InstanceId,
            SystemMonitorProvider.CardTypeId,
            schemaVersion: 1,
            snapshotHub,
            readyActions: [CardsContract.RefreshActionId],
            failureActions:
            [
                CardsContract.RefreshActionId,
                CardsContract.OpenDiagnosticsActionId,
            ],
            utcNow: () => _clock.UtcNow,
            sequenceProvider: NextSequence);

        _hostRegistration = providerHost.Register(
            new ProviderRefreshSubscription(
                Guid.NewGuid(),
                SystemMonitorProvider.CreateRequestKey(),
                SystemMonitorProvider.CreateArguments(),
                _provider,
                adapter,
                new ProviderRefreshVisibility(
                    PanelVisible: false,
                    InViewport: false,
                    DisplayConnected: false)));

        try
        {
            // Registered cold: nothing samples until the card becomes visible or the entry
            // asks for a summary.
            _visibilityRegistry.Register(
                SystemMonitorProvider.InstanceId,
                _hostRegistration.SubscriptionId,
                keepWarmWithoutPanel: false);
        }
        catch
        {
            _hostRegistration.Dispose();
            _provider.Dispose();
            throw;
        }

        _keepWarmTimer = new Timer(
            _ => ExpireKeepWarm(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public SystemMonitorSettingsRecord GetSettings()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _settings;
        }
    }

    /// <summary>
    /// The adapters the machine can measure right now. Read on demand rather than cached: the
    /// set changes with docking, VPNs and cables, and a list captured at startup would be a
    /// menu of the machine as it used to be.
    /// </summary>
    public static IReadOnlyList<SystemMonitorNetworkInterfaceDto> ListNetworkInterfaces() =>
        SystemMetricSampler.ListSelectableInterfaces();

    public SystemMonitorSettingsRecord SaveSettings(
        string instanceId,
        IReadOnlyList<SystemMonitorItemDto> cardItems,
        IReadOnlyList<SystemMonitorItemDto> entryItems,
        int expectedRevision,
        string? networkInterfaceId = null)
    {
        if (!string.Equals(
                instanceId,
                SystemMonitorProvider.InstanceId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only the built-in hardware monitor instance can be configured.",
                nameof(instanceId));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SystemMonitorSettingsRecord saved = _settingsRepository.Save(
                instanceId,
                cardItems,
                entryItems,
                expectedRevision,
                _clock.UtcNow,
                networkInterfaceId);

            // No republish and no re-registration: the next tick already reads the new list,
            // and the card is at most one cadence behind. The network source is different -
            // it changes what gets sampled, not what gets shown - so it is pushed down to the
            // sampler here rather than read per tick.
            _provider.SetNetworkInterfaceId(saved.NetworkInterfaceId);
            _settings = saved;
            return saved;
        }
    }

    /// <summary>
    /// The taskbar entry's projection, composed from the last tick and the entry's own item
    /// list. Asking is itself the demand signal that keeps the provider sampling.
    /// </summary>
    public SystemMonitorSummaryDto? TryGetSummary()
    {
        SystemMonitorSettingsRecord settings;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            settings = _settings;
        }

        NoteSummaryRequested();

        SystemMetricSample? sample = _provider.LastSample;
        if (sample is null)
        {
            // Nothing has been sampled yet. The entry renders its own unavailable state
            // rather than a row of placeholders that look like readings.
            return null;
        }

        IReadOnlyList<SystemMetricSample> recent = _provider.RecentSamples;
        var segments = new List<SystemMonitorSegmentDto>(settings.EntryItems.Count);
        foreach (SystemMonitorItemDto item in settings.EntryItems)
        {
            if (item.MetricId is null)
            {
                continue;
            }

            SystemMonitorSegmentDto segment =
                SystemMonitorFormatter.FormatSegment(sample, item.MetricId, item.Detail);
            segments.Add(segment with
            {
                History = SystemMonitorHistory.Normalize(recent, item.MetricId),
            });
        }

        return new SystemMonitorSummaryDto
        {
            InstanceId = SystemMonitorProvider.InstanceId,
            Segments = segments,
            SampledAtUtc = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
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
        }

        _keepWarmTimer.Dispose();
        _hostRegistration.Dispose();
        _provider.Dispose();
    }

    private void NoteSummaryRequested()
    {
        bool startSampling;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lastSummaryRequestUtc = _clock.UtcNow;
            startSampling = !_keptWarm;
            _keptWarm = true;
        }

        if (startSampling)
        {
            // The counters kept running while nobody watched; start a fresh window rather
            // than reporting a delta that spans the whole idle period.
            _provider.ResetBaseline();
            _visibilityRegistry.SetKeptWarm(SystemMonitorProvider.InstanceId, true);
        }

        // Re-arm rather than run a periodic timer: when nothing is asking, no timer exists.
        _keepWarmTimer.Change(KeepWarmWindow, Timeout.InfiniteTimeSpan);
    }

    private void ExpireKeepWarm()
    {
        bool stopSampling = false;
        TimeSpan? rearm = null;

        lock (_gate)
        {
            if (_disposed || !_keptWarm)
            {
                return;
            }

            TimeSpan sinceRequest = _lastSummaryRequestUtc is { } requestedAt
                ? _clock.UtcNow - requestedAt
                : KeepWarmWindow;
            if (sinceRequest >= KeepWarmWindow)
            {
                _keptWarm = false;
                stopSampling = true;
            }
            else
            {
                rearm = KeepWarmWindow - sinceRequest;
            }
        }

        if (stopSampling)
        {
            _visibilityRegistry.SetKeptWarm(SystemMonitorProvider.InstanceId, false);
        }
        else if (rearm is { } delay)
        {
            _keepWarmTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    private static SystemMonitorSettingsRecord CreateDefaultSettings() =>
        new(
            SystemMonitorProvider.InstanceId,
            SystemMonitorContract.DefaultCardItems,
            SystemMonitorContract.DefaultEntryItems,
            revision: 0,
            updatedAtUtc: null);
}
