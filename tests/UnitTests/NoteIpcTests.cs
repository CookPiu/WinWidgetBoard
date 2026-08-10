using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.CoreBroker.Commands;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class NoteIpcTests
{
    [TestMethod(DisplayName = "IT-NOTE-001 [NTE-001] Notes IPC supports save, search, revision conflict and delete")]
    public async Task NotesIpcSupportsSaveSearchConflictAndDelete()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "notes-ipc-token";
        var router = new CoreBrokerCommandRouter(new NoteRepository(database));
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(pipeName, token, "0.1.0", router);
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(
                pipeName,
                connectTimeout: TimeSpan.FromSeconds(1),
                requestTimeout: TimeSpan.FromSeconds(1));
            Envelope hello = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);
            SessionHelloResponse helloPayload = hello.Payload.Deserialize<SessionHelloResponse>(
                ContractJson.Options)!;
            CollectionAssert.Contains(helloPayload.Capabilities.ToArray(), NotesContract.SaveMethod);

            Guid createOperationId = Guid.NewGuid();
            Envelope saved = await client.SendAsync(
                CreateRequest(
                    NotesContract.SaveMethod,
                    new NoteSaveRequest
                    {
                        ClientOperationId = createOperationId,
                        NoteId = "note-1",
                        Title = "IPC note",
                        Body = "first body",
                        BodyFormat = NotesContract.MarkdownFormat,
                    }),
                CancellationToken.None);
            Assert.IsNull(saved.Error);
            NoteSaveResponse savedPayload = saved.Payload.Deserialize<NoteSaveResponse>(
                ContractJson.Options)!;

            Envelope duplicate = await client.SendAsync(
                CreateRequest(
                    NotesContract.SaveMethod,
                    new NoteSaveRequest
                    {
                        ClientOperationId = createOperationId,
                        NoteId = "note-1",
                        Title = "IPC note",
                        Body = "first body",
                        BodyFormat = NotesContract.MarkdownFormat,
                    }),
                CancellationToken.None);
            Assert.IsNull(duplicate.Error);
            NoteSaveResponse duplicatePayload = duplicate.Payload.Deserialize<NoteSaveResponse>(
                ContractJson.Options)!;
            Assert.AreEqual(
                savedPayload.Note.UpdatedAtUtc,
                duplicatePayload.Note.UpdatedAtUtc);

            Envelope updated = await client.SendAsync(
                CreateRequest(
                    NotesContract.SaveMethod,
                    new NoteSaveRequest
                    {
                        ClientOperationId = Guid.NewGuid(),
                        NoteId = "note-1",
                        Title = "IPC note updated",
                        Body = "latest body",
                        BodyFormat = NotesContract.PlainTextFormat,
                        ExpectedUpdatedAtUtc = savedPayload.Note.UpdatedAtUtc,
                    }),
                CancellationToken.None);
            Assert.IsNull(updated.Error);
            NoteSaveResponse updatedPayload = updated.Payload.Deserialize<NoteSaveResponse>(
                ContractJson.Options)!;

            Envelope conflict = await client.SendAsync(
                CreateRequest(
                    NotesContract.SaveMethod,
                    new NoteSaveRequest
                    {
                        ClientOperationId = Guid.NewGuid(),
                        NoteId = "note-1",
                        Title = "stale",
                        Body = "must not overwrite",
                        BodyFormat = NotesContract.PlainTextFormat,
                        ExpectedUpdatedAtUtc = savedPayload.Note.UpdatedAtUtc,
                    }),
                CancellationToken.None);
            Assert.IsNotNull(conflict.Error);
            Assert.AreEqual("conflict.notes-revision", conflict.Error!.Code);

            Envelope search = await client.SendAsync(
                CreateRequest(
                    NotesContract.SearchMethod,
                    new NoteSearchRequest { Query = "latest" }),
                CancellationToken.None);
            Assert.IsNull(search.Error);
            NoteSearchResponse searchPayload = search.Payload.Deserialize<NoteSearchResponse>(
                ContractJson.Options)!;
            Assert.AreEqual(1, searchPayload.Notes.Count);
            Assert.AreEqual("latest body", searchPayload.Notes[0].Body);

            Guid deleteOperationId = Guid.NewGuid();
            var deleteRequest = new NoteDeleteRequest
            {
                ClientOperationId = deleteOperationId,
                NoteId = "note-1",
                ExpectedUpdatedAtUtc = updatedPayload.Note.UpdatedAtUtc,
            };
            Envelope deleted = await client.SendAsync(
                CreateRequest(NotesContract.DeleteMethod, deleteRequest),
                CancellationToken.None);
            Assert.IsNull(deleted.Error);
            Assert.IsTrue(deleted.Payload.Deserialize<NoteDeleteResponse>(ContractJson.Options)!.Deleted);

            Envelope duplicateDelete = await client.SendAsync(
                CreateRequest(NotesContract.DeleteMethod, deleteRequest),
                CancellationToken.None);
            Assert.IsNull(duplicateDelete.Error);
            Assert.IsTrue(duplicateDelete.Payload.Deserialize<NoteDeleteResponse>(ContractJson.Options)!.Deleted);

            Envelope missing = await client.SendAsync(
                CreateRequest(
                    NotesContract.GetMethod,
                    new NoteGetRequest { NoteId = "note-1" }),
                CancellationToken.None);
            Assert.IsNotNull(missing.Error);
            Assert.AreEqual("resource.not-found", missing.Error!.Code);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-NOTE-002 [NTE-001] Notes IPC rejects invalid format and oversized input")]
    public async Task NotesIpcRejectsInvalidFormatAndOversizedInput()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "notes-validation-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            new CoreBrokerCommandRouter(new NoteRepository(database)));
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);

            Envelope invalidFormat = await client.SendAsync(
                CreateRequest(
                    NotesContract.SaveMethod,
                    new NoteSaveRequest
                    {
                        ClientOperationId = Guid.NewGuid(),
                        NoteId = "note-1",
                        Title = "title",
                        Body = "body",
                        BodyFormat = "html",
                    }),
                CancellationToken.None);
            Assert.IsNotNull(invalidFormat.Error);
            Assert.AreEqual("validation.invalid-argument", invalidFormat.Error!.Code);

            Envelope oversized = await client.SendAsync(
                CreateRequest(
                    NotesContract.SaveMethod,
                    new NoteSaveRequest
                    {
                        ClientOperationId = Guid.NewGuid(),
                        NoteId = "note-2",
                        Title = "title",
                        Body = new string('x', NotesContract.MaxBodyLength + 1),
                        BodyFormat = NotesContract.PlainTextFormat,
                    }),
                CancellationToken.None);
            Assert.IsNotNull(oversized.Error);
            Assert.AreEqual("validation.invalid-argument", oversized.Error!.Code);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    private static Envelope CreateRequest(string method, object payload) =>
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

    private static async Task<SqliteDatabase> OpenDatabaseAsync()
    {
        SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        await database.ApplySchemaAsync();
        return database;
    }
}
