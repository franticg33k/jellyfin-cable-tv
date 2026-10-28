using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CableTv.Logos;
using SkiaSharp;

namespace Jellyfin.Plugin.CableTv.Streaming;

/// <summary>
/// Draws the "Coming up next" and "Tonight at 8:00 PM" cards that air during breaks and gaps between programmes, in the
/// blue of a 90s channel, like the weather card.
/// </summary>
public static class PromoCardRenderer
{
    /// <summary>Changes when the drawing changes, so cached cards are redrawn.</summary>
    public const int StyleVersion = 1;

    private const int Width = 1280;
    private const int Height = 720;
    private static readonly SKColor Yellow = new(0xFF, 0xE6, 0x3C);
    private static readonly SKColor Pale = new(0xC8, 0xD6, 0xF5);

    /// <summary>
    /// Draws a card.
    /// </summary>
    /// <param name="info">What to announce.</param>
    /// <param name="channelNumber">The channel's number.</param>
    /// <param name="channelName">The channel's name.</param>
    /// <returns>PNG bytes.</returns>
    public static byte[] RenderPng(PromoInfo info, string channelNumber, string channelName)
    {
        ArgumentNullException.ThrowIfNull(info);
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
            canvas.DrawRect(0, Height - 80, Width, 80, bar);
        }

        Text(canvas, (channelNumber + "  " + channelName).Trim().ToUpperInvariant(), 48, 64, 40, Yellow, SKTextAlign.Left, Width - 96);
        Text(canvas, info.Heading, 64, 230, 52, Yellow, SKTextAlign.Left, Width - 128);
        if (info.Kind == PromoKind.UpNext)
        {
            // The heading of a "Tonight at 8:00 PM" card already carries the time.
            Text(canvas, info.TimeText, Width - 64, 230, 52, SKColors.White, SKTextAlign.Right, 320);
        }

        var lines = Wrap(info.Title.ToUpperInvariant(), Width - 128, 96, out var size);
        var y = 340f;
        foreach (var line in lines)
        {
            Text(canvas, line, 64, y, size, SKColors.White, SKTextAlign.Left, Width - 128);
            y += size * 1.05f;
        }

        if (!string.IsNullOrWhiteSpace(info.Subtitle))
        {
            Text(canvas, info.Subtitle!, 64, y + 20, 44, Pale, SKTextAlign.Left, Width - 128);
            y += 70;
        }

        if (!string.IsNullOrWhiteSpace(info.Details))
        {
            Text(canvas, info.Details!, 64, y + 30, 34, Pale, SKTextAlign.Left, Width - 128);
        }

        Text(canvas, "STAY TUNED", Width / 2f, Height - 28, 34, Pale, SKTextAlign.Center, Width - 128);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Text(SKCanvas canvas, string text, float x, float baseline, float size, SKColor color, SKTextAlign align, float maxWidth)
    {
        using var font = new SKFont(LogoRenderer.Typeface.Value, size) { Embolden = true, Subpixel = true };
        while (size > 20 && font.MeasureText(text) > maxWidth)
        {
            size -= 2;
            font.Size = size;
        }

        using var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(110), IsAntialias = true };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x + 3, baseline + 3, align, font, shadow);
        canvas.DrawText(text, x, baseline, align, font, paint);
    }

    /// <summary>Fits a title on one to three lines at the largest size that fits.</summary>
    private static string[] Wrap(string text, float maxWidth, float startSize, out float size)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        using var font = new SKFont(LogoRenderer.Typeface.Value, startSize) { Embolden = true };
        for (size = startSize; size > 40; size -= 4)
        {
            font.Size = size;
            var lines = new List<string>();
            var current = string.Empty;
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && font.MeasureText(candidate) > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            lines.Add(current);
            if (lines.Count <= 2 && lines.All(l => font.MeasureText(l) <= maxWidth))
            {
                return [.. lines];
            }
        }

        return [text];
    }
}
