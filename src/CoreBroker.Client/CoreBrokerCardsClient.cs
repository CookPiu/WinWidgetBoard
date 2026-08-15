using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Client;

/// <summary>
/// Client-side cards.subscribe adapter. The underlying pipe client owns the
/// connection and demultiplexes responses from asynchronous event frames.
/// </summary>
public sealed class CoreBrokerCardsClient : IDisposable
{
    private readonly CoreBrokerPipeClient _pipeClient;
    private bool _disposed;

    public CoreBrokerCardsClient(CoreBrokerPipeClient pipeClient)
    {
        _pipeClient = pipeClient ?? throw new ArgumentNullException(nameof(pipeClient));
        _pipeClient.EventReceived += OnEventReceived;
    }

    public event EventHandler<CardSnapshotReceivedEventArgs>? SnapshotReceived;

    public async Task<CardsSubscribeResponse> SubscribeAsync(
        CardsSubscribeRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        Envelope response = await _pipeClient
            .SendWithReconnectAsync(
                new Envelope
                {
                    ProtocolVersion = ProtocolConstants.CurrentVersion,
                    MessageType = EnvelopeMessageType.Request,
                    MessageId = Guid.NewGuid(),
                    CorrelationId = null,
                    SentAtUtc = DateTimeOffset.UtcNow,
                    Method = CardsContract.SubscribeMethod,
                    Payload = JsonSerializer.SerializeToElement(
                        request,
                        ContractJson.Options),
                    Error = null,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Error is not null)
        {
            throw new InvalidOperationException(
                $"CoreBroker cards subscription failed: {response.Error.Code}");
        }

        try
        {
            CardsSubscribeResponse? payload = response.Payload.Deserialize<CardsSubscribeResponse>(
                ContractJson.Options);
            return payload ?? throw new InvalidOperationException(
                "The CoreBroker cards subscription response payload was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The CoreBroker cards subscription response payload was invalid.",
                exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pipeClient.EventReceived -= OnEventReceived;
    }

    private void OnEventReceived(
        object? sender,
        CoreBrokerEventReceivedEventArgs eventArgs)
    {
        Envelope message = eventArgs.Message;
        if (!string.Equals(
                message.Method,
                CardsContract.SnapshotEventMethod,
                StringComparison.Ordinal))
        {
            return;
        }

        CardSnapshotEvent? snapshotEvent;
        try
        {
            snapshotEvent = message.Payload.Deserialize<CardSnapshotEvent>(
                ContractJson.Options);
        }
        catch (JsonException)
        {
            return;
        }

        if (snapshotEvent?.Snapshot is null)
        {
            return;
        }

        SnapshotReceived?.Invoke(
            this,
            new CardSnapshotReceivedEventArgs(snapshotEvent.Snapshot));
    }
}

public sealed class CardSnapshotReceivedEventArgs : EventArgs
{
    public CardSnapshotReceivedEventArgs(CardStateSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public CardStateSnapshot Snapshot { get; }
}
