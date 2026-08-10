using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Client;

public interface INoteClient
{
    Task<NoteDto> SaveNoteAsync(
        NoteSaveRequest request,
        CancellationToken cancellationToken);

    Task<NoteDto?> GetNoteAsync(
        string noteId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NoteDto>> SearchNotesAsync(
        string query,
        CancellationToken cancellationToken);

    Task<NoteDeleteResponse> DeleteNoteAsync(
        NoteDeleteRequest request,
        CancellationToken cancellationToken);
}

public sealed class CoreBrokerNotesClient : INoteClient
{
    private readonly CoreBrokerPipeClient _client;

    public CoreBrokerNotesClient(CoreBrokerPipeClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<NoteDto> SaveNoteAsync(
        NoteSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(NotesContract.SaveMethod, request),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, NotesContract.SaveMethod);

        NoteSaveResponse payload = Deserialize<NoteSaveResponse>(
            response,
            NotesContract.SaveMethod);
        return payload.Note;
    }

    public async Task<NoteDto?> GetNoteAsync(
        string noteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(
                NotesContract.GetMethod,
                new NoteGetRequest { NoteId = noteId }),
            cancellationToken).ConfigureAwait(false);
        if (response.Error?.Code == "resource.not-found")
        {
            return null;
        }

        EnsureSuccess(response, NotesContract.GetMethod);
        return Deserialize<NoteGetResponse>(response, NotesContract.GetMethod).Note;
    }

    public async Task<IReadOnlyList<NoteDto>> SearchNotesAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(
                NotesContract.SearchMethod,
                new NoteSearchRequest { Query = query }),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, NotesContract.SearchMethod);
        return Deserialize<NoteSearchResponse>(response, NotesContract.SearchMethod).Notes;
    }

    public async Task<NoteDeleteResponse> DeleteNoteAsync(
        NoteDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Envelope response = await _client.SendWithReconnectAsync(
            CreateRequest(NotesContract.DeleteMethod, request),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, NotesContract.DeleteMethod);
        return Deserialize<NoteDeleteResponse>(response, NotesContract.DeleteMethod);
    }

    private static Envelope CreateRequest<TPayload>(
        string method,
        TPayload payload) =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Request,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = method,
            Payload = JsonSerializer.SerializeToElement(payload, ContractJson.Options),
            Error = null,
        };

    private static TPayload Deserialize<TPayload>(Envelope response, string method)
    {
        try
        {
            return response.Payload.Deserialize<TPayload>(ContractJson.Options)
                ?? throw new CoreBrokerClientException(
                    method,
                    "protocol.empty-payload",
                    "CoreBroker returned an empty response payload.");
        }
        catch (JsonException exception)
        {
            throw new CoreBrokerClientException(
                method,
                "protocol.invalid-payload",
                "CoreBroker returned an invalid response payload.",
                exception);
        }
    }

    private static void EnsureSuccess(Envelope response, string method)
    {
        if (response.Error is null)
        {
            return;
        }

        throw new CoreBrokerClientException(
            method,
            response.Error.Code,
            response.Error.DeveloperMessage ?? response.Error.MessageKey,
            isTransient: response.Error.IsTransient);
    }
}

public sealed class CoreBrokerClientException : InvalidOperationException
{
    public CoreBrokerClientException(
        string method,
        string code,
        string message,
        Exception? innerException = null,
        bool isTransient = false)
        : base($"CoreBroker {method} failed: {code} ({message})", innerException)
    {
        Method = method;
        Code = code;
        IsTransient = isTransient;
    }

    public string Method { get; }

    public string Code { get; }

    public bool IsTransient { get; }
}
