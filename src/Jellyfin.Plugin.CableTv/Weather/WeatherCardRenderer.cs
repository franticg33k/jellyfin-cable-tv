using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.CableTv.Logos;
using SkiaSharp;

namespace Jellyfin.Plugin.CableTv.Weather;

/// <summary>
/// Draws a forecast card for a weather channel's Live TV stream: current conditions on the left, the coming days on
/// the right, in the blue of a 90s weather channel.
/// </summary>
public static class WeatherCardRenderer
{
    private const int Width = 1280;
    private const int Height = 720;
    private static readonly SKColor Yellow = new(0xFF, 0xE6, 0x3C);
    private static readonly SKColor Pale = new(0xC8, 0xD6, 0xF5);

    /// <summary>Draws the card.</summary>
    /// <param name="report">Forecast.</param>
    /// <param name="channelName">Channel name for the header.</param>
    /// <returns>PNG bytes.</returns>
    public static byte[] RenderPng(WeatherReport report, string channelName)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        using (var background = new SKPaint())
        {
            background.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Height), [new SKColor(0x10, 0x20, 0x80), new SKColor(0x00, 0x10, 0x40)], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, Width, Height, background);
        }

        using (var bar = new SKPaint { Color = new SKColor(0x06, 0x0C, 0x30, 0xE0) })
        {
            canvas.DrawRect(0, 0, Width, 96, bar);
        }

        var unit = report.Metric ? "°C" : "°F";
        Text(canvas, "LOCAL FORECAST", 48, 64, 44, Yellow);
        Text(canvas, report.Location.ToUpperInvariant(), Width - 48, 64, 34, SKColors.White, SKTextAlign.Right);

        // Current conditions.
        var now = report.Current;
        Text(canvas, "CURRENT CONDITIONS", 64, 160, 28, Pale);
        Text(canvas, Math.Round(now.Temperature).ToString(CultureInfo.InvariantCulture) + unit, 64, 320, 150, SKColors.White);
        Text(canvas, now.Condition, 64, 380, 42, Yellow);
        var wind = now.WindSpeed < 1 ? "Calm" : FormattableString.Invariant($"{now.WindDirection} {Math.Round(now.WindSpeed)} {(report.Metric ? "km/h" : "mph")}");
        string[] details =
        [
            FormattableString.Invariant($"Feels like  {Math.Round(now.FeelsLike)}{unit}"),
            FormattableString.Invariant($"Humidity  {now.Humidity}%"),
            "Wind  " + wind,
            FormattableString.Invariant($"Pressure  {now.Pressure} {(report.Metric ? "hPa" : "in")}"),
        ];
        for (var i = 0; i < details.Length; i++)
        {
            Text(canvas, details[i], 64, 450 + (i * 50), 32, SKColors.White);
        }

        // The coming days.
        var days = report.Daily.Take(5).ToList();
        const float left = 600;
        var column = (Width - left - 48) / Math.Max(days.Count, 1);
        Text(canvas, "EXTENDED FORECAST", left, 160, 28, Pale);
        using (var panel = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x18), IsAntialias = true })
        {
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(left - 12, 185, Width - 36, 640), 12), panel);
        }

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var x = left + (i * column) + (column / 2);
            var name = DateOnly.TryParse(day.Date, CultureInfo.InvariantCulture, out var date)
                ? (i == 0 ? "TODAY" : date.DayOfWeek.ToString()[..3].ToUpperInvariant())
                : day.Date;
            Text(canvas, name, x, 240, 34, Yellow, SKTextAlign.Center);
            Text(canvas, day.Condition, x, 330, 26, SKColors.White, SKTextAlign.Center);
            Text(canvas, "Hi " + Math.Round(day.High).ToString(CultureInfo.InvariantCulture), x, 420, 34, SKColors.White, SKTextAlign.Center);
            Text(canvas, "Lo " + Math.Round(day.Low).ToString(CultureInfo.InvariantCulture), x, 470, 34, Pale, SKTextAlign.Center);
            if (day.PrecipitationChance is int chance)
            {
                Text(canvas, chance.ToString(CultureInfo.InvariantCulture) + "%", x, 560, 30, new SKColor(0x7F, 0xD8, 0xFF), SKTextAlign.Center);
            }
        }

        var updated = "Updated " + report.UpdatedUtc.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (report.Sunrise is not null && report.Sunset is not null)
        {
            updated = "Sunrise " + report.Sunrise + "   Sunset " + report.Sunset + "   " + updated;
        }

        Text(canvas, updated, Width / 2f, 690, 24, Pale, SKTextAlign.Center);
        Text(canvas, channelName.ToUpperInvariant(), 48, 690, 24, Pale);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Text(SKCanvas canvas, string text, float x, float y, float size, SKColor color, SKTextAlign align = SKTextAlign.Left)
    {
        using var font = new SKFont(LogoRenderer.Typeface.Value, size) { Subpixel = true };
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 0xA0), IsAntialias = true };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x + 3, y + 3, align, font, shadow);
        canvas.DrawText(text, x, y, align, font, paint);
    }
}
