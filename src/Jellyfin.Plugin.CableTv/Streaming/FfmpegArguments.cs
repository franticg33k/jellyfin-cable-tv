using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.CableTv.Scheduling;

namespace Jellyfin.Plugin.CableTv.Streaming;

/// <summary>
/// Builds the ffmpeg command for one piece of a channel's continuous MPEG-TS stream.
/// </summary>
/// <remarks>
/// <para>
/// Each slot is its own ffmpeg run, written back to back into one stream. Every run re-encodes audio to AAC 48 kHz
/// stereo (a mid-stream audio codec change silences some players) and paces itself at real time.
/// </para>
/// <para>
/// Timestamps: a run keeps the item's own timestamps (<c>-copyts -start_at_zero</c>) shifted so the point it starts
/// playing from lands on the stream position. When video is copied from a seek, ffmpeg starts at the keyframe before
/// that point; those extra frames then fall just before the position instead of pushing the run's end into the next
/// run. <see cref="TimestampBase"/> keeps them from going negative at the start of the stream.
/// </para>
/// </remarks>
public static class FfmpegArguments
{
    /// <summary>Added to every timestamp; longer than any sane keyframe interval.</summary>
    public static readonly TimeSpan TimestampBase = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Builds arguments for a slot.
    /// </summary>
    /// <param name="slot">The slot; filler when <see cref="ScheduledSlot.Item"/> is null.</param>
    /// <param name="startInItem">Position inside the item to start from.</param>
    /// <param name="duration">How much to play.</param>
    /// <param name="streamPosition">Time since the stream started; where <paramref name="startInItem"/> lands on the stream's clock.</param>
    /// <param name="profile">Stream format.</param>
    /// <param name="initialBurst">Seconds to send faster than real time, so a new viewer buffers quickly.</param>
    /// <returns>The argument list.</returns>
    public static IReadOnlyList<string> Build(
        ScheduledSlot slot,
        TimeSpan startInItem,
        TimeSpan duration,
        TimeSpan streamPosition,
        StreamProfile profile,
        double initialBurst = 0)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(profile);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-copyts", "-start_at_zero" };
        var item = slot.Item;
        var pace = new List<string> { "-readrate", "1" };
        if (initialBurst > 0)
        {
            pace.AddRange(["-readrate_initial_burst", Seconds(initialBurst)]);
        }

        // Durations go on the inputs: with -copyts an output -t would count from the item's own timestamps, not from
        // the seek point.
        var length = Seconds(duration.TotalSeconds);
        bool copy;
        string audioMap;
        var videoMap = "0:v:0";
        if (item is { IsAudio: true, Path: not null })
        {
            // Music has no picture: its cover art (or a plain background) becomes the video.
            copy = false;
            args.AddRange(pace);
            if (startInItem > TimeSpan.Zero)
            {
                args.AddRange(["-ss", Seconds(startInItem.TotalSeconds)]);
            }

            args.AddRange(["-t", length, "-i", item.Path]);
            if (startInItem > TimeSpan.Zero)
            {
                args.AddRange(["-itsoffset", Seconds(startInItem.TotalSeconds)]);
            }

            if (!string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
            {
                args.AddRange(["-loop", "1", "-framerate", "25", "-t", length, "-i", item.ImagePath]);
            }
            else
            {
                args.AddRange(["-t", length, "-f", "lavfi", "-i", FormattableString.Invariant($"color=c=0x101830:s={profile.Width}x{profile.Height}:r=25")]);
            }

            audioMap = "0:a:0";
            videoMap = "1:v:0";
        }
        else if (item?.Path is null)
        {
            copy = false;
            args.AddRange(pace);
            args.AddRange(["-t", length, "-f", "lavfi", "-i", FormattableString.Invariant($"color=c=black:s={profile.Width}x{profile.Height}:r=25")]);
            args.AddRange(["-t", length, "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
            audioMap = "1:a:0";
        }
        else
        {
            copy = profile.CanCopy(item);
            args.AddRange(pace);
            if (startInItem > TimeSpan.Zero)
            {
                if (copy)
                {
                    // Copied video starts at the keyframe before the seek point. Start the re-encoded audio there
                    // too: otherwise the stream opens with seconds of video and no audio, and Jellyfin's probe
                    // reports an audio track with no sample rate, which breaks remuxing to HLS (live.m3u8).
                    args.Add("-noaccurate_seek");
                }

                args.AddRange(["-ss", Seconds(startInItem.TotalSeconds)]);
            }

            args.AddRange(["-t", length, "-i", item.Path]);
            if (item.HasAudio)
            {
                audioMap = "0:a:0";
            }
            else
            {
                // Generated silence starts at zero; line it up with the seek point.
                if (startInItem > TimeSpan.Zero)
                {
                    args.AddRange(["-itsoffset", Seconds(startInItem.TotalSeconds)]);
                }

                args.AddRange(["-t", length, "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
                audioMap = "1:a:0";
            }
        }

        args.AddRange(["-map", videoMap, "-map", audioMap, "-sn", "-dn"]);

        if (copy)
        {
            args.AddRange(["-c:v", "copy"]);
        }
        else
        {
            args.AddRange(["-vf", FormattableString.Invariant(
                $"scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=decrease,pad={profile.Width}:{profile.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p")]);
            if (profile.VideoCodec == "hevc")
            {
                args.AddRange(["-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error"]);
            }
            else
            {
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "21"]);
            }

            args.AddRange(["-g", "50"]);
        }

        args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
        if (profile.NormalizeLoudness && item is not null)
        {
            args.AddRange(["-af", "loudnorm=I=-16:TP=-1.5:LRA=11"]);
        }

        var startsAt = item?.Path is null ? TimeSpan.Zero : startInItem;
        args.AddRange(["-output_ts_offset", Seconds((TimestampBase + streamPosition - startsAt).TotalSeconds)]);
        args.AddRange(["-f", "mpegts", "pipe:1"]);
        return args;
    }

    /// <summary>
    /// Builds arguments that air a still image (a weather card) with silence.
    /// </summary>
    /// <param name="imagePath">Image file.</param>
    /// <param name="duration">How long to air it.</param>
    /// <param name="streamPosition">Time since the stream started.</param>
    /// <param name="profile">Stream format.</param>
    /// <param name="initialBurst">Seconds to send faster than real time for a new viewer.</param>
    /// <returns>The argument list.</returns>
    public static IReadOnlyList<string> BuildStill(string imagePath, TimeSpan duration, TimeSpan streamPosition, StreamProfile profile, double initialBurst = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var length = Seconds(duration.TotalSeconds);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-readrate", "1" };
        if (initialBurst > 0)
        {
            args.AddRange(["-readrate_initial_burst", Seconds(initialBurst)]);
        }

        args.AddRange(["-loop", "1", "-framerate", "25", "-t", length, "-i", imagePath]);
        args.AddRange(["-t", length, "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        args.AddRange(["-map", "0:v:0", "-map", "1:a:0"]);
        args.AddRange(["-vf", FormattableString.Invariant(
            $"scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=decrease,pad={profile.Width}:{profile.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p")]);
        args.AddRange(profile.VideoCodec == "hevc"
            ? ["-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error"]
            : ["-c:v", "libx264", "-preset", "veryfast", "-tune", "stillimage", "-crf", "23"]);
        args.AddRange(["-g", "50", "-c:a", "aac", "-b:a", "96k", "-ar", "48000", "-ac", "2"]);
        args.AddRange(["-output_ts_offset", Seconds((TimestampBase + streamPosition).TotalSeconds)]);
        args.AddRange(["-f", "mpegts", "pipe:1"]);
        return args;
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
