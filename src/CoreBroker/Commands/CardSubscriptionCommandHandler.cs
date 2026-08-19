using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Providers;
using static WinWidgetBoard.CoreBroker.Commands.CoreBrokerCommandSupport;

namespace WinWidgetBoard.CoreBroker.Commands;

internal sealed class CardSubscriptionCommandHandler
{
    private readonly CardSnapshotSubscriptionHub? _cardSnapshotSubscriptionHub;
    private readonly ProviderRefreshVisibilityRegistry? _providerVisibilityRegistry;

    public CardSubscriptionCommandHandler(
        CardSnapshotSubscriptionHub? cardSnapshotSubscriptionHub,
        ProviderRefreshVisibilityRegistry? providerVisibilityRegistry)
    {
        _cardSnapshotSubscriptionHub = cardSnapshotSubscriptionHub;
        _providerVisibilityRegistry = providerVisibilityRegistry;
    }

    public bool SubscriptionsAvailable => _cardSnapshotSubscriptionHub is not null;

    public Envelope Handle(
        Envelope request,
        Guid connectionId,
        out CardSnapshotSubscription? subscription)
    {
        subscription = null;
        if (_cardSnapshotSubscriptionHub is null)
        {
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (connectionId == Guid.Empty ||
            !TryDeserializePayload(
                request.Payload,
                out CardsSubscribeRequest? payload) ||
            payload is null)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        try
        {
            if (!_cardSnapshotSubscriptionHub.TrySubscribe(
                    connectionId,
                    payload,
                    out CardsSubscribeResponse? responsePayload,
                    out subscription) ||
                responsePayload is null ||
                subscription is null)
            {
                subscription = null;
                return ErrorResponse(request, "validation.invalid-argument", "validation");
            }

            _providerVisibilityRegistry?.Apply(connectionId, payload);

            return SuccessResponse(
                request,
                CardsContract.SubscribeMethod,
                responsePayload);
        }
        catch (ObjectDisposedException)
        {
            subscription = null;
            return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }
    }

    public void RemoveConnection(Guid connectionId)
    {
        _cardSnapshotSubscriptionHub?.Remove(connectionId);
        _providerVisibilityRegistry?.Remove(connectionId);
    }
}
