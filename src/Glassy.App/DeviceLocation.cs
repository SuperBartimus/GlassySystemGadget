using System.Runtime.InteropServices;
using Glassy.Core;
using Windows.Devices.Geolocation;

namespace Glassy.App;

/// <summary>Windows' own Geolocation API (the same one Store apps use) - the only part of the Weather feature
/// that needs WinRT, which is why Glassy.App (not Glassy.Core) targets a windows10.0 TFM. One-shot: Settings'
/// "Use my current location" button calls this and writes the resolved lat/lon straight into PanelConfig, same
/// as a Zip/City lookup would - Engine's WeatherQuery treats Device exactly like LatLon from then on.</summary>
public static class DeviceLocation
{
    public static async Task<(double lat, double lon)?> TryGetAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed) return null;
            var geo = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var pos = await geo.GetGeopositionAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15));
            return (pos.Coordinate.Point.Position.Latitude, pos.Coordinate.Point.Position.Longitude);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or TimeoutException)
        {
            AppLog.Write("device location failed: " + ex.Message);
            return null;
        }
    }
}
