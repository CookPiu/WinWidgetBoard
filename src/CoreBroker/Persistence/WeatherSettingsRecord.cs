using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Local weather location settings. This record contains the user's automatic/manual mode,
/// but never the Windows permission result or a fetched weather payload.
///
/// <see cref="ApiKey"/> is the one field here that is a secret. It exists in this record so
/// the provider can be constructed with it, and must never be copied into a contract DTO, a
/// log line or a provider request key - callers outside the broker learn only that a key is
/// present. See <see cref="WeatherSettingsContract"/> and ADR-0038.
/// </summary>
public sealed record WeatherSettingsRecord
{
    public WeatherSettingsRecord(
        string instanceId,
        string label,
        double latitude,
        double longitude,
        bool useDeviceLocation,
        string unitSystem,
        int revision,
        string? updatedAtUtc,
        string providerId = WeatherSettingsContract.OpenMeteoProviderId,
        string apiHost = "",
        string? apiKey = null)
    {
        if (!WeatherSettingsContract.IsValidInstanceId(instanceId))
        {
            throw new ArgumentException(
                "Weather settings instance ID is invalid.",
                nameof(instanceId));
        }

        if (!WeatherSettingsContract.TryNormalizeLabel(label, out string? normalizedLabel) ||
            normalizedLabel is null)
        {
            throw new ArgumentException(
                "Weather settings label is invalid.",
                nameof(label));
        }

        if (!WeatherSettingsContract.IsValidCoordinates(latitude, longitude))
        {
            throw new ArgumentOutOfRangeException(
                nameof(latitude),
                "Weather coordinates are outside the valid geographic range.");
        }

        if (!WeatherSettingsContract.IsValidUnitSystem(unitSystem))
        {
            throw new ArgumentException(
                "Weather settings unit system is invalid.",
                nameof(unitSystem));
        }

        if (!WeatherSettingsContract.IsValidProviderId(providerId))
        {
            throw new ArgumentException(
                "Weather settings provider is invalid.",
                nameof(providerId));
        }

        if (!WeatherSettingsContract.TryNormalizeApiHost(
                apiHost,
                out string normalizedApiHost))
        {
            throw new ArgumentException(
                "Weather settings API host is invalid.",
                nameof(apiHost));
        }

        if (apiKey is not null && !WeatherSettingsContract.IsValidApiKey(apiKey))
        {
            throw new ArgumentException(
                "Weather settings API key is invalid.",
                nameof(apiKey));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (updatedAtUtc is not null)
        {
            updatedAtUtc = NoteRecord.FormatTimestamp(
                NoteRecord.ParseTimestamp(updatedAtUtc, nameof(updatedAtUtc)));
        }

        InstanceId = instanceId;
        Label = normalizedLabel;
        Latitude = latitude;
        Longitude = longitude;
        UseDeviceLocation = useDeviceLocation;
        UnitSystem = unitSystem;
        Revision = revision;
        UpdatedAtUtc = updatedAtUtc;
        ProviderId = providerId;
        ApiHost = normalizedApiHost;
        ApiKey = apiKey;
    }

    public string InstanceId { get; }

    public string Label { get; }

    public double Latitude { get; }

    public double Longitude { get; }

    public bool UseDeviceLocation { get; }

    public string UnitSystem { get; }

    public int Revision { get; }

    public string? UpdatedAtUtc { get; }

    public string ProviderId { get; }

    /// <summary>The account's own API host, empty for a source that does not use one.</summary>
    public string ApiHost { get; }

    /// <summary>
    /// The decrypted API key, or null when none is stored or the stored one could not be
    /// decrypted on this machine. Broker-internal - see the type-level remarks.
    /// </summary>
    public string? ApiKey { get; }

    public bool HasApiCredential => !string.IsNullOrEmpty(ApiKey);
}

public sealed class WeatherSettingsRevisionConflictException : InvalidOperationException
{
    public WeatherSettingsRevisionConflictException(
        string instanceId,
        int expectedRevision,
        int actualRevision)
        : base($"Weather settings '{instanceId}' changed since the expected revision was read.")
    {
        InstanceId = instanceId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string InstanceId { get; }

    public int ExpectedRevision { get; }

    public int ActualRevision { get; }
}
