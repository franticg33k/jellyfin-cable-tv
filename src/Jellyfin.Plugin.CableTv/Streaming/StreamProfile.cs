using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;

namespace Jellyfin.Plugin.CableTv.Streaming;

/// <summary>
/// The format a channel's continuous stream is held to.
/// </summary>
/// <param name="Mode">Copy, transcode or auto.</param>
/// <param name="VideoCodec">Output video codec: "h264" or "hevc".</param>
/// <param name="Width">Output width for transcoded items and filler.</param>
/// <param name="Height">Output height for transcoded items and filler.</param>
/// <param name="NormalizeLoudness">Whether audio goes through loudnorm.</param>
public sealed record StreamProfile(FallbackStreamMode Mode, string VideoCodec, int Width, int Height, bool NormalizeLoudness)
{
    /// <summary>Codecs the stream can encode to.</summary>
    private static readonly string[] EncodableCodecs = ["h264", "hevc"];

    /// <summary>
    /// Picks the profile for a pool: in copy and auto modes the codec and resolution most of the airtime already uses,
    /// so most items can be copied; in transcode mode H.264 at the configured height.
    /// </summary>
    /// <param name="pool">Programme pool.</param>
    /// <param name="mode">Stream mode.</param>
    /// <param name="transcodeHeight">Frame height for transcode mode.</param>
    /// <param name="normalizeLoudness">Whether to level loudness.</param>
    /// <returns>The profile.</returns>
    public static StreamProfile For(IEnumerable<PoolItem> pool, FallbackStreamMode mode, int transcodeHeight, bool normalizeLoudness)
    {
        ArgumentNullException.ThrowIfNull(pool);

        if (mode == FallbackStreamMode.Transcode)
        {
            var height = Even(Math.Clamp(transcodeHeight, 240, 2160));
            return new StreamProfile(mode, "h264", Even(height * 16 / 9), height, normalizeLoudness);
        }

        var items = pool.ToList();
        var codec = items
            .Where(i => i.VideoCodec is not null)
            .GroupBy(i => i.VideoCodec!.ToLowerInvariant())
            .OrderByDescending(g => g.Sum(i => i.DurationTicks))
            .Select(g => g.Key)
            .FirstOrDefault();
        if (codec is null || !EncodableCodecs.Contains(codec))
        {
            codec = "h264";
        }

        var size = items
            .Where(i => i.Width > 0 && i.Height > 0 && string.Equals(i.VideoCodec, codec, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => (Width: i.Width!.Value, Height: i.Height!.Value))
            .OrderByDescending(g => g.Sum(i => i.DurationTicks))
            .Select(g => g.Key)
            .FirstOrDefault();

        return size == default
            ? new StreamProfile(mode, codec, 1280, 720, normalizeLoudness)
            : new StreamProfile(mode, codec, Even(size.Width), Even(size.Height), normalizeLoudness);
    }

    /// <summary>
    /// Whether an item's video can be copied into the stream as is.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True to copy, false to transcode.</returns>
    public bool CanCopy(PoolItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Mode switch
        {
            FallbackStreamMode.Copy => true,
            FallbackStreamMode.Transcode => false,
            _ => string.Equals(item.VideoCodec, VideoCodec, StringComparison.OrdinalIgnoreCase)
                 && item.Width == Width
                 && item.Height == Height,
        };
    }

    private static int Even(int value) => value - (value % 2);
}
