using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Providers;

namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// The last feed the broker saw, reduced to the rates this card can use. This is the one
/// piece of token-usage state that is persisted besides the settings, and it contains no
/// usage: only public list prices, the feed's ETag, and when they were fetched (ADR-0035).
/// </summary>
public sealed record TokenUsagePricingCacheRecord(
    IReadOnlyDictionary<string, TokenUsageRate> Rates,
    string? ETag,
    DateTimeOffset FetchedAtUtc);

public sealed class TokenUsagePricingCacheRepository
{
    private readonly SqliteRepository _repository;

    public TokenUsagePricingCacheRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _repository = new SqliteRepository(database);
    }

    public TokenUsagePricingCacheRecord? Get()
    {
        TokenUsagePricingCacheRecord? result = null;
        _repository.Query(
            "SELECT rates_json, etag, fetched_at_utc FROM token_usage_pricing WHERE id = 1;",
            null,
            statement =>
            {
                IReadOnlyDictionary<string, TokenUsageRate> rates =
                    DeserializeRates(statement.ReadText(0));
                string? fetchedAt = statement.ReadText(2);
                if (rates.Count > 0 && fetchedAt is not null)
                {
                    result = new TokenUsagePricingCacheRecord(
                        rates,
                        statement.ReadText(1),
                        NoteRecord.ParseTimestamp(fetchedAt, "fetched_at_utc"));
                }
            });
        return result;
    }

    public void Save(TokenUsagePricingCacheRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string json = SerializeRates(record.Rates);
        string fetchedAt = NoteRecord.FormatTimestamp(record.FetchedAtUtc);
        _repository.Execute(
            "INSERT INTO token_usage_pricing (id, rates_json, etag, fetched_at_utc) " +
            "VALUES (1, @rates, @etag, @fetched) " +
            "ON CONFLICT(id) DO UPDATE SET rates_json = excluded.rates_json, " +
            "etag = excluded.etag, fetched_at_utc = excluded.fetched_at_utc;",
            statement =>
            {
                statement.BindText("@rates", json);
                statement.BindNullableText("@etag", record.ETag);
                statement.BindText("@fetched", fetchedAt);
            });
    }

    /// <summary>A conditional GET that came back 304: the rates stand, the check is recorded.</summary>
    public void Touch(DateTimeOffset checkedAtUtc)
    {
        string fetchedAt = NoteRecord.FormatTimestamp(checkedAtUtc);
        _repository.Execute(
            "UPDATE token_usage_pricing SET fetched_at_utc = @fetched WHERE id = 1;",
            statement => statement.BindText("@fetched", fetchedAt));
    }

    public static string SerializeRates(IReadOnlyDictionary<string, TokenUsageRate> rates) =>
        JsonSerializer.Serialize(
            rates.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new StoredRate(
                    pair.Key,
                    pair.Value.InputPerMillion,
                    pair.Value.OutputPerMillion,
                    pair.Value.CacheWrite5mPerMillion,
                    pair.Value.CacheWrite1hPerMillion,
                    pair.Value.CacheReadPerMillion))
                .ToArray(),
            ContractJson.Options);

    public static IReadOnlyDictionary<string, TokenUsageRate> DeserializeRates(string? json)
    {
        var rates = new Dictionary<string, TokenUsageRate>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return rates;
        }

        StoredRate[]? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredRate[]>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return rates;
        }

        foreach (StoredRate rate in stored ?? [])
        {
            if (!string.IsNullOrEmpty(rate.Model) &&
                rate.Input >= 0 && rate.Output >= 0 &&
                rate.CacheWrite5m >= 0 && rate.CacheWrite1h >= 0 && rate.CacheRead >= 0)
            {
                rates[rate.Model] = new TokenUsageRate(
                    rate.Input,
                    rate.Output,
                    rate.CacheWrite5m,
                    rate.CacheWrite1h,
                    rate.CacheRead);
            }
        }

        return rates;
    }

    private sealed record StoredRate(
        string Model,
        decimal Input,
        decimal Output,
        decimal CacheWrite5m,
        decimal CacheWrite1h,
        decimal CacheRead);
}
