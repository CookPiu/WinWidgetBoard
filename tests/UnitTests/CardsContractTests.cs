using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardsContractTests
{
    [TestMethod(DisplayName =
        "CT-CARD-001 [CRD-001/CRD-002/CRD-003] " +
        "cards.subscribe and card snapshot preserve the versioned shape")]
    public void SubscribeAndSnapshotRoundTrip()
    {
        var source = new CardsSubscribeRequest
        {
            InstanceIds = ["demo.weather"],
            Visibility = new CardSubscriptionVisibility
            {
                PanelVisible = true,
                VisibleInstanceIds = ["demo.weather"],
            },
        };
        var snapshot = new CardStateSnapshot
        {
            InstanceId = "demo.weather",
            CardTypeId = "builtin.weather",
            SchemaVersion = 1,
            Sequence = 4,
            GeneratedAtUtc = new DateTimeOffset(
                2026,
                8,
                14,
                10,
                0,
                0,
                TimeSpan.Zero),
            ValidUntilUtc = new DateTimeOffset(
                2026,
                8,
                14,
                10,
                15,
                0,
                TimeSpan.Zero),
            Status = CardSnapshotStatus.Ready,
            Freshness = CardSnapshotFreshness.Fresh,
            Payload = JsonSerializer.SerializeToElement(
                new { temperature = 31.2 }),
            AllowedActions = [CardsContract.RefreshActionId],
        };

        string requestJson = JsonSerializer.Serialize(
            source,
            ContractJson.Options);
        string snapshotJson = JsonSerializer.Serialize(
            snapshot,
            ContractJson.Options);
        CardsSubscribeRequest parsedRequest = JsonSerializer.Deserialize<CardsSubscribeRequest>(
            requestJson,
            ContractJson.Options)!;
        CardStateSnapshot parsedSnapshot = JsonSerializer.Deserialize<CardStateSnapshot>(
            snapshotJson,
            ContractJson.Options)!;

        Assert.AreEqual(CardsContract.SubscribeMethod, CardsContract.Methods[0]);
        Assert.AreEqual("demo.weather", parsedRequest.InstanceIds[0]);
        Assert.IsTrue(parsedRequest.Visibility.PanelVisible);
        Assert.AreEqual(snapshot.InstanceId, parsedSnapshot.InstanceId);
        Assert.AreEqual(snapshot.Sequence, parsedSnapshot.Sequence);
        Assert.AreEqual(CardSnapshotStatus.Ready, parsedSnapshot.Status);
        Assert.AreEqual(CardSnapshotFreshness.Fresh, parsedSnapshot.Freshness);
        Assert.AreEqual(
            31.2,
            parsedSnapshot.Payload.GetProperty("temperature").GetDouble(),
            0.0001);
    }

    [TestMethod(DisplayName =
        "CT-CARD-002 [CRD-001/CRD-005] " +
        "card contract identifiers enforce the shared bounds")]
    public void CardContractIdentifiersEnforceBounds()
    {
        Assert.IsTrue(CardsContract.IsValidIdentifier("demo.card", 32));
        Assert.IsFalse(CardsContract.IsValidIdentifier("demo card", 32));
        Assert.IsFalse(CardsContract.IsValidIdentifier("demo.card\n", 32));
        Assert.IsFalse(CardsContract.IsValidIdentifier("", 32));
        Assert.IsFalse(CardsContract.IsValidIdentifier("12345", 4));
    }
}
