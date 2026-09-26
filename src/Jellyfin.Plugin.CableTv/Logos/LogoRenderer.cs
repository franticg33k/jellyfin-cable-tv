using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.CableTv.Scheduling;
using SkiaSharp;

namespace Jellyfin.Plugin.CableTv.Logos;

/// <summary>
/// Draws a simple channel logo: the channel name on a coloured plate, the colour derived from the name so every
/// channel keeps its own look. Uses the SkiaSharp library Jellyfin already ships and an embedded open font
/// (Oswald, SIL Open Font License), so it works on servers without system fonts.
/// </summary>
public static class LogoRenderer
{
    /// <summary>Changes when the drawing changes, so cached logos are redrawn.</summary>
    public const int StyleVersion = 1;

    /// <summary>Logo width in pixels (16:9).</summary>
    public const int Width = 640;

    /// <summary>Logo height in pixels.</summary>
    public const int Height = 360;

    private static readonly Lazy<SKTypeface> Typeface = new(LoadTypeface);

    /// <summary>
    /// Draws a logo as PNG.
    /// </summary>
    /// <param name="name">Channel name.</param>
    /// <returns>PNG bytes.</returns>
    public static byte[] RenderPng(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var hue = (float)(StableHash.Add(StableHash.Start(), name.ToUpperInvariant()) % 360);
        var top = SKColor.FromHsv(hue, 70, 72);
        var bottom = SKColor.FromHsv((hue + 25) % 360, 80, 38);
        var plate = new SKRect(8, 8, Width - 8, Height - 8);
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, plate.Top), new SKPoint(0, plate.Bottom), [top, bottom], SKShaderTileMode.Clamp))
        using (var fill = new SKPaint { Shader = shader, IsAntialias = true })
        {
            canvas.DrawRoundRect(plate, 36, 36, fill);
        }

        using (var border = new SKPaint { Color = SKColors.White.WithAlpha(70), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 })
        {
            canvas.DrawRoundRect(plate, 36, 36, border);
        }

        DrawName(canvas, name.Trim().ToUpperInvariant(), plate);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawName(SKCanvas canvas, string text, SKRect plate)
    {
        var maxWidth = plate.Width - 80;
        var lines = Wrap(text, maxWidth, 150f, out var size);
        using var font = new SKFont(Typeface.Value, size) { Embolden = true, Subpixel = true };
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(90), IsAntialias = true };

        font.GetFontMetrics(out var metrics);
        var lineHeight = (metrics.Descent - metrics.Ascent) * 0.95f;
        var blockHeight = lineHeight * lines.Length;
        var baseline = plate.MidY - (blockHeight / 2) - metrics.Ascent;
        foreach (var line in lines)
        {
            var width = font.MeasureText(line);
            var x = plate.MidX - (width / 2);
            canvas.DrawText(line, x + 4, baseline + 4, SKTextAlign.Left, font, shadow);
            canvas.DrawText(line, x, baseline, SKTextAlign.Left, font, paint);
            baseline += lineHeight;
        }
    }

    /// <summary>
    /// Fits the name on one or two lines at the largest size that fits.
    /// </summary>
    private static string[] Wrap(string text, float maxWidth, float startSize, out float size)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        using var font = new SKFont(Typeface.Value, startSize) { Embolden = true };
        for (size = startSize; size > 24; size -= 4)
        {
            font.Size = size;
            if (font.MeasureText(text) <= maxWidth && size <= 150)
            {
                return [text];
            }

            if (words.Length > 1 && size <= 110)
            {
                // Split where the two lines come out most even.
                var best = Enumerable.Range(1, words.Length - 1)
                    .Select(i => new[] { string.Join(' ', words[..i]), string.Join(' ', words[i..]) })
                    .MinBy(pair => Math.Abs(font.MeasureText(pair[0]) - font.MeasureText(pair[1])))!;
                if (best.All(line => font.MeasureText(line) <= maxWidth))
                {
                    return best;
                }
            }
        }

        return [text];
    }

    private static SKTypeface LoadTypeface()
    {
        using var stream = typeof(LogoRenderer).Assembly.GetManifestResourceStream("Jellyfin.Plugin.CableTv.Logos.Oswald.ttf");
        if (stream is null)
        {
            return SKTypeface.Default;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        return SKTypeface.FromStream(copy) ?? SKTypeface.Default;
    }
}
