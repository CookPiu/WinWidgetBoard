using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.CoreBroker.Commands;
using WinWidgetBoard.CoreBroker.Ipc;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardsSubscriptionTests
{
    [TestMethod(DisplayName = "UT-CARDS-001 [CRD-001] cards.subscribe returns latest and streams events")]
    public async Task SubscribeReturnsInitialSnapshotAndStreamsEvent()
    {
        string pipeName = CoreBrokerPipeNames.CreateTestName();
        string token = "cards-subscribe-token";
        var hub = new CardSnapshotSubscriptionHub();
        await hub.PublishAsync(CreateSnapshot("demo.weather", 1), CancellationToken.None);
        var router = new CoreBrokerCommandRouter(cardSnapshotSubscriptionHub: hub);
        using var cancellation = new CancellationTokenSource();
        var server = new CoreBrokerPipeServer(pipeName, token, "0.1.0", router);
        Task serverTask = server.RunAsync(cancellation.Token);

        try
        {
            await using var pipeClient = new CoreBrokerPipeClient(pipeName);
            using var cardsClient = new CoreBrokerCardsClient(pipeClient);
            var received = new TaskCompletionSource<CardStateSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cardsClient.SnapshotReceived += (_, args) =>
            {
                if (args.Snapshot.Sequence == 2)
                {
                    received.TrySetResult(args.Snapshot);
                }
            };

            Envelope hello = await pipeClient.HandshakeAsync(
                SessionHelloContract.WorkspacePanelClientType,
                token,
                CancellationToken.None);

            Assert.IsNull(hello.Error);
            Assert.IsTrue(
                hello.Payload
                    .GetProperty("capabilities")
                    .EnumerateArray()
                    .Any(value => value.GetString() == CardsContract.SubscribeMethod));

            CardsSubscribeResponse response = await cardsClient.SubscribeAsync(
                new CardsSubscribeRequest
                {
                    InstanceIds = ["demo.weather"],
                    Visibility = new CardSubscriptionVisibility
                    {
                        PanelVisible = true,
                        VisibleInstanceIds = ["demo.weather"],
                    },
                },
                CancellationToken.None);

            Assert.AreNotEqual(Guid.Empty, response.SubscriptionId);
            Assert.AreEqual(1, response.InitialSnapshots.Count);
            Assert.AreEqual(1, response.InitialSnapshots[0].Sequence);

            await hub.PublishAsync(CreateSnapshot("demo.weather", 2), CancellationToken.None);
            Task<Envelope> pingTask = pipeClient.SendAsync(
                CreatePingRequest(),
                CancellationToken.None);
            CardStateSnapshot streamed = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual("demo.weather", streamed.InstanceId);
            Assert.AreEqual(2, streamed.Sequence);

            Envelope ping = await pingTask;
            Assert.IsNull(ping.Error);
        }
        finally
        {
            cancellation.Cancel();
            await serverTask;
            hub.Dispose();
        }
    }

    [TestMethod(DisplayName = "UT-CARDS-002 [NFR-PERF-007] subscription coalesces by instance and disconnects on overflow")]
    public async Task SubscriptionCoalescesAndReportsOverflow()
    {
        var hub = new CardSnapshotSubscriptionHub(subscriptionBufferCapacity: 2);
        Guid connectionId = Guid.NewGuid();
        Assert.IsTrue(hub.TrySubscribe(
            connectionId,
            new CardsSubscribeRequest
            {
                InstanceIds = ["card.one", "card.two", "card.three", "card.four"],
                Visibility = new CardSubscriptionVisibility { PanelVisible = true },
            },
            out _,
            out CardSnapshotSubscription? subscription));
        Assert.IsNotNull(subscription);

        try
        {
            await hub.PublishAsync(CreateSnapshot("card.one", 1), CancellationToken.None);
            await hub.PublishAsync(CreateSnapshot("card.one", 2), CancellationToken.None);
            await hub.PublishAsync(CreateSnapshot("card.two", 1), CancellationToken.None);
            await hub.PublishAsync(CreateSnapshot("card.three", 1), CancellationToken.None);
            await hub.PublishAsync(CreateSnapshot("card.four", 1), CancellationToken.None);

            CardSnapshotSubscriptionOverflowException exception =
                await Assert.ThrowsAsync<CardSnapshotSubscriptionOverflowException>(
                    async () => await subscription!.WaitAndDrainAsync(CancellationToken.None));
            Assert.AreEqual(subscription.SubscriptionId, exception.SubscriptionId);
        }
        finally
        {
            hub.Remove(connectionId);
            hub.Dispose();
        }
    }

    [TestMethod(DisplayName = "UT-CARDS-003 [CRD-002] WorkspacePanel dispatcher maps and sequence-gates snapshots")]
    public async Task WorkspacePanelDispatcherMapsSnapshotAndIgnoresOlderSequence()
    {
        using CardRuntimeInstance runtime = CreateRuntime("demo.dispatcher");
        int dispatchCalls = 0;
        var dispatcher = new CardSnapshotDispatcher(
            action =>
            {
                dispatchCalls++;
                return ValueTask.FromResult(action());
            });
        Assert.IsTrue(dispatcher.Register(runtime));

        CardSnapshotDispatchResult applied = await dispatcher.DispatchAsync(
            CreateSnapshot("demo.dispatcher", 1),
            CancellationToken.None);
        CardSnapshotDispatchResult ignored = await dispatcher.DispatchAsync(
            CreateSnapshot("demo.dispatcher", 1),
            CancellationToken.None);

        Assert.AreEqual(CardSnapshotDispatchResult.Applied, applied);
        Assert.AreEqual(CardSnapshotDispatchResult.IgnoredOlder, ignored);
        Assert.AreEqual(2, dispatchCalls);
        Assert.AreEqual(1, runtime.Snapshot.Sequence);
        Assert.AreEqual(CardRuntimeStatus.Ready, runtime.Snapshot.Status);
        Assert.IsFalse(dispatcher.Register(runtime));
        Assert.IsTrue(dispatcher.Unregister(runtime.InstanceId, runtime));
    }

    private static CardStateSnapshot CreateSnapshot(string instanceId, long sequence) =>
        new()
        {
            InstanceId = instanceId,
            CardTypeId = "test.card",
            SchemaVersion = 1,
            Sequence = sequence,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Status = CardSnapshotStatus.Ready,
            Freshness = CardSnapshotFreshness.Fresh,
            Payload = JsonSerializer.SerializeToElement(
                new { sequence },
                ContractJson.Options),
            AllowedActions = [CardsContract.RefreshActionId],
        };

    private static CardRuntimeInstance CreateRuntime(string instanceId)
    {
        var definition = new CardDefinition(
            "test.card",
            "TestCardTitle.Text",
            CardSize.M,
            [CardSize.M]);
        var snapshot = new CardRuntimeSnapshot(
            instanceId,
            definition.CardTypeId,
            1,
            0,
            DateTimeOffset.UtcNow,
            CardRuntimeFreshness.Unknown,
            CardRuntimeStatus.Unknown);
        var runtime = new CardRuntimeInstance(definition, instanceId, snapshot);
        runtime.Initialize();
        return runtime;
    }

    private static Envelope CreatePingRequest() =>
        new()
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageType = EnvelopeMessageType.Request,
            MessageId = Guid.NewGuid(),
            CorrelationId = null,
            SentAtUtc = DateTimeOffset.UtcNow,
            Method = "session.ping",
            Payload = JsonSerializer.SerializeToElement(new { }, ContractJson.Options),
            Error = null,
        };
}
