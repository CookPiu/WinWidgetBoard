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
        Revision = revision;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string InstanceId { get; }

    public string Label { get; }

    public double Latitude { get; }

    public double Longitude { get; }

    public bool UseDeviceLocation { get; }

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
