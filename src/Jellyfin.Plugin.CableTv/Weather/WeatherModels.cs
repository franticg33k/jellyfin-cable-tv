using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.CableTv.Weather;

/// <summary>A weather channel's forecast, as clients draw it.</summary>
/// <param name="Location">Place name.</param>
/// <param name="Metric">Whether values are metric (°C, km/h, hPa) rather than imperial (°F, mph, inHg).</param>
/// <param name="UpdatedUtc">When the forecast was fetched.</param>
/// <param name="Current">Current conditions.</param>
/// <param name="Daily">The next days.</param>
/// <param name="Hourly">The next hours.</param>
/// <param name="Sunrise">Today's sunrise, local time "HH:mm".</param>
/// <param name="Sunset">Today's sunset, local time "HH:mm".</param>
public sealed record WeatherReport(
    string Location,
    bool Metric,
    DateTime UpdatedUtc,
    WeatherNow Current,
    IReadOnlyList<WeatherDay> Daily,
    IReadOnlyList<WeatherHour> Hourly,
    string? Sunrise,
    string? Sunset);

/// <summary>Current conditions.</summary>
/// <param name="Temperature">Temperature.</param>
/// <param name="FeelsLike">Apparent temperature.</param>
/// <param name="Humidity">Relative humidity, percent.</param>
/// <param name="WindSpeed">Wind speed.</param>
/// <param name="WindDirection">Compass direction the wind comes from, e.g. "NW".</param>
/// <param name="Pressure">Pressure at sea level.</param>
/// <param name="Code">WMO weather code.</param>
/// <param name="Condition">Condition in words, e.g. "Partly cloudy".</param>
public sealed record WeatherNow(double Temperature, double FeelsLike, int Humidity, double WindSpeed, string WindDirection, double Pressure, int Code, string Condition);

/// <summary>One day of the forecast.</summary>
/// <param name="Date">Date, "yyyy-MM-dd".</param>
/// <param name="High">High temperature.</param>
/// <param name="Low">Low temperature.</param>
/// <param name="Code">WMO weather code.</param>
/// <param name="Condition">Short condition, e.g. "Pt Cloudy".</param>
/// <param name="PrecipitationChance">Chance of precipitation, percent.</param>
public sealed record WeatherDay(string Date, double High, double Low, int Code, string Condition, int? PrecipitationChance);

/// <summary>One hour of the forecast.</summary>
/// <param name="Time">Local time, "yyyy-MM-ddTHH:mm".</param>
/// <param name="Temperature">Temperature.</param>
/// <param name="Code">WMO weather code.</param>
/// <param name="PrecipitationChance">Chance of precipitation, percent.</param>
public sealed record WeatherHour(string Time, double Temperature, int Code, int? PrecipitationChance);

/// <summary>Words for WMO weather codes.</summary>
public static class WeatherCodes
{
    /// <summary>Long and short descriptions of a WMO weather code.</summary>
    /// <param name="code">Code.</param>
    /// <returns>The descriptions.</returns>
    public static (string Long, string Short) Describe(int code) => code switch
    {
        0 => ("Sunny", "Sunny"),
        1 => ("Mostly sunny", "M Sunny"),
        2 => ("Partly cloudy", "Pt Cloudy"),
        3 => ("Cloudy", "Cloudy"),
        45 or 48 => ("Fog", "Fog"),
        51 or 53 or 55 => ("Drizzle", "Drizzle"),
        56 or 57 => ("Freezing drizzle", "Frz Drzl"),
        61 => ("Light rain", "Lt Rain"),
        63 => ("Rain", "Rain"),
        65 => ("Heavy rain", "Hvy Rain"),
        66 or 67 => ("Freezing rain", "Frz Rain"),
        71 => ("Light snow", "Lt Snow"),
        73 => ("Snow", "Snow"),
        75 => ("Heavy snow", "Hvy Snow"),
        77 => ("Snow grains", "Snow"),
        80 or 81 => ("Showers", "Showers"),
        82 => ("Heavy showers", "Hvy Shwr"),
        85 or 86 => ("Snow showers", "Snw Shwr"),
        95 => ("Thunderstorms", "T-Storm"),
        96 or 99 => ("Thunderstorms with hail", "T-Storm"),
        _ => ("Unknown", "--"),
    };

    /// <summary>Compass point for a direction in degrees.</summary>
    /// <param name="degrees">Direction.</param>
    /// <returns>For example "NW".</returns>
    public static string Compass(double degrees)
    {
        string[] points = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];
        return points[(int)Math.Round((((degrees % 360) + 360) % 360) / 22.5) % 16];
    }
}
