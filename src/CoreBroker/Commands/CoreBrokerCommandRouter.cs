using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;
using WinWidgetBoard.CoreBroker.Providers;
using static WinWidgetBoard.CoreBroker.Commands.CoreBrokerCommandSupport;

namespace WinWidgetBoard.CoreBroker.Commands;

public sealed class CoreBrokerCommandRouter
{
    private const string SessionPingMethod = "session.ping";

    private readonly object _gate = new();
    private readonly NoteCommandHandler? _noteCommandHandler;
    private readonly LayoutCommandHandler? _layoutCommandHandler;
    private readonly WeatherSettingsCommandHandler? _weatherSettingsCommandHandler;
    private readonly CardSubscriptionCommandHandler _cardSubscriptionCommandHandler;
    private readonly PanelVisibilityCommandHandler _panelVisibilityCommandHandler;

    public CoreBrokerCommandRouter(
        NoteRepository? noteRepository = null,
        LayoutRepository? layoutRepository = null,
        CardSnapshotSubscriptionHub? cardSnapshotSubscriptionHub = null,
        ProviderRefreshVisibilityRegistry? providerVisibilityRegistry = null,
        WeatherProviderRuntime? weatherProviderRuntime = null)
    {
        _noteCommandHandler = noteRepository is null
            ? null
            : new NoteCommandHandler(noteRepository, _gate);
        _layoutCommandHandler = layoutRepository is null
            ? null
            : new LayoutCommandHandler(layoutRepository, _gate);
        _weatherSettingsCommandHandler = weatherProviderRuntime is null
            ? null
            : new WeatherSettingsCommandHandler(weatherProviderRuntime, _gate);
        _cardSubscriptionCommandHandler = new CardSubscriptionCommandHandler(
            cardSnapshotSubscriptionHub,
            providerVisibilityRegistry);
        _panelVisibilityCommandHandler = new PanelVisibilityCommandHandler(_gate);
    }

    public bool NotesAvailable => _noteCommandHandler is not null;

    public bool LayoutsAvailable => _layoutCommandHandler is not null;

    public bool CardsAvailable => _cardSubscriptionCommandHandler.SubscriptionsAvailable;

    public bool WeatherSettingsAvailable => _weatherSettingsCommandHandler is not null;

    public bool PanelVisible => _panelVisibilityCommandHandler.PanelVisible;

    public long VisibilityRevision => _panelVisibilityCommandHandler.VisibilityRevision;

    public int VisibilityReportCount => _panelVisibilityCommandHandler.VisibilityReportCount;

    public Envelope Handle(Envelope request) =>
        Handle(request, Guid.Empty, out _);

    public Envelope Handle(
        Envelope request,
        Guid connectionId,
        out CardSnapshotSubscription? subscription)
    {
        subscription = null;
        if (request.MessageType != EnvelopeMessageType.Request)
        {
            return ErrorResponse(request, "validation.invalid-argument", "validation");
        }

        if (string.Equals(request.Method, SessionPingMethod, StringComparison.Ordinal))
        {
            return SuccessResponse(
                request,
                SessionPingMethod,
                new
                {
                    serverTimeUtc = DateTimeOffset.UtcNow,
                });
        }

        if (string.Equals(
                request.Method,
                CardsContract.SubscribeMethod,
                StringComparison.Ordinal))
        {
            return _cardSubscriptionCommandHandler.Handle(
                request,
                connectionId,
                out subscription);
        }

        if (string.Equals(
                request.Method,
                PanelVisibilityContract.Method,
                StringComparison.Ordinal))
        {
            return _panelVisibilityCommandHandler.Handle(request);
        }

        if (LayoutContract.Methods.Contains(request.Method, StringComparer.Ordinal))
        {
            return _layoutCommandHandler?.Handle(request) ??
                ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (NotesContract.Methods.Contains(request.Method, StringComparer.Ordinal))
        {
            return _noteCommandHandler?.Handle(request) ??
                ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        if (WeatherSettingsContract.Methods.Contains(request.Method, StringComparer.Ordinal))
        {
            return _weatherSettingsCommandHandler?.Handle(request) ??
                ErrorResponse(request, "resource.unavailable", "resource-unavailable");
        }

        return ErrorResponse(request, "resource.unavailable", "resource-unavailable");
    }

    public void RemoveConnection(Guid connectionId) =>
        _cardSubscriptionCommandHandler.RemoveConnection(connectionId);
}
