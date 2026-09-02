using System.Text;
using WinWidgetBoard.CoreBroker.Persistence;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// The daily price sync (ADR-0035). What is pinned: that the feed is reduced to first-party
/// ids only, that a missing cache field falls back to the vendors' published multiplier
/// rather than to zero, that the synced rate wins over the built-in one, and that the
/// cadence retries an hour after a failure rather than waiting a day.
/// </summary>
[TestClass]
public sealed class TokenUsagePricingFeedTests
{
    private static readonly string[] ExpectedFirstPartyIds =
        ["claude-fable-5-1", "gpt-5.6-sol", "gpt-5.4"];

    private const string Feed = """
        {
          "sample_spec": { "input_cost_per_token": 0.0, "litellm_provider": "one of ..." },
          "claude-fable-5-1": {
            "litellm_provider": "anthropic",
            "input_cost_per_token": 1e-05,
            "output_cost_per_token": 5e-05,
            "cache_creation_input_token_cost": 1.25e-05,
            "cache_creation_input_token_cost_above_1hr": 2e-05,
            "cache_read_input_token_cost": 2.5e-07,
            "max_tokens": 128000,
            "supported_modalities": ["text", "image"]
          },
          "gpt-5.6-sol": {
            "litellm_provider": "openai",
            "input_cost_per_token": 4e-06,
            "output_cost_per_token": 2e-05,
            "cache_creation_input_token_cost": 5e-06,
            "cache_read_input_token_cost": 4e-07
          },
          "gpt-5.4": {
            "litellm_provider": "openai",
            "input_cost_per_token": 2.5e-06,
            "output_cost_per_token": 1.5e-05
          },
          "bedrock/anthropic.claude-fable-5-1": {
            "litellm_provider": "bedrock",
            "input_cost_per_token": 1.2e-05,
            "output_cost_per_token": 6e-05
          },
          "openrouter/anthropic/claude-fable-5-1": {
            "litellm_provider": "openrouter",
            "input_cost_per_token": 1e-05,
            "output_cost_per_token": 5e-05
          },
          "claude-broken": {
            "litellm_provider": "anthropic",
            "output_cost_per_token": 5e-05
          },
          "Claude-Weird_Id": {
            "litellm_provider": "anthropic",
            "input_cost_per_token": 1e-05,
            "output_cost_per_token": 5e-05
          }
        }
        """;

    [TestMethod(DisplayName =
        "UT-TOKUSE-110 [USE-017] The feed is reduced to first-party ids at per-million rates")]
    public void FeedIsReducedToFirstPartyIds()
    {
        IReadOnlyDictionary<string, TokenUsageRate> rates =
            TokenUsagePricingFeed.Parse(Encoding.UTF8.GetBytes(Feed));

        CollectionAssert.AreEquivalent(ExpectedFirstPartyIds, rates.Keys.ToArray());

        TokenUsageRate fable = rates["claude-fable-5-1"];
        Assert.AreEqual(10m, fable.InputPerMillion);
        Assert.AreEqual(50m, fable.OutputPerMillion);
        Assert.AreEqual(12.5m, fable.CacheWrite5mPerMillion);
        Assert.AreEqual(20m, fable.CacheWrite1hPerMillion);
        Assert.AreEqual(0.25m, fable.CacheReadPerMillion);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-111 [USE-017] A missing cache field falls back to the vendor multiplier")]
    public void MissingCacheFieldsFallBackToVendorMultipliers()
    {
        IReadOnlyDictionary<string, TokenUsageRate> rates =
            TokenUsagePricingFeed.Parse(Encoding.UTF8.GetBytes(Feed));

        // OpenAI publishes no 1-hour tier: its only write rate stands in for both.
        TokenUsageRate sol = rates["gpt-5.6-sol"];
        Assert.AreEqual(5m, sol.CacheWrite5mPerMillion);
        Assert.AreEqual(5m, sol.CacheWrite1hPerMillion);
        Assert.AreEqual(0.4m, sol.CacheReadPerMillion);

        // No cache fields at all: 1.25x input for a write, 0.1x for a read.
        TokenUsageRate plain = rates["gpt-5.4"];
        Assert.AreEqual(3.125m, plain.CacheWrite5mPerMillion);
        Assert.AreEqual(3.125m, plain.CacheWrite1hPerMillion);
        Assert.AreEqual(0.25m, plain.CacheReadPerMillion);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-112 [USE-017] The synced rate wins over the built-in one; the table stays as the floor")]
    public void SyncedRateWinsAndBuiltInRemainsTheFloor()
    {
        var book = new TokenUsageRateBook();
        Assert.IsNotNull(book.TryGetRate("claude-opus-5"), "built-in table");
        Assert.IsNull(book.TryGetRate("claude-not-yet-released"));

        book.ApplySynced(
            new Dictionary<string, TokenUsageRate>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-opus-5"] = new(1m, 2m, 3m, 4m, 5m),
                ["claude-not-yet-released"] = new(7m, 8m, 9m, 10m, 11m),
            },
            new DateTimeOffset(2026, 9, 2, 1, 0, 0, TimeSpan.Zero));

        Assert.AreEqual(1m, book.TryGetRate("claude-opus-5")!.Value.InputPerMillion);
        Assert.AreEqual(7m, book.TryGetRate("claude-not-yet-released")!.Value.InputPerMillion);
        // A model only the built-in table knows still prices.
        Assert.IsNotNull(book.TryGetRate("claude-haiku-4-5"));
        Assert.AreEqual(2, book.SyncedCount);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-113 [USE-017] Cadence: now when never fetched, a day after success, an hour after failure")]
    public void CadenceIsDailyWithHourlyRetry()
    {
        var now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        Assert.AreEqual(now, TokenUsagePricingSyncer.NextDueUtc(null, null, false, now));
        Assert.AreEqual(
            now.AddHours(-2).AddDays(1),
            TokenUsagePricingSyncer.NextDueUtc(now.AddHours(-2), now.AddHours(-2), false, now));
        Assert.AreEqual(
            now.AddMinutes(-10).AddHours(1),
            TokenUsagePricingSyncer.NextDueUtc(now.AddDays(-3), now.AddMinutes(-10), true, now));
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-114 [USE-017] A stored feed is re-serialized exactly")]
    public void StoredRatesRoundTrip()
    {
        var rates = new Dictionary<string, TokenUsageRate>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-fable-5-1"] = new(10m, 50m, 12.5m, 20m, 0.25m),
            ["gpt-5.6-sol"] = new(4m, 20m, 5m, 5m, 0.4m),
        };

        string json = TokenUsagePricingCacheRepository.SerializeRates(rates);
        IReadOnlyDictionary<string, TokenUsageRate> restored =
            TokenUsagePricingCacheRepository.DeserializeRates(json);

        Assert.AreEqual(2, restored.Count);
        Assert.AreEqual(rates["claude-fable-5-1"], restored["claude-fable-5-1"]);
        Assert.AreEqual(rates["gpt-5.6-sol"], restored["gpt-5.6-sol"]);
        Assert.AreEqual(0, TokenUsagePricingCacheRepository.DeserializeRates("not json").Count);
    }

    [TestMethod(DisplayName =
        "UT-TOKUSE-115 [USE-016] The built-in table prices Fable 5.1 at its cheaper cache read")]
    public void BuiltInTableKnowsFable51()
    {
        TokenUsageRate? rate = TokenUsagePricing.TryGetRate("claude-fable-5-1");

        Assert.IsNotNull(rate);
        Assert.AreEqual(10m, rate.Value.InputPerMillion);
        Assert.AreEqual(0.25m, rate.Value.CacheReadPerMillion);
    }
}
