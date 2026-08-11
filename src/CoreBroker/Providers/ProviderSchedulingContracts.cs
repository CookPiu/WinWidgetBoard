using System.Text.Json;

namespace WinWidgetBoard.CoreBroker.Providers;

/// <summary>
/// Identifies one deterministic, scheduled provider source. The key deliberately
/// excludes card instance identity so equal source requests can be coalesced.
/// </summary>
public sealed record ProviderRequestKey
{
    private const int MaxIdentifierLength = 128;

    public ProviderRequestKey(
        string providerId,
        string capability,
        string dataSourceKey,
        string argumentsFingerprint)
    {
        ProviderId = RequireIdentifier(providerId, nameof(providerId));
        Capability = RequireIdentifier(capability, nameof(capability));
        DataSourceKey = RequireIdentifier(dataSourceKey, nameof(dataSourceKey));
        ArgumentsFingerprint = RequireIdentifier(
            argumentsFingerprint,
            nameof(argumentsFingerprint));
    }

    public string ProviderId { get; }

    public string Capability { get; }

    public string DataSourceKey { get; }

    public string ArgumentsFingerprint { get; }

    private static string RequireIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaxIdentifierLength ||
            value.Any(char.IsWhiteSpace) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"'{parameterName}' must be at most {MaxIdentifierLength} " +
                "characters and cannot contain whitespace or control characters.",
                parameterName);
        }

        return value;
    }
}

/// <summary>
/// The only provider mode admitted by the M2.3.5 contract is scheduled work.
/// Event, persistent-task, sync and plugin providers are intentionally not
/// represented here.
/// </summary>
public sealed record ScheduledProviderDescriptor
{
    public ScheduledProviderDescriptor(
        string providerId,
        string capability,
        TimeSpan minimumInterval,
        TimeSpan visibleInterval,
        TimeSpan? hiddenInterval,
        TimeSpan? powerSaverInterval,
        TimeSpan requestTimeout,
        bool requiresNetwork,
        bool supportsManualRefresh,
        TimeSpan manualRefreshMinimumInterval,
        ProviderBackoffOptions backoffOptions)
    {
        ProviderId = RequireIdentifier(providerId, nameof(providerId));
        Capability = RequireIdentifier(capability, nameof(capability));
        MinimumInterval = RequirePositiveFinite(
            minimumInterval,
            nameof(minimumInterval));
        VisibleInterval = RequireCadence(
            visibleInterval,
            MinimumInterval,
            nameof(visibleInterval));
        HiddenInterval = RequireOptionalCadence(
            hiddenInterval,
            MinimumInterval,
            nameof(hiddenInterval));
        PowerSaverInterval = RequireOptionalCadence(
            powerSaverInterval,
            MinimumInterval,
            nameof(powerSaverInterval));
        RequestTimeout = RequirePositiveFinite(
            requestTimeout,
            nameof(requestTimeout));
        RequiresNetwork = requiresNetwork;
        SupportsManualRefresh = supportsManualRefresh;
        ManualRefreshMinimumInterval = RequirePositiveFinite(
            manualRefreshMinimumInterval,
            nameof(manualRefreshMinimumInterval));
        BackoffOptions = backoffOptions ??
            throw new ArgumentNullException(nameof(backoffOptions));
    }

    public string ProviderId { get; }

    public string Capability { get; }

    public TimeSpan MinimumInterval { get; }

    public TimeSpan VisibleInterval { get; }

    public TimeSpan? HiddenInterval { get; }

    public TimeSpan? PowerSaverInterval { get; }

    public TimeSpan RequestTimeout { get; }

    public bool RequiresNetwork { get; }

    public bool SupportsManualRefresh { get; }

    public TimeSpan ManualRefreshMinimumInterval { get; }

    public ProviderBackoffOptions BackoffOptions { get; }

    private static TimeSpan RequireCadence(
        TimeSpan value,
        TimeSpan minimum,
        string parameterName)
    {
        TimeSpan validated = RequirePositiveFinite(value, parameterName);
        if (validated < minimum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Cadence cannot be shorter than the minimum interval.");
        }

        return validated;
    }

    private static TimeSpan? RequireOptionalCadence(
        TimeSpan? value,
        TimeSpan minimum,
        string parameterName) =>
        value is null
            ? null
            : RequireCadence(value.Value, minimum, parameterName);

    private static TimeSpan RequirePositiveFinite(
        TimeSpan value,
        string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "The interval must be finite and positive.");
        }

        return value;
    }

    private static string RequireIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 ||
            value.Any(char.IsWhiteSpace) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"'{parameterName}' must be at most 128 characters and " +
                "cannot contain whitespace or control characters.",
                parameterName);
        }

        return value;
    }
}

public enum ProviderNetworkState
{
    Unknown = 0,
    Online,
    Offline,
    Constrained,
}

public enum ProviderPowerState
{
    Unknown = 0,
    Normal,
    BatterySaver,
    Critical,
}

public readonly record struct ProviderRefreshVisibility(
    bool PanelVisible,
    bool InViewport,
    bool DisplayConnected)
{
    public bool IsVisible =>
        PanelVisible && InViewport && DisplayConnected;
}

public enum ProviderRefreshResultKind
{
    Unknown = 0,
    Success,
    Empty,
    Offline,
    PermissionRequired,
    Unavailable,
    Failed,
    RateLimited,
    TimedOut,
}

public enum ProviderManualRefreshOutcome
{
    Unknown = 0,
    Started,
    Coalesced,
    RateLimited,
    NotVisible,
    Paused,
    Offline,
    NotRegistered,
    Disposed,
}

/// <summary>
/// An immutable request passed to a scheduled provider. JsonElement.Clone
/// detaches the request from the caller's JsonDocument ownership.
/// </summary>
public sealed record ProviderRefreshRequest
{
    public ProviderRefreshRequest(
        Guid requestId,
        ProviderRequestKey key,
        DateTimeOffset deadlineUtc,
        JsonElement arguments,
        long generation)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException(
                "Request ID must not be empty.",
                nameof(requestId));
        }

        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                generation,
                "Generation cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(key);
        RequestId = requestId;
        Key = key;
        DeadlineUtc = deadlineUtc.ToUniversalTime();
        Arguments = CloneJson(arguments);
        Generation = generation;
    }

    public Guid RequestId { get; }

    public ProviderRequestKey Key { get; }

    public DateTimeOffset DeadlineUtc { get; }

    public JsonElement Arguments { get; }

    public long Generation { get; }

    private static JsonElement CloneJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            using JsonDocument document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

        return value.Clone();
    }
}

/// <summary>
/// A normalized provider outcome. Invalid result enum values fail safe to
/// Failed; cancellation is deliberately not a result kind and is handled by
/// the scheduler that owns the request token.
/// </summary>
public sealed record ProviderRefreshResult
{
    public ProviderRefreshResult(
        Guid requestId,
        ProviderRefreshResultKind kind,
        JsonElement payload,
        DateTimeOffset producedAtUtc,
        DateTimeOffset? validUntilUtc = null,
        string? errorCode = null,
        TimeSpan? retryAfter = null)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException(
                "Request ID must not be empty.",
                nameof(requestId));
        }

        if (retryAfter is { } retry &&
            (retry < TimeSpan.Zero || retry == Timeout.InfiniteTimeSpan))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryAfter),
                retryAfter,
                "Retry-After must be finite and non-negative.");
        }

        RequestId = requestId;
        Kind = kind is ProviderRefreshResultKind.Unknown ||
            !Enum.IsDefined(kind)
            ? ProviderRefreshResultKind.Failed
            : kind;
        Payload = CloneJson(payload);
        ProducedAtUtc = producedAtUtc.ToUniversalTime();
        ValidUntilUtc = validUntilUtc?.ToUniversalTime();
        ErrorCode = errorCode is null
            ? null
            : RequireIdentifier(errorCode, nameof(errorCode));
        RetryAfter = retryAfter;
    }

    public Guid RequestId { get; }

    public ProviderRefreshResultKind Kind { get; }

    public JsonElement Payload { get; }

    public DateTimeOffset ProducedAtUtc { get; }

    public DateTimeOffset? ValidUntilUtc { get; }

    public string? ErrorCode { get; }

    public TimeSpan? RetryAfter { get; }

    private static JsonElement CloneJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            using JsonDocument document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

        return value.Clone();
    }

    private static string RequireIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 ||
            value.Any(char.IsWhiteSpace) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"'{parameterName}' must be at most 128 characters and " +
                "cannot contain whitespace or control characters.",
                parameterName);
        }

        return value;
    }
}

public interface IProviderRefreshSource
{
    ScheduledProviderDescriptor Descriptor { get; }

    ValueTask<ProviderRefreshResult> FetchAsync(
        ProviderRefreshRequest request,
        CancellationToken cancellationToken);
}

public interface IProviderRefreshSink
{
    ValueTask ApplyAsync(
        ProviderRefreshRequest request,
        ProviderRefreshResult result,
        CancellationToken cancellationToken);
}
