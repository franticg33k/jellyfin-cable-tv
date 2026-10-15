using System;
using System.Collections.Generic;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Logos;

/// <summary>
/// The pixel work behind channel logo options, on plain RGBA byte arrays so it needs no imaging library.
/// </summary>
public static class LogoImage
{
    /// <summary>
    /// Makes a logo's background transparent. The background colour is read from the image's corners and removed outward
    /// from the edges, so the same colour inside the logo (a white letter, say) stays. Colours between the tolerance
    /// and half as much again fade out, which keeps edges smooth.
    /// </summary>
    /// <param name="rgba">Straight (not premultiplied) RGBA pixels, row by row. Changed in place.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="tolerancePercent">0–100: how far from the background colour still counts as background.</param>
    /// <returns>True when a background was found and removed.</returns>
    public static bool RemoveBackground(byte[] rgba, int width, int height, int tolerancePercent)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width < 2 || height < 2 || rgba.Length < width * height * 4)
        {
            return false;
        }

        var tolerance = Math.Clamp(tolerancePercent, 1, 100) / 100.0 * MaxDistance;
        var soft = tolerance * 1.5;

        // The background is the colour of the corners; if they disagree or are already see-through, there is none to remove.
        var corners = new[] { 0, width - 1, (height - 1) * width, (height * width) - 1 };
        double r = 0, g = 0, b = 0;
        foreach (var corner in corners)
        {
            var i = corner * 4;
            if (rgba[i + 3] < 200)
            {
                return false;
            }

            r += rgba[i];
            g += rgba[i + 1];
            b += rgba[i + 2];
        }

        r /= 4;
        g /= 4;
        b /= 4;
        foreach (var corner in corners)
        {
            if (Distance(rgba, corner * 4, r, g, b) > tolerance)
            {
                return false;
            }
        }

        var seen = new bool[width * height];
        var queue = new Queue<int>();
        void Push(int pixel)
        {
            if (!seen[pixel])
            {
                seen[pixel] = true;
                queue.Enqueue(pixel);
            }
        }

        for (var x = 0; x < width; x++)
        {
            Push(x);
            Push(((height - 1) * width) + x);
        }

        for (var y = 0; y < height; y++)
        {
            Push(y * width);
            Push((y * width) + width - 1);
        }

        var removed = false;
        while (queue.Count > 0)
        {
            var pixel = queue.Dequeue();
            var offset = pixel * 4;
            var distance = Distance(rgba, offset, r, g, b);
            if (distance > soft)
            {
                continue;
            }

            if (distance <= tolerance)
            {
                rgba[offset + 3] = 0;
                removed = true;
            }
            else
            {
                // An edge pixel: partly background, so partly see-through.
                var keep = (distance - tolerance) / (soft - tolerance);
                rgba[offset + 3] = (byte)Math.Min(rgba[offset + 3], (int)(keep * 255));
                continue;
            }

            var x = pixel % width;
            var y = pixel / width;
            if (x > 0)
            {
                Push(pixel - 1);
            }

            if (x < width - 1)
            {
                Push(pixel + 1);
            }

            if (y > 0)
            {
                Push(pixel - width);
            }

            if (y < height - 1)
            {
                Push(pixel + width);
            }
        }

        return removed;
    }

    /// <summary>
    /// The smallest rectangle containing every visible pixel, so the logo can be placed without its empty margin.
    /// </summary>
    /// <param name="rgba">Straight RGBA pixels.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="minAlpha">Alpha below which a pixel counts as empty.</param>
    /// <returns>The rectangle, or null when nothing is visible.</returns>
    public static (int X, int Y, int Width, int Height)? VisibleBounds(byte[] rgba, int width, int height, int minAlpha = 16)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (rgba[(((y * width) + x) * 4) + 3] < minAlpha)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < left ? null : (left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// Where a logo of a given size goes in a canvas: as large as fits within <paramref name="scalePercent"/> of the
    /// canvas, keeping its proportions, placed by <paramref name="align"/>.
    /// </summary>
    /// <param name="sourceWidth">Logo width.</param>
    /// <param name="sourceHeight">Logo height.</param>
    /// <param name="canvasWidth">Canvas width.</param>
    /// <param name="canvasHeight">Canvas height.</param>
    /// <param name="scalePercent">10–100.</param>
    /// <param name="align">Placement within the canvas.</param>
    /// <returns>The destination rectangle.</returns>
    public static (float X, float Y, float Width, float Height) Place(
        int sourceWidth,
        int sourceHeight,
        int canvasWidth,
        int canvasHeight,
        int scalePercent,
        LogoAlign align)
    {
        var scale = Math.Clamp(scalePercent, 10, 100) / 100f;
        var boxWidth = canvasWidth * scale;
        var boxHeight = canvasHeight * scale;
        var fit = Math.Min(boxWidth / sourceWidth, boxHeight / sourceHeight);
        var width = sourceWidth * fit;
        var height = sourceHeight * fit;

        var x = align switch
        {
            LogoAlign.Left or LogoAlign.TopLeft or LogoAlign.BottomLeft => 0f,
            LogoAlign.Right or LogoAlign.TopRight or LogoAlign.BottomRight => canvasWidth - width,
            _ => (canvasWidth - width) / 2f,
        };
        var y = align switch
        {
            LogoAlign.Top or LogoAlign.TopLeft or LogoAlign.TopRight => 0f,
            LogoAlign.Bottom or LogoAlign.BottomLeft or LogoAlign.BottomRight => canvasHeight - height,
            _ => (canvasHeight - height) / 2f,
        };
        return (x, y, width, height);
    }

    private static double MaxDistance => Math.Sqrt(3 * 255d * 255d);

    private static double Distance(byte[] rgba, int offset, double r, double g, double b)
    {
        var dr = rgba[offset] - r;
        var dg = rgba[offset + 1] - g;
        var db = rgba[offset + 2] - b;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }
}
