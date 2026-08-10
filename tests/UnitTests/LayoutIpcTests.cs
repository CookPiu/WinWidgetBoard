using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.CoreBroker.Commands;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.CoreBroker.Persistence;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class LayoutIpcTests
{
    [TestMethod(DisplayName = "IT-LAYOUT-001 [LYT-006] Layout IPC saves, reloads and deduplicates a layout command")]
    public async Task LayoutIpcSavesReloadsAndDeduplicates()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "layout-ipc-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            new CoreBrokerCommandRouter(
                layoutRepository: new LayoutRepository(database)));
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            Envelope hello = await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            Assert.IsNull(hello.Error);
            SessionHelloResponse helloPayload = hello.Payload.Deserialize<SessionHelloResponse>(
                ContractJson.Options)!;
            CollectionAssert.Contains(
                helloPayload.Capabilities.ToArray(),
                LayoutContract.SaveMethod);

            Guid operationId = Guid.NewGuid();
            var request = new LayoutSaveRequest
            {
                ClientOperationId = operationId,
                LayoutId = "primary-default",
                DisplayId = "primary",
                ExpectedRevision = 0,
                Items =
                [
                    new LayoutItemDto
                    {
                        InstanceId = "demo.notes",
                        Order = 0,
                        ColumnSpan = 2,
                        RowSpan = 2,
                        SizeId = "l",
                        PreferredColumn = 0,
                        PreferredRow = 3,
                    },
                    new LayoutItemDto
                    {
                        InstanceId = "demo.timer",
                        Order = 1,
                        ColumnSpan = 2,
                        RowSpan = 1,
                        SizeId = "m",
                        PreferredColumn = 2,
                        PreferredRow = 1,
                    },
                ],
            };

            Envelope saved = await client.SendAsync(
                CreateRequest(LayoutContract.SaveMethod, request),
                CancellationToken.None);
            Assert.IsNull(saved.Error);
            LayoutSaveResponse savedPayload = saved.Payload.Deserialize<LayoutSaveResponse>(
                ContractJson.Options)!;
            Assert.AreEqual(1, savedPayload.Layout.Revision);

            Envelope duplicate = await client.SendAsync(
                CreateRequest(LayoutContract.SaveMethod, request),
                CancellationToken.None);
            Assert.IsNull(duplicate.Error);
            LayoutSaveResponse duplicatePayload = duplicate.Payload.Deserialize<LayoutSaveResponse>(
                ContractJson.Options)!;
            Assert.AreEqual(savedPayload.Layout.Revision, duplicatePayload.Layout.Revision);

            Envelope loaded = await client.SendAsync(
                CreateRequest(
                    LayoutContract.GetMethod,
                    new LayoutGetRequest
                    {
                        LayoutId = "primary-default",
                        DisplayId = "primary",
                    }),
                CancellationToken.None);
            Assert.IsNull(loaded.Error);
            LayoutGetResponse loadedPayload = loaded.Payload.Deserialize<LayoutGetResponse>(
                ContractJson.Options)!;
            Assert.IsNotNull(loadedPayload.Layout);
            Assert.AreEqual(2, loadedPayload.Layout!.Items.Count);
            Assert.AreEqual("demo.timer", loadedPayload.Layout.Items[1].InstanceId);
            Assert.AreEqual(3, loadedPayload.Layout.Items[0].PreferredRow);
            Assert.AreEqual(1, loadedPayload.Layout.Items[1].PreferredRow);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
        }
    }

    [TestMethod(DisplayName = "IT-LAYOUT-002 [LYT-006] Layout IPC rejects a stale revision")]
    public async Task LayoutIpcRejectsStaleRevision()
    {
        await using SqliteDatabase database = await OpenDatabaseAsync();
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "layout-conflict-token";
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(
            pipeName,
            token,
            "0.1.0",
            new CoreBrokerCommandRouter(
                layoutRepository: new LayoutRepository(database)));
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var client = new CoreBrokerPipeClient(pipeName);
            await client.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);
            var request = new LayoutSaveRequest
            {
                ClientOperationId = Guid.NewGuid(),
                LayoutId = "primary-default",
                DisplayId = "primary",
                ExpectedRevision = 0,
                Items = Array.Empty<LayoutItemDto>(),
            };
            Envelope first = await client.SendAsync(
                CreateRequest(LayoutContract.SaveMethod, request),
                CancellationToken.None);
            Assert.IsNull(first.Error);

            Envelope conflict = await client.SendAsync(
                CreateRequest(
                    LayoutContract.SaveMethod,
                    request with { ClientOperationId = Guid.NewGuid() }),
                CancellationToken.None);
            Assert.IsNotNull(conflict.Error);
            Assert.AreEqual("conflict.layout-revision", conflict.Error!.Code);
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
