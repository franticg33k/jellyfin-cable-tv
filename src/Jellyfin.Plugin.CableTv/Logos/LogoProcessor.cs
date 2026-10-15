using System;
using System.IO;
using Jellyfin.Plugin.CableTv.Configuration;
using SkiaSharp;

namespace Jellyfin.Plugin.CableTv.Logos;

/// <summary>
/// Turns a source image into a channel logo with the channel's options applied: plain background made transparent,
/// the empty margin trimmed, then scaled and placed on a transparent canvas the size of a generated logo. Uses the
/// SkiaSharp library Jellyfin already ships; the pixel work itself is in <see cref="LogoImage"/>.
/// </summary>
public static class LogoProcessor
{
    /// <summary>Largest source image accepted, in pixels.</summary>
    private const int MaxPixels = 6_000_000;

    /// <summary>
    /// Applies logo options to an image.
    /// </summary>
    /// <param name="source">The image file's bytes (PNG, JPEG, WebP, GIF…).</param>
    /// <param name="transparent">Whether to make the plain background transparent.</param>
    /// <param name="tolerance">Background tolerance, 0–100.</param>
    /// <param name="scale">How much of the canvas the logo fills, in percent.</param>
    /// <param name="align">Where the logo sits.</param>
    /// <returns>PNG bytes.</returns>
    /// <exception cref="InvalidDataException">The bytes aren't an image, or it is too large.</exception>
    public static byte[] Process(byte[] source, bool transparent, int tolerance, int scale, LogoAlign align)
    {
        ArgumentNullException.ThrowIfNull(source);
        var probe = SKBitmap.DecodeBounds(source);
        if (probe.IsEmpty || (long)probe.Width * probe.Height > MaxPixels)
        {
            throw new InvalidDataException("The logo isn't an image, or it is too large.");
        }

        var info = new SKImageInfo(probe.Width, probe.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var decoded = SKBitmap.Decode(source, info) ?? throw new InvalidDataException("The logo image couldn't be read.");
        var width = decoded.Width;
        var height = decoded.Height;
        var rgba = decoded.GetPixelSpan().ToArray();

        if (transparent)
        {
            LogoImage.RemoveBackground(rgba, width, height, tolerance);
        }

        var bounds = LogoImage.VisibleBounds(rgba, width, height) ?? (0, 0, width, height);
        using var trimmed = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var target = trimmed.GetPixelSpan();
        for (var y = 0; y < bounds.Height; y++)
        {
            new ReadOnlySpan<byte>(rgba, (((bounds.Y + y) * width) + bounds.X) * 4, bounds.Width * 4)
                .CopyTo(target.Slice(y * bounds.Width * 4, bounds.Width * 4));
        }

        var place = LogoImage.Place(bounds.Width, bounds.Height, LogoRenderer.Width, LogoRenderer.Height, scale, align);
        using var surface = SKSurface.Create(new SKImageInfo(LogoRenderer.Width, LogoRenderer.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(trimmed);
        surface.Canvas.DrawImage(image, SKRect.Create(place.X, place.Y, place.Width, place.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
