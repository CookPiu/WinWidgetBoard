using System.Text.Json;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// Broker snapshots and locally produced snapshots are two independent sequence counters.
/// Comparing one against the other made the panel discard real card data as "older": the
/// panel's own visibility scheduler bumps the local sequence, so by the time CoreBroker
/// pushed its first snapshot the local number had already overtaken it and the card sat on
/// its placeholder for as long as the panel had been running.
/// </summary>
[TestClass]
public sealed class CardRemoteSnapshotSequenceTests
{
    [TestMethod(DisplayName =
        "UT-CARD-097 [CRD-001] The first broker snapshot wins over a local placeholder")]
    public void FirstRemoteSnapshotWinsOverLocalSequence()
    {
        CardRuntimeInstance runtime = CreateRuntime(out ICardDefinition definition);

        // The panel's own scheduler publishes several local snapshots first.
        for (long sequence = 1; sequence <= 5; sequence++)
        {
            Assert.IsTrue(runtime.ApplySnapshot(
                CreateSnapshot(definition, sequence, CardRuntimeStatus.Unavailable)));
        }

        // CoreBroker's first push carries sequence 1 in its own counter.
        Assert.IsTrue(
            runtime.ApplyRemoteSnapshot(
                CreateSnapshot(definition, 1, CardRuntimeStatus.Ready)),
            "The first broker snapshot must win regardless of the local sequence.");
        Assert.AreEqual(CardRuntimeStatus.Ready, runtime.Snapshot.Status);
    }

    [TestMethod(DisplayName =
        "UT-CARD-098 [CRD-001] Broker snapshots stay ordered among themselves")]
    public void RemoteSnapshotsRemainOrdered()
    {
        CardRuntimeInstance runtime = CreateRuntime(out ICardDefinition definition);

        Assert.IsTrue(runtime.ApplyRemoteSnapshot(
            CreateSnapshot(definition, 4, CardRuntimeStatus.Ready)));
        // Out-of-order delivery must still be dropped inside the broker's sequence space.
        Assert.IsFalse(runtime.ApplyRemoteSnapshot(
            CreateSnapshot(definition, 3, CardRuntimeStatus.Stale)));
        Assert.IsFalse(runtime.ApplyRemoteSnapshot(
            CreateSnapshot(definition, 4, CardRuntimeStatus.Stale)));
        Assert.AreEqual(CardRuntimeStatus.Ready, runtime.Snapshot.Status);

        Assert.IsTrue(runtime.ApplyRemoteSnapshot(
            CreateSnapshot(definition, 5, CardRuntimeStatus.Stale)));
        Assert.AreEqual(CardRuntimeStatus.Stale, runtime.Snapshot.Status);
    }

    [TestMethod(DisplayName =
        "UT-CARD-099 [CRD-001] A local snapshot cannot suppress a later broker snapshot")]
    public void LocalSnapshotsDoNotSuppressLaterRemoteOnes()
    {
        CardRuntimeInstance runtime = CreateRuntime(out ICardDefinition definition);

        Assert.IsTrue(runtime.ApplyRemoteSnapshot(
            CreateSnapshot(definition, 2, CardRuntimeStatus.Ready)));
        // A local publish races in and pushes the shared field far ahead.
        Assert.IsTrue(runtime.ApplySnapshot(
            CreateSnapshot(definition, 99, CardRuntimeStatus.Unavailable)));

        Assert.IsTrue(
            runtime.ApplyRemoteSnapshot(
                CreateSnapshot(definition, 3, CardRuntimeStatus.Ready)),
            "The next broker snapshot must still apply after a local publish.");
        Assert.AreEqual(CardRuntimeStatus.Ready, runtime.Snapshot.Status);
    }

    private static CardRuntimeInstance CreateRuntime(out ICardDefinition definition)
    {
        definition = BuiltInCardCatalog.Weather;
        return new CardRuntimeInstance(
            definition,
            BuiltInCardCatalog.WeatherInstanceId,
            CreateSnapshot(definition, 0, CardRuntimeStatus.Unavailable));
    }

    private static CardRuntimeSnapshot CreateSnapshot(
        ICardDefinition definition,
        long sequence,
        CardRuntimeStatus status) =>
        new(
            BuiltInCardCatalog.WeatherInstanceId,
            definition.CardTypeId,
            BuiltInCardRuntimeFactory.CurrentSchemaVersion,
            sequence,
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
            CardRuntimeFreshness.Fresh,
            status,
            JsonSerializer.SerializeToElement(new { probe = sequence }));
}
