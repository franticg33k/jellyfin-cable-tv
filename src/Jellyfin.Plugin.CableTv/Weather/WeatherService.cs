using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Weather;

/// <summary>
/// Forecasts for weather channels from Open-Meteo (free, no key), cached for a while, and a drawn forecast card for
/// Live TV clients that can't draw one themselves.
/// </summary>
public class WeatherService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WeatherService> _logger;
    private readonly ConcurrentDictionary<string, Cached> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (double Lat, double Lon, string Name)> _places = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fetch = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="WeatherService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public WeatherService(IHttpClientFactory httpClientFactory, ILogger<WeatherService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// The forecast for a weather channel; cached, so calling it often is cheap. Returns the last good forecast when a
    /// refresh fails, and null when there has never been one.
    /// </summary>
    /// <param name="channel">Weather channel.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The forecast.</returns>
    public async Task<WeatherReport?> GetAsync(ChannelDefinition channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var location = channel.WeatherLocation?.Trim();
        if (string.IsNullOrEmpty(location))
        {
            return null;
        }

        var key = location + (channel.WeatherMetric ? "|m" : "|i");
        if (_reports.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.RefreshAt)
        {
            return cached.Report;
        }

        await _fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_reports.TryGetValue(key, out cached) && DateTime.UtcNow < cached.RefreshAt)
            {
                return cached.Report;
            }

            try
            {
                var report = await FetchAsync(location, channel.WeatherMetric, cancellationToken).ConfigureAwait(false);
                _reports[key] = new Cached(report, DateTime.UtcNow + CacheFor);
                return report;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Couldn't fetch the forecast for {Location}", location);
                _reports[key] = new Cached(cached?.Report, DateTime.UtcNow + RetryAfter);
                return cached?.Report;
            }
        }
        finally
        {
            _fetch.Release();
        }
    }

    /// <summary>
    /// A forecast card image for the channel's Live TV stream, redrawn when the forecast changes.
    /// </summary>
    /// <param name="channel">Weather channel.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Path of a PNG, or null when there's no forecast or it can't be drawn.</returns>
    public async Task<string?> CardAsync(ChannelDefinition channel, CancellationToken cancellationToken)
    {
        var report = await GetAsync(channel, cancellationToken).ConfigureAwait(false);
        if (report is null || Plugin.Instance is not { } plugin)
        {
            return null;
        }

        var directory = Path.Combine(plugin.DataFolderPath, "weather");
        var stamp = report.UpdatedUtc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, Library.TitleNormalizer.Slug(channel.Id) + "-" + stamp + ".png");
        if (File.Exists(path))
        {
            return path;
        }

        try
        {
            Directory.CreateDirectory(directory);
            foreach (var old in Directory.GetFiles(directory, Library.TitleNormalizer.Slug(channel.Id) + "-*.png"))
            {
                File.Delete(old);
            }

            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, WeatherCardRenderer.RenderPng(report, channel.Name), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TypeInitializationException or DllNotFoundException)
        {
            _logger.LogWarning(ex, "Couldn't draw the forecast card for {Channel}", channel.Id);
            return null;
        }
    }

    private async Task<WeatherReport> FetchAsync(string location, bool metric, CancellationToken cancellationToken)
    {
        var (lat, lon, name) = await LocateAsync(location, cancellationToken).ConfigureAwait(false);
        var units = metric ? string.Empty : "&temperature_unit=fahrenheit&wind_speed_unit=mph";
        var url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&timezone=auto&forecast_days=7{units}")
            + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m,pressure_msl"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset"
            + "&hourly=temperature_2m,weather_code,precipitation_probability";
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var doc = JsonDocument.Parse(await client.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false));
        var root = doc.RootElement;

        var current = root.GetProperty("current");
        var code = current.GetProperty("weather_code").GetInt32();
        var pressure = current.GetProperty("pressure_msl").GetDouble();
        var now = new WeatherNow(
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(),
            (int)Math.Round(current.GetProperty("relative_humidity_2m").GetDouble()),
            current.GetProperty("wind_speed_10m").GetDouble(),
            WeatherCodes.Compass(current.GetProperty("wind_direction_10m").GetDouble()),
            metric ? pressure : Math.Round(pressure * 0.02953, 2),
            code,
            WeatherCodes.Describe(code).Long);

        var daily = root.GetProperty("daily");
        var dates = daily.GetProperty("time").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
        var days = dates.Select((date, i) =>
        {
            var dayCode = daily.GetProperty("weather_code")[i].GetInt32();
            return new WeatherDay(
                date,
                daily.GetProperty("temperature_2m_max")[i].GetDouble(),
                daily.GetProperty("temperature_2m_min")[i].GetDouble(),
                dayCode,
                WeatherCodes.Describe(dayCode).Short,
                IntOrNull(daily.GetProperty("precipitation_probability_max")[i]));
        }).ToList();

        var hourly = root.GetProperty("hourly");
        var nowLocal = current.GetProperty("time").GetString() ?? string.Empty;
        var times = hourly.GetProperty("time").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
        // Start at the hour that contains now ("2026-09-26T11:15" starts at "11:00").
        var hour = nowLocal.Length >= 13 ? nowLocal[..13] : nowLocal;
        var from = Math.Max(0, times.FindIndex(t => string.CompareOrdinal(t.Length >= 13 ? t[..13] : t, hour) >= 0));
        var hours = Enumerable.Range(from, Math.Min(12, times.Count - from)).Select(i => new WeatherHour(
            times[i],
            hourly.GetProperty("temperature_2m")[i].GetDouble(),
            hourly.GetProperty("weather_code")[i].GetInt32(),
            IntOrNull(hourly.GetProperty("precipitation_probability")[i]))).ToList();

        static string? Clock(JsonElement e) => e.GetString() is { Length: >= 16 } s ? s[11..16] : null;
        return new WeatherReport(
            name,
            metric,
            DateTime.UtcNow,
            now,
            days,
            hours,
            Clock(daily.GetProperty("sunrise")[0]),
            Clock(daily.GetProperty("sunset")[0]));
    }

    private static int? IntOrNull(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (int)Math.Round(e.GetDouble()) : null;

    /// <summary>"lat,lon" as written, or a place name looked up with Open-Meteo's geocoder.</summary>
    private async Task<(double Lat, double Lon, string Name)> LocateAsync(string location, CancellationToken cancellationToken)
    {
        var parts = location.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            return (lat, lon, location);
        }

        if (_places.TryGetValue(location, out var known))
        {
            return known;
        }

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&name=" + Uri.EscapeDataString(location);
        using var doc = JsonDocument.Parse(await client.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("No place called " + location);
        }

        var first = results[0];
        var place = (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble(), first.GetProperty("name").GetString() ?? location);
        _places[location] = place;
        return place;
    }

    private sealed record Cached(WeatherReport? Report, DateTime RefreshAt);
}
