using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Ipc;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Owns the single built-in token-usage registration.
///
/// Much thinner than the other provider runtimes because there is nothing to own beyond the
/// registration itself: the reading has no settings, so no repository and no re-registration,
/// and it has no consumer outside the card, so no keep-warm window. The card's visibility is
/// the only thing that decides whether anything is read at all.
/// </summary>
public sealed class TokenUsageRuntime : IDisposable
{
    private readonly ProviderRefreshVisibilityRegistry _visibilityRegistry;
    private readonly TokenUsageProvider _provider;
    private readonly ProviderRefreshHostRegistration _hostRegistration;
    private long _sequence;
    private bool _disposed;

    public TokenUsageRuntime(
        ProviderRefreshHost providerHost,
        ProviderRefreshVisibilityRegistry visibilityRegistry,
        CardSnapshotSubscriptionHub snapshotHub,
        IProviderRefreshClock clock,
        string? transcriptRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(providerHost);
        _visibilityRegistry = visibilityRegistry ??
            throw new ArgumentNullException(nameof(visibilityRegistry));
        ArgumentNullException.ThrowIfNull(snapshotHub);
        ArgumentNullException.ThrowIfNull(clock);

        _provider = new TokenUsageProvider(
            () => clock.UtcNow,
            transcriptRootDirectory);

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
            utcNow: () => clock.UtcNow,
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hostRegistration.Dispose();
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);
}
