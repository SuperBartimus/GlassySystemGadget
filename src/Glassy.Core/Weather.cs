using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Glassy.Core;

public enum WeatherLocationMode { Zip, CityState, LatLon, Device }

/// <summary>Open-Meteo's WMO weather codes folded into the handful of conditions this app draws an icon for.
/// "Cloudy" and "Overcast" share one icon tier (WMO has no code distinguishing them) - see the Weather settings note.</summary>
public enum WeatherCondition { Unknown, Clear, PartlyCloudy, Overcast, Fog, Drizzle, Rain, RainShowers, Sleet, Snow, SnowShowers, Thunderstorm }

public static class WeatherCodes
{
    public static WeatherCondition FromWmo(int code) => code switch
    {
        0 or 1 => WeatherCondition.Clear,
        2 => WeatherCondition.PartlyCloudy,
        3 => WeatherCondition.Overcast,
        45 or 48 => WeatherCondition.Fog,
        51 or 53 or 55 => WeatherCondition.Drizzle,
        56 or 57 or 66 or 67 => WeatherCondition.Sleet,
        61 or 63 or 65 => WeatherCondition.Rain,
        80 or 81 or 82 => WeatherCondition.RainShowers,
        71 or 73 or 75 or 77 => WeatherCondition.Snow,
        85 or 86 => WeatherCondition.SnowShowers,
        95 or 96 or 99 => WeatherCondition.Thunderstorm,
        _ => WeatherCondition.Unknown,
    };

    public static string Label(WeatherCondition c) => c switch
    {
        WeatherCondition.Clear => "Clear", WeatherCondition.PartlyCloudy => "Partly cloudy", WeatherCondition.Overcast => "Overcast/cloudy",
        WeatherCondition.Fog => "Fog", WeatherCondition.Drizzle => "Drizzle", WeatherCondition.Rain => "Rain", WeatherCondition.RainShowers => "Rain showers",
        WeatherCondition.Sleet => "Sleet/freezing rain", WeatherCondition.Snow => "Snow", WeatherCondition.SnowShowers => "Snow showers",
        WeatherCondition.Thunderstorm => "Thunderstorm", _ => "Unknown",
    };
}

public struct WeatherPoint
{
    public DateTime Time;
    /// <summary>Hourly: the forecast temperature. Daily: the day's high.</summary>
    public double TempC;
    /// <summary>Daily only; NaN for an hourly point.</summary>
    public double TempLowC;
    public double PrecipChance;   // 0-100
    public WeatherCondition Condition;
}

public struct WeatherAlert
{
    public string Event;
    public string Severity;
    public string Headline;
}

public sealed class WeatherSnapshot
{
    public string LocationName = "";
    public double TempC, FeelsLikeC, HumidityPct, WindKmh, WindGustKmh, PrecipChance;
    public WeatherCondition Condition;
    /// <summary>Up to 4 points, 2 hours apart, starting at or after now.</summary>
    public List<WeatherPoint> Hourly { get; } = new();
    /// <summary>Up to 3 points, one per day, starting tomorrow.</summary>
    public List<WeatherPoint> Daily { get; } = new();
    /// <summary>US-only (NWS has no equivalent free feed elsewhere) - always empty outside the US.</summary>
    public List<WeatherAlert> Alerts { get; } = new();
    public DateTime FetchedAtUtc;
}

/// <summary>What Settings asks the provider to resolve. Device mode expects the App layer to have already written
/// resolved coordinates into Lat/Lon via Windows' Geolocation API - Core stays free of any WinRT/OS dependency, so
/// from here Device looks exactly like LatLon except for the display name.</summary>
public readonly record struct WeatherQuery(WeatherLocationMode Mode, string Zip, string Country, string City, string State, double Lat, double Lon)
{
    public bool IsConfigured => Mode switch
    {
        WeatherLocationMode.Zip => Zip.Trim() != "",
        WeatherLocationMode.CityState => City.Trim() != "",
        WeatherLocationMode.LatLon => true,
        WeatherLocationMode.Device => Lat != 0 || Lon != 0,
        _ => false,
    };
}

public interface IWeatherProvider : IDisposable
{
    /// <summary>null until the first successful fetch.</summary>
    WeatherSnapshot Snapshot { get; }
    string LastError { get; }
    bool Refreshing { get; }
    DateTime? LastFetchUtc { get; }
    /// <summary>Cheap - call every Tick. Starts an async refresh when due (interval elapsed, the query changed, or
    /// forceNow) and nothing is already in flight; otherwise returns immediately.</summary>
    void Poll(WeatherQuery query, TimeSpan interval, bool forceNow);
}

/// <summary>Open-Meteo for the forecast (free, keyless) plus Open-Meteo's own geocoding for City/State and
/// Zippopotam.us for postal codes (also free, keyless). US severe-weather alerts come from api.weather.gov (NWS) -
/// no equivalent free feed exists for other countries, so Alerts is always empty outside the US. All three are
/// public, unauthenticated HTTP APIs: the only data ever sent is the resolved latitude/longitude (or the zip/city
/// text typed into Settings) - see the README's "Network use" section.</summary>
public sealed class OpenMeteoWeatherProvider : IWeatherProvider
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static OpenMeteoWeatherProvider() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("GlassySystemGadget/1.0 (+https://github.com/SuperBartimus/GlassySystemGadget)");

    public WeatherSnapshot Snapshot { get; private set; }
    public string LastError { get; private set; }
    public bool Refreshing { get; private set; }
    public DateTime? LastFetchUtc { get; private set; }

    WeatherQuery lastQuery;
    bool haveQuery;

    public void Poll(WeatherQuery query, TimeSpan interval, bool forceNow)
    {
        if (Refreshing || !query.IsConfigured) return;
        bool queryChanged = !haveQuery || !query.Equals(lastQuery);
        bool due = forceNow || queryChanged || Snapshot == null || LastFetchUtc == null || (DateTime.UtcNow - LastFetchUtc.Value) >= interval;
        if (!due) return;
        Refreshing = true; haveQuery = true; lastQuery = query;
        _ = Task.Run(() => RefreshAsync(query));
    }

    async Task RefreshAsync(WeatherQuery q)
    {
        try
        {
            var (lat, lon, name) = q.Mode switch
            {
                WeatherLocationMode.LatLon => (q.Lat, q.Lon, $"{q.Lat:0.###}, {q.Lon:0.###}"),
                WeatherLocationMode.Device => (q.Lat, q.Lon, "Current location"),
                WeatherLocationMode.Zip => await GeocodeZipAsync(q.Zip, q.Country),
                _ => await GeocodeCityAsync(q.City, q.State),
            };
            var snap = await FetchForecastAsync(lat, lon, name);
            Snapshot = snap; LastError = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            LastError = ex.Message; AppLog.Write("weather refresh failed: " + ex.Message);
        }
        finally { Refreshing = false; LastFetchUtc = DateTime.UtcNow; }
    }

    async Task<(double lat, double lon, string name)> GeocodeZipAsync(string zip, string country)
    {
        string url = $"https://api.zippopotam.us/{Uri.EscapeDataString(country)}/{Uri.EscapeDataString(zip.Trim())}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var place = doc.RootElement.GetProperty("places")[0];
        double lat = double.Parse(place.GetProperty("latitude").GetString()!, CultureInfo.InvariantCulture);
        double lon = double.Parse(place.GetProperty("longitude").GetString()!, CultureInfo.InvariantCulture);
        string city = place.GetProperty("place name").GetString() ?? zip;
        string state = place.TryGetProperty("state abbreviation", out var s) ? s.GetString() ?? "" : "";
        return (lat, lon, state == "" ? city : $"{city}, {state}");
    }

    async Task<(double lat, double lon, string name)> GeocodeCityAsync(string city, string state)
    {
        string q = state.Trim() == "" ? city.Trim() : $"{city.Trim()}, {state.Trim()}";
        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(q)}&count=1&language=en&format=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            throw new HttpRequestException($"no location found for \"{q}\"");
        var r = results[0];
        double lat = r.GetProperty("latitude").GetDouble(), lon = r.GetProperty("longitude").GetDouble();
        string name = r.GetProperty("name").GetString() ?? city;
        string admin1 = r.TryGetProperty("admin1", out var a1) ? a1.GetString() ?? "" : "";
        return (lat, lon, admin1 == "" ? name : $"{name}, {admin1}");
    }

    async Task<WeatherSnapshot> FetchForecastAsync(double lat, double lon, string name)
    {
        string url = "https://api.open-meteo.com/v1/forecast" +
            $"?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation_probability,weather_code,wind_speed_10m,wind_gusts_10m" +
            "&hourly=temperature_2m,precipitation_probability,weather_code" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
            "&timezone=auto&forecast_days=4";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var root = doc.RootElement;
        var cur = root.GetProperty("current");
        var snap = new WeatherSnapshot
        {
            LocationName = name,
            TempC = cur.GetProperty("temperature_2m").GetDouble(),
            FeelsLikeC = cur.GetProperty("apparent_temperature").GetDouble(),
            HumidityPct = cur.GetProperty("relative_humidity_2m").GetDouble(),
            WindKmh = cur.GetProperty("wind_speed_10m").GetDouble(),
            WindGustKmh = cur.GetProperty("wind_gusts_10m").GetDouble(),
            PrecipChance = cur.TryGetProperty("precipitation_probability", out var pp) ? pp.GetDouble() : 0,
            Condition = WeatherCodes.FromWmo(cur.GetProperty("weather_code").GetInt32()),
            FetchedAtUtc = DateTime.UtcNow,
        };

        var hourly = root.GetProperty("hourly");
        var hTimes = hourly.GetProperty("time"); var hTemp = hourly.GetProperty("temperature_2m");
        var hPrecip = hourly.GetProperty("precipitation_probability"); var hCode = hourly.GetProperty("weather_code");
        DateTime nowLocal = DateTime.Parse(cur.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);
        int start = 0;
        for (int i = 0; i < hTimes.GetArrayLength(); i++)
            if (DateTime.Parse(hTimes[i].GetString()!, CultureInfo.InvariantCulture) >= nowLocal) { start = i; break; }
        for (int i = start, taken = 0; i < hTimes.GetArrayLength() && taken < 4; i += 2, taken++)
        {
            snap.Hourly.Add(new WeatherPoint
            {
                Time = DateTime.Parse(hTimes[i].GetString()!, CultureInfo.InvariantCulture),
                TempC = hTemp[i].GetDouble(), TempLowC = double.NaN,
                PrecipChance = hPrecip[i].GetDouble(), Condition = WeatherCodes.FromWmo(hCode[i].GetInt32()),
            });
        }

        var daily = root.GetProperty("daily");
        var dTimes = daily.GetProperty("time"); var dMax = daily.GetProperty("temperature_2m_max"); var dMin = daily.GetProperty("temperature_2m_min");
        var dPrecip = daily.GetProperty("precipitation_probability_max"); var dCode = daily.GetProperty("weather_code");
        // Index 0 is today (already covered by the current-conditions block above); show the next 3 days.
        for (int i = 1; i < dTimes.GetArrayLength() && i <= 3; i++)
        {
            snap.Daily.Add(new WeatherPoint
            {
                Time = DateTime.Parse(dTimes[i].GetString()!, CultureInfo.InvariantCulture),
                TempC = dMax[i].GetDouble(), TempLowC = dMin[i].GetDouble(),
                PrecipChance = dPrecip[i].GetDouble(), Condition = WeatherCodes.FromWmo(dCode[i].GetInt32()),
            });
        }

        if (lat is >= 15 and <= 72 && lon is >= -180 and <= -60)   // NWS covers the US (+ territories); skip the call entirely elsewhere
        {
            try { await FetchUsAlertsAsync(lat, lon, snap); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            { AppLog.Write("weather alerts fetch failed (forecast itself still succeeded): " + ex.Message); }
        }
        return snap;
    }

    async Task FetchUsAlertsAsync(double lat, double lon, WeatherSnapshot into)
    {
        string url = $"https://api.weather.gov/alerts/active?point={lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/geo+json");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("features", out var features)) return;
        foreach (var f in features.EnumerateArray())
        {
            var props = f.GetProperty("properties");
            into.Alerts.Add(new WeatherAlert
            {
                Event = props.GetProperty("event").GetString() ?? "Alert",
                Severity = props.TryGetProperty("severity", out var sv) ? sv.GetString() ?? "" : "",
                Headline = props.TryGetProperty("headline", out var hl) ? hl.GetString() ?? "" : "",
            });
        }
    }

    public void Dispose() { }
}

/// <summary>Fixed reading for tests and for --demo (no network call at all).</summary>
public sealed class FakeWeatherProvider : IWeatherProvider
{
    public WeatherSnapshot Snapshot { get; set; }
    public string LastError { get; set; }
    public bool Refreshing { get; set; }
    public DateTime? LastFetchUtc { get; set; }
    public void Poll(WeatherQuery query, TimeSpan interval, bool forceNow) { }
    public void Dispose() { }
}
