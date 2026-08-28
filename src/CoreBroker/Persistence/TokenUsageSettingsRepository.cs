using System.Text.Json;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Which vendors the card counts. Nothing measured is persisted - only the user's choice of
/// what to look at, the same discipline the hardware monitor's settings follow.
/// </summary>
public sealed record TokenUsageSettingsRecord
{
    public TokenUsageSettingsRecord(
        string instanceId,
        IReadOnlyList<string> enabledVendors,
        int revision,
        string? updatedAtUtc)
    {
        if (!TokenUsageContract.IsValidInstanceId(instanceId))
        {
            throw new ArgumentException(
                "Token usage settings instance ID is invalid.",
                nameof(instanceId));
        }

        if (!TokenUsageContract.IsValidVendorList(enabledVendors))
        {
            throw new ArgumentException(
                "Token usage vendor selection is invalid.",
                nameof(enabledVendors));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (updatedAtUtc is not null)
        {
            updatedAtUtc = NoteRecord.FormatTimestamp(
                NoteRecord.ParseTimestamp(updatedAtUtc, nameof(updatedAtUtc)));
        }

        InstanceId = instanceId;
        EnabledVendors = enabledVendors.ToArray();
        Revision = revision;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string InstanceId { get; }

    public IReadOnlyList<string> EnabledVendors { get; }

    public int Revision { get; }

    public string? UpdatedAtUtc { get; }

    public static string SerializeVendors(IReadOnlyList<string> vendors) =>
        JsonSerializer.Serialize(vendors, ContractJson.Options);

    /// <summary>
    /// Reads a stored selection back. A row written by a newer build can name a vendor this
    /// one cannot read; unknown entries are dropped rather than failing the read, so an older
    /// build still starts and still counts everything it does understand.
    /// </summary>
    public static IReadOnlyList<string> DeserializeVendors(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        List<string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<string>>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }

        if (parsed is null)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var vendors = new List<string>(parsed.Count);
        // Contract order rather than stored order, so the card's pages are always laid out the
        // same way regardless of the order they happened to be saved in.
        foreach (string vendor in TokenUsageContract.VendorIds)
        {
            if (parsed.Contains(vendor, StringComparer.Ordinal) && seen.Add(vendor))
            {
                vendors.Add(vendor);
            }
        }

        return vendors;
    }
}

public sealed class TokenUsageSettingsRevisionConflictException : InvalidOperationException
{
    public TokenUsageSettingsRevisionConflictException(
        string instanceId,
        int expectedRevision,
        int actualRevision)
        : base(
            $"Token usage settings '{instanceId}' changed since the expected revision was " +
            "read.")
    {
        InstanceId = instanceId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string InstanceId { get; }

    public int ExpectedRevision { get; }

    public int ActualRevision { get; }
}

public sealed class TokenUsageSettingsRepository
{
    private readonly SqliteRepository _repository;

    public TokenUsageSettingsRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _repository = new SqliteRepository(database);
    }

    public TokenUsageSettingsRecord? Get(string instanceId)
    {
        ValidateInstanceId(instanceId);
        TokenUsageSettingsRecord? result = null;
        _repository.Query(
            "SELECT instance_id, enabled_vendors, revision, updated_at_utc " +
            "FROM token_usage_settings WHERE instance_id = @instance;",
            statement => statement.BindText("@instance", instanceId),
            statement => result = ReadRecord(statement));
        return result;
    }

    public TokenUsageSettingsRecord Save(
        string instanceId,
        IReadOnlyList<string> enabledVendors,
        int expectedRevision,
        DateTimeOffset? nowUtc = null)
    {
        ValidateInstanceId(instanceId);
        if (!TokenUsageContract.IsValidVendorList(enabledVendors))
        {
            throw new ArgumentException(
                "Token usage vendor selection is invalid.",
                nameof(enabledVendors));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        using SqliteTransaction transaction = _repository.BeginTransaction();
        TokenUsageSettingsRecord? current = Get(instanceId);
        int actualRevision = current?.Revision ?? 0;
        if (actualRevision != expectedRevision)
        {
            throw new TokenUsageSettingsRevisionConflictException(
                instanceId,
                expectedRevision,
                actualRevision);
        }

        int nextRevision = checked(actualRevision + 1);
        string updatedAtUtc = NoteRecord.FormatTimestamp(nowUtc ?? DateTimeOffset.UtcNow);
        string vendorsJson = TokenUsageSettingsRecord.SerializeVendors(enabledVendors);

        if (current is null)
        {
            _repository.Execute(
                "INSERT INTO token_usage_settings " +
                "(instance_id, enabled_vendors, revision, updated_at_utc) " +
                "VALUES (@instance, @vendors, @revision, @updated);",
                statement =>
                {
                    statement.BindText("@instance", instanceId);
                    statement.BindText("@vendors", vendorsJson);
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                });
        }
        else
        {
            int changes = _repository.Execute(
                "UPDATE token_usage_settings SET enabled_vendors = @vendors, " +
                "revision = @revision, updated_at_utc = @updated " +
                "WHERE instance_id = @instance AND revision = @expected;",
                statement =>
                {
                    statement.BindText("@vendors", vendorsJson);
                    statement.BindInt("@revision", nextRevision);
                    statement.BindText("@updated", updatedAtUtc);
                    statement.BindText("@instance", instanceId);
                    statement.BindInt("@expected", expectedRevision);
                });
            if (changes != 1)
            {
                throw new TokenUsageSettingsRevisionConflictException(
                    instanceId,
                    expectedRevision,
                    current.Revision);
            }
        }

        transaction.Commit();
        return new TokenUsageSettingsRecord(
            instanceId,
            enabledVendors,
            nextRevision,
            updatedAtUtc);
    }

    private static TokenUsageSettingsRecord ReadRecord(SqliteStatement statement) =>
        new(
            statement.ReadText(0) ??
                throw new SqliteException(1, "token_usage_settings.instance_id is NULL."),
            TokenUsageSettingsRecord.DeserializeVendors(statement.ReadText(1)),
            statement.ReadInt(2),
            statement.ReadText(3) ??
                throw new SqliteException(1, "token_usage_settings.updated_at_utc is NULL."));

    private static void ValidateInstanceId(string instanceId)
    {
        if (!TokenUsageContract.IsValidInstanceId(instanceId))
        {
            throw new ArgumentException(
                "Token usage settings instance ID is invalid.",
                nameof(instanceId));
        }
    }
}
