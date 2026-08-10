using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.CoreBroker.Commands;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CoreBrokerNotesClientTests
{
    [TestMethod(DisplayName = "IT-NOTE-003 [NTE-001] Client exposes typed note operations over IPC")]
    public async Task ClientExposesTypedNoteOperationsOverIpc()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "notes-client-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            new CoreBrokerCommandRouter(new NoteRepository(database)));
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var pipeClient = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await pipeClient.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);

            var notes = new CoreBrokerNotesClient(pipeClient);
            Assert.IsNull(await notes.GetNoteAsync("client-note", CancellationToken.None));

            NoteDto saved = await notes.SaveNoteAsync(
                new NoteSaveRequest
                {
                    ClientOperationId = Guid.NewGuid(),
                    NoteId = "client-note",
                    Title = "Client note",
                    Body = "saved through the typed client",
                    BodyFormat = NotesContract.PlainTextFormat,
                },
                CancellationToken.None);
            Assert.AreEqual("client-note", saved.NoteId);

            NoteDto? loaded = await notes.GetNoteAsync(
                "client-note",
                CancellationToken.None);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(saved.UpdatedAtUtc, loaded!.UpdatedAtUtc);

            IReadOnlyList<NoteDto> search = await notes.SearchNotesAsync(
                "typed client",
                CancellationToken.None);
            Assert.AreEqual(1, search.Count);
            Assert.AreEqual("client-note", search[0].NoteId);

            NoteDeleteResponse deleted = await notes.DeleteNoteAsync(
                new NoteDeleteRequest
                {
                    ClientOperationId = Guid.NewGuid(),
                    NoteId = "client-note",
                    ExpectedUpdatedAtUtc = saved.UpdatedAtUtc,
                },
                CancellationToken.None);
            Assert.IsTrue(deleted.Deleted);
            Assert.IsNull(await notes.GetNoteAsync("client-note", CancellationToken.None));
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-NOTE-004 [NTE-001] Client preserves revision conflict code")]
    public async Task ClientPreservesRevisionConflictCode()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "notes-client-conflict-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            new CoreBrokerCommandRouter(new NoteRepository(database)));
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var pipeClient = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await pipeClient.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);

            var notes = new CoreBrokerNotesClient(pipeClient);
            NoteDto saved = await notes.SaveNoteAsync(
                new NoteSaveRequest
                {
                    ClientOperationId = Guid.NewGuid(),
                    NoteId = "conflict-note",
                    Title = "Initial",
                    Body = "Initial",
                    BodyFormat = NotesContract.PlainTextFormat,
                },
                CancellationToken.None);
            _ = await notes.SaveNoteAsync(
                new NoteSaveRequest
                {
                    ClientOperationId = Guid.NewGuid(),
                    NoteId = "conflict-note",
                    Title = "Updated",
                    Body = "Updated",
                    BodyFormat = NotesContract.PlainTextFormat,
                    ExpectedUpdatedAtUtc = saved.UpdatedAtUtc,
                },
                CancellationToken.None);

            CoreBrokerClientException exception =
                await Assert.ThrowsAsync<CoreBrokerClientException>(() =>
                    notes.SaveNoteAsync(
                        new NoteSaveRequest
                        {
                            ClientOperationId = Guid.NewGuid(),
                            NoteId = "conflict-note",
                            Title = "Stale",
                            Body = "Stale",
                            BodyFormat = NotesContract.PlainTextFormat,
                            ExpectedUpdatedAtUtc = saved.UpdatedAtUtc,
                        },
                        CancellationToken.None));
            Assert.AreEqual("conflict.notes-revision", exception.Code);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    private static async Task<SqliteDatabase> OpenDatabaseAsync()
    {
        SqliteDatabase database = await SqliteDatabase.OpenAsync(
            new SqliteDatabaseOptions(":memory:"));
        await database.ApplySchemaAsync();
        return database;
    }
}
