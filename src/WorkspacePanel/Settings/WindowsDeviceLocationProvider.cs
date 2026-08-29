using System.Runtime.InteropServices;
using Windows.Devices.Geolocation;
using WinWidgetBoard.Contracts.Protocol;

namespace WinWidgetBoard.WorkspacePanel.Settings;

/// <summary>
/// Reads one foreground position from the Windows location service. Permission is owned by
/// Windows; this type never stores the permission result and never performs reverse geocoding.
/// </summary>
public sealed class WindowsDeviceLocationProvider : IDeviceLocationProvider
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<DeviceLocationResult> GetCurrentLocationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            GeolocationAccessStatus access = await Geolocator.RequestAccessAsync();
            if (access == GeolocationAccessStatus.Denied)
            {
                return DeviceLocationResult.Denied();
            }

            if (access != GeolocationAccessStatus.Allowed)
            {
                return DeviceLocationResult.Unavailable();
            }

            var locator = new Geolocator
            {
                DesiredAccuracy = PositionAccuracy.Default,
            };
            Geoposition position = await locator.GetGeopositionAsync(MaximumAge, Timeout);
            cancellationToken.ThrowIfCancellationRequested();
            BasicGeoposition coordinate = position.Coordinate.Point.Position;
            return WeatherSettingsContract.IsValidCoordinates(
                    coordinate.Latitude,
                    coordinate.Longitude)
                ? DeviceLocationResult.Available(
                    coordinate.Latitude,
                    coordinate.Longitude)
                : DeviceLocationResult.Unavailable();
        }
        catch (UnauthorizedAccessException)
        {
            return DeviceLocationResult.Denied();
        }
        catch (COMException)
        {
            return DeviceLocationResult.Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeviceLocationResult.Unavailable();
        }
    }
}
