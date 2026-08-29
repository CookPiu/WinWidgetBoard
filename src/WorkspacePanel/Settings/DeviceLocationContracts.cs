namespace WinWidgetBoard.WorkspacePanel.Settings;

public enum DeviceLocationStatus
{
    Available,
    Denied,
    Unavailable,
}

public readonly record struct DeviceLocationResult(
    DeviceLocationStatus Status,
    double Latitude,
    double Longitude)
{
    public static DeviceLocationResult Available(double latitude, double longitude) =>
        new(DeviceLocationStatus.Available, latitude, longitude);

    public static DeviceLocationResult Denied() =>
        new(DeviceLocationStatus.Denied, 0, 0);

    public static DeviceLocationResult Unavailable() =>
        new(DeviceLocationStatus.Unavailable, 0, 0);
}

public interface IDeviceLocationProvider
{
    Task<DeviceLocationResult> GetCurrentLocationAsync(
        CancellationToken cancellationToken);
}
