using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Owns the single built-in token-usage registration and its vendor selection.
///
/// Like the hardware monitor's runtime it never rebuilds its registration: changing which
/// vendors are counted changes the payload's contents but not the request key. It has no
/// keep-warm window either, because nothing outside the card consumes the reading - the card's
/// visibility is the only thing that decides whether anything is read at all.
/// </summary>
public sealed class TokenUsageRuntime : IDisposable
{
    private readonly object _gate = new();
    private readonly TokenUsageSettingsRepository _settingsRepository;
    private readonly ProviderRefreshVisibilityRegistry _visibilityRegistry;
    private readonly IProviderRefreshClock _clock;
    private readonly TokenUsageProvider _provider;
    private readonly ProviderRefreshHostRegistration _hostRegistration;
    private TokenUsageSettingsRecord _settings;
    private long _sequence;
    private bool _disposed;

    public TokenUsageRuntime(
        TokenUsageSettingsRepository settingsRepository,
        ProviderRefreshHost providerHost,
        ProviderRefreshVisibilityRegistry visibilityRegistry,
        CardSnapshotSubscriptionHub snapshotHub,
        IProviderRefreshClock clock,
        IReadOnlyList<ITokenUsageSource>? sources = null)
    {
        _settingsRepository = settingsRepository ??
            throw new ArgumentNullException(nameof(settingsRepository));
        ArgumentNullException.ThrowIfNull(providerHost);
        _visibilityRegistry = visibilityRegistry ??
            throw new ArgumentNullException(nameof(visibilityRegistry));
        ArgumentNullException.ThrowIfNull(snapshotHub);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        _settings = _settingsRepository.Get(TokenUsageProvider.InstanceId) ??
            CreateDefaultSettings();
        _provider = new TokenUsageProvider(
            sources ?? TokenUsageProvider.CreateDefaultSources(),
            _settings.EnabledVendors,
            () => _clock.UtcNow);

        var adapter = new ProviderCardSnapshotAdapter(
            TokenUsageProvider.InstanceId,
            TokenUsageProvider.CardTypeId,
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
                TokenUsageProvider.CreateRequestKey(),
                TokenUsageProvider.CreateArguments(),
                _provider,
                adapter,
                new ProviderRefreshVisibility(
                    PanelVisible: false,
                    InViewport: false,
                    DisplayConnected: false)));

        try
        {
            // Registered cold: nothing is read until the card is on screen.
            _visibilityRegistry.Register(
                TokenUsageProvider.InstanceId,
                _hostRegistration.SubscriptionId,
                keepWarmWithoutPanel: false);
        }
        catch
        {
            _hostRegistration.Dispose();
            throw;
        }
    }

    public TokenUsageSettingsRecord GetSettings()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _settings;
        }
    }

    /// <summary>
    /// Which vendors this machine actually has records for right now, for the settings surface
    /// to offer. Not stored state: a vendor can be installed or removed between two openings of
    /// the dialog.
    /// </summary>
    public IReadOnlyList<TokenUsageVendorStatusDto> ListVendors() => _provider.ListVendors();

    public TokenUsageSettingsRecord SaveSettings(
        string instanceId,
        IReadOnlyList<string> enabledVendors,
        int expectedRevision)
    {
        if (!string.Equals(
                instanceId,
                TokenUsageProvider.InstanceId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only the built-in token usage instance can be configured.",
                nameof(instanceId));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            TokenUsageSettingsRecord saved = _settingsRepository.Save(
                instanceId,
                enabledVendors,
                expectedRevision,
                _clock.UtcNow);

            // Pushed down rather than read per tick: switching a vendor off has to drop its
            // records now, not merely stop counting new ones.
            _provider.SetEnabledVendors(saved.EnabledVendors);
            _settings = saved;
            return saved;
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
        }

        _hostRegistration.Dispose();
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// Everything known is enabled until the user says otherwise: the card's job is to say what
    /// was spent, and a vendor silently left out of a total is the one failure mode that cannot
    /// be noticed by looking at it.
    /// </summary>
    private static TokenUsageSettingsRecord CreateDefaultSettings() =>
        new(
            TokenUsageProvider.InstanceId,
            TokenUsageContract.VendorIds,
            revision: 0,
            updatedAtUtc: null);
}
