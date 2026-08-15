using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Projects connection-scoped cards.subscribe visibility onto scheduled
/// provider registrations. A provider runs only while at least one connected
/// panel has the matching instance in its visible viewport.
/// </summary>
public sealed class ProviderRefreshVisibilityRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly ProviderRefreshScheduler _scheduler;
    private readonly Dictionary<string, Guid> _providerSubscriptions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CardsSubscribeRequest> _connections = [];
    private bool _disposed;

    public ProviderRefreshVisibilityRegistry(
        ProviderRefreshScheduler scheduler)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public void Register(
        string instanceId,
        Guid subscriptionId)
    {
        if (!CardsContract.IsValidIdentifier(
                instanceId,
                CardsContract.MaxInstanceIdLength))
        {
            throw new ArgumentException(
                "Provider instance ID is invalid.",
                nameof(instanceId));
        }

        if (subscriptionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Provider subscription ID must not be empty.",
                nameof(subscriptionId));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_providerSubscriptions.TryAdd(instanceId, subscriptionId))
            {
                throw new ArgumentException(
                    "A provider subscription is already registered for this instance.",
                    nameof(instanceId));
            }
        }

        _scheduler.SetVisibility(
            subscriptionId,
            new ProviderRefreshVisibility(
                PanelVisible: false,
                InViewport: false,
                DisplayConnected: false));
    }

    public bool Apply(
        Guid connectionId,
        CardsSubscribeRequest? request)
    {
        if (connectionId == Guid.Empty ||
            !TryNormalizeRequest(request, out CardsSubscribeRequest? normalized) ||
            normalized is null)
        {
            return false;
        }

        CardsSubscribeRequest[] connections;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _connections[connectionId] = normalized;
            connections = _connections.Values.ToArray();
        }

        ApplyVisibility(connections);
        return true;
    }

    public bool Remove(Guid connectionId)
    {
        CardsSubscribeRequest[] connections;
        lock (_gate)
        {
            if (_disposed || !_connections.Remove(connectionId))
            {
                return false;
            }

            connections = _connections.Values.ToArray();
        }

        ApplyVisibility(connections);
        return true;
    }

    public void Dispose()
    {
        Guid[] subscriptionIds;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptionIds = _providerSubscriptions.Values.ToArray();
            _providerSubscriptions.Clear();
            _connections.Clear();
        }

        foreach (Guid subscriptionId in subscriptionIds)
        {
            _scheduler.SetVisibility(
                subscriptionId,
                new ProviderRefreshVisibility(
                    PanelVisible: false,
                    InViewport: false,
                    DisplayConnected: false));
        }
    }

    private void ApplyVisibility(
        CardsSubscribeRequest[] connections)
    {
        KeyValuePair<string, Guid>[] providers;
        lock (_gate)
        {
            providers = _providerSubscriptions.ToArray();
        }

        foreach ((string instanceId, Guid subscriptionId) in providers)
        {
            bool subscribed = connections.Any(connection =>
                connection.InstanceIds.Contains(
                    instanceId,
                    StringComparer.Ordinal));
            bool panelVisible = subscribed && connections.Any(connection =>
                IsVisible(connection, instanceId));
            bool displayConnected = subscribed && connections.Length > 0;
            _scheduler.SetVisibility(
                subscriptionId,
                new ProviderRefreshVisibility(
                    panelVisible,
                    InViewport: panelVisible,
                    displayConnected));
        }
    }

    private static bool IsVisible(
        CardsSubscribeRequest request,
        string instanceId)
    {
        if (!request.Visibility.PanelVisible ||
            !request.InstanceIds.Contains(instanceId, StringComparer.Ordinal))
        {
            return false;
        }

        return request.Visibility.VisibleInstanceIds.Count == 0 ||
            request.Visibility.VisibleInstanceIds.Contains(
                instanceId,
                StringComparer.Ordinal);
    }

    private static bool TryNormalizeRequest(
        CardsSubscribeRequest? request,
        out CardsSubscribeRequest? normalized)
    {
        normalized = null;
        if (request is null || request.Visibility is null ||
            !TryNormalizeIdentifiers(
                request.InstanceIds,
                out string[] instanceIds) ||
            !TryNormalizeIdentifiers(
                request.Visibility.VisibleInstanceIds,
                out string[] visibleInstanceIds))
        {
            return false;
        }

        HashSet<string> instanceSet = instanceIds.ToHashSet(
            StringComparer.Ordinal);
        if (visibleInstanceIds.Any(instanceId => !instanceSet.Contains(instanceId)))
        {
            return false;
        }

        normalized = new CardsSubscribeRequest
        {
            InstanceIds = Array.AsReadOnly(instanceIds),
            Visibility = new CardSubscriptionVisibility
            {
                PanelVisible = request.Visibility.PanelVisible,
                VisibleInstanceIds = Array.AsReadOnly(visibleInstanceIds),
            },
        };
        return true;
    }

    private static bool TryNormalizeIdentifiers(
        IReadOnlyList<string>? values,
        out string[] normalized)
    {
        normalized = [];
        if (values is null || values.Count > CardsContract.MaxInstanceIds)
        {
            return false;
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? value in values)
        {
            if (!CardsContract.IsValidIdentifier(
                    value,
                    CardsContract.MaxInstanceIdLength) ||
                !unique.Add(value!))
            {
                return false;
            }
        }

        normalized = values.ToArray();
        return true;
    }
}
