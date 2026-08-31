using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.CoreBroker.Persistence;

/// <summary>
/// Local weather location settings. This record contains the user's automatic/manual mode,
/// but never the Windows permission result or a fetched weather payload.
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
        string? updatedAtUtc)
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
    }

    public string InstanceId { get; }

    public string Label { get; }

    public double Latitude { get; }

    public double Longitude { get; }

    public bool UseDeviceLocation { get; }

    public string UnitSystem { get; }

    public int Revision { get; }

    public string? UpdatedAtUtc { get; }
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
