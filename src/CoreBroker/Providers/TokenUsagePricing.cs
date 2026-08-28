using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// What one million tokens of each kind costs, in USD, at the vendor's published list price.
/// </summary>
public readonly record struct TokenUsageRate(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheWrite5mPerMillion,
    decimal CacheWrite1hPerMillion,
    decimal CacheReadPerMillion);

/// <summary>
/// Published list prices for the models this build can recognise.
///
/// Three things about this table are deliberate, and all three exist because a money figure is
/// believed far more readily than a token count.
///
/// It is a *list price* table, not a bill. Claude Code and Codex are normally used on a
/// subscription, where the per-token rate is not what the user actually pays; the figure this
/// produces is "what this usage would cost at public API rates", which is the same thing
/// ccusage and cc-switch report, and the card says so rather than implying an invoice.
///
/// A model that is not in the table contributes **nothing** and is counted instead. Pricing an
/// unknown model at zero would quietly under-report the total, which is the one failure mode
/// that cannot be noticed by looking at the number - so the card reports how many models it
/// could not price rather than hiding them in a plausible-looking total.
///
/// The rates are verified against the vendors' own pricing pages on the date below, and that
/// date travels with the table. Prices move: OpenAI cut the Sol rates on 2026-08-22, and
/// Anthropic made the Sonnet 5 introductory price permanent. A stale table is the expected
/// failure here, so the date is part of the data rather than a comment.
/// </summary>
public static class TokenUsagePricing
{
    /// <summary>
    /// When the rates below were last checked against the vendors' published pricing. Shown to
    /// nobody, but it is what a later reader needs in order to know whether to re-check.
    /// </summary>
    public const string VerifiedOn = "2026-08-28";

    /// <summary>
    /// Sources: platform.claude.com/docs/en/about-claude/pricing for the Claude rows (base
    /// input, 5m and 1h cache writes, cache hits, output); OpenAI's published GPT-5.6 rates
    /// after the 2026-08-22 cut for the Codex rows, where a cache write is 1.25x uncached
    /// input and a cache read is the published cached-input rate.
    /// </summary>
    private static readonly Dictionary<string, TokenUsageRate> Rates =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // --- Anthropic ------------------------------------------------------------
            ["claude-fable-5"] = new(10m, 50m, 12.50m, 20m, 1m),
            ["claude-mythos-5"] = new(10m, 50m, 12.50m, 20m, 1m),
            ["claude-opus-5"] = new(5m, 25m, 6.25m, 10m, 0.50m),
            ["claude-opus-4-8"] = new(5m, 25m, 6.25m, 10m, 0.50m),
            ["claude-opus-4-7"] = new(5m, 25m, 6.25m, 10m, 0.50m),
            ["claude-opus-4-6"] = new(5m, 25m, 6.25m, 10m, 0.50m),
            ["claude-opus-4-5"] = new(5m, 25m, 6.25m, 10m, 0.50m),
            ["claude-sonnet-5"] = new(2m, 10m, 2.50m, 4m, 0.20m),
            ["claude-sonnet-4-6"] = new(3m, 15m, 3.75m, 6m, 0.30m),
            ["claude-sonnet-4-5"] = new(3m, 15m, 3.75m, 6m, 0.30m),
            ["claude-haiku-4-5"] = new(1m, 5m, 1.25m, 2m, 0.10m),

            // --- OpenAI (Codex) -------------------------------------------------------
            ["gpt-5.6-sol"] = new(4m, 20m, 5m, 5m, 0.40m),
            ["gpt-5.6-terra"] = new(2m, 12m, 2.50m, 2.50m, 0.20m),
            ["gpt-5.6-luna"] = new(0.20m, 1.20m, 0.25m, 0.25m, 0.02m),
        };

    /// <summary>
    /// The rate for a model, or null when this build has no verified price for it.
    ///
    /// Deliberately an exact lookup with no prefix or family matching. A routed model id such
    /// as the ones a proxy rewrites - "anthropic/claude-opus-5-ps-aws-dst" - may or may not
    /// bill at the first-party rate, and guessing from the substring "claude-opus-5" would put
    /// a confident number on an assumption nobody checked.
    /// </summary>
    public static TokenUsageRate? TryGetRate(string? model) =>
        model is not null && Rates.TryGetValue(model, out TokenUsageRate rate)
            ? rate
            : null;

    /// <summary>What one response cost at list price, or null when the model has no rate.</summary>
    public static decimal? TryGetCost(TranscriptUsageRecord record)
    {
        if (TryGetRate(record.Model) is not { } rate)
        {
            return null;
        }

        return (record.InputTokens * rate.InputPerMillion
            + record.OutputTokens * rate.OutputPerMillion
            + record.CacheWrite5mTokens * rate.CacheWrite5mPerMillion
            + record.CacheWrite1hTokens * rate.CacheWrite1hPerMillion
            + record.CacheReadTokens * rate.CacheReadPerMillion) / 1_000_000m;
    }

    /// <summary>Model ids this build can price, for tests and diagnostics.</summary>
    public static IReadOnlyCollection<string> PricedModels => Rates.Keys;
}
