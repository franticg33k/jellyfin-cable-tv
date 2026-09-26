using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Scheduling;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Streaming;

/// <summary>
/// Commercials converted ahead of time to each channel's stream format, so the continuous stream copies them instead
/// of transcoding every airing (in Auto mode), or doesn't switch resolution mid-stream (in Copy mode).
/// </summary>
public class CommercialCache
{
    private readonly ChannelStore _store;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<CommercialCache> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommercialCache"/> class.
    /// </summary>
    /// <param name="store">Channel store.</param>
    /// <param name="mediaEncoder">Media encoder, for the ffmpeg path.</param>
    /// <param name="logger">Logger.</param>
    public CommercialCache(ChannelStore store, IMediaEncoder mediaEncoder, ILogger<CommercialCache> logger)
    {
        _store = store;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    private static string? Directory => Plugin.Instance is { } plugin ? Path.Combine(plugin.DataFolderPath, "commercials") : null;

    /// <summary>
    /// The item as the stream should air it: the converted copy when there is one for this format, else the item.
    /// </summary>
    /// <param name="item">Commercial.</param>
    /// <param name="profile">Channel's stream format.</param>
    /// <returns>The item to air.</returns>
    public static PoolItem ForStream(PoolItem item, StreamProfile profile) => ForStream(item, profile, Directory);

    /// <summary>
    /// The item as the stream should air it, with converted copies kept in <paramref name="directory"/>.
    /// </summary>
    /// <param name="item">Commercial.</param>
    /// <param name="profile">Channel's stream format.</param>
    /// <param name="directory">Folder of converted copies.</param>
    /// <returns>The item to air.</returns>
    public static PoolItem ForStream(PoolItem item, StreamProfile profile, string? directory)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Mode == FallbackStreamMode.Transcode || profile.CanCopy(item) && profile.Mode != FallbackStreamMode.Copy || PathFor(item, profile, directory) is not { } path || !File.Exists(path))
        {
            return item;
        }

        return item with { Path = path, VideoCodec = profile.VideoCodec, Width = profile.Width, Height = profile.Height, HasAudio = true };
    }

    /// <summary>
    /// Converts every commercial some channel would otherwise transcode or air in the wrong format.
    /// </summary>
    /// <param name="progress">Progress, 0-100.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task PrepareAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var work = _store.Channels
            .Where(c => c.Stream.Mode != FallbackStreamMode.Transcode)
            .SelectMany(c => c.Commercials.Select(ad => (Ad: ad, c.Stream)))
            .Where(w => w.Ad.Path is not null && (w.Stream.Mode == FallbackStreamMode.Copy || !w.Stream.CanCopy(w.Ad)))
            .DistinctBy(w => PathFor(w.Ad, w.Stream))
            .ToList();
        var keep = new HashSet<string>(work.Select(w => PathFor(w.Ad, w.Stream)!), StringComparer.Ordinal);
        RemoveStale(keep);

        var done = 0;
        foreach (var (ad, profile) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathFor(ad, profile)!;
            if (!File.Exists(path))
            {
                await ConvertAsync(ad, profile, path, cancellationToken).ConfigureAwait(false);
            }

            progress.Report(100.0 * ++done / work.Count);
        }

        _logger.LogInformation("Cable TV commercials ready: {Count} converted copies", work.Count);
    }

    private static string? PathFor(PoolItem item, StreamProfile profile) => PathFor(item, profile, Directory);

    private static string? PathFor(PoolItem item, StreamProfile profile, string? directory)
        => directory is { } dir
            ? Path.Combine(dir, FormattableString.Invariant($"{item.ItemId:N}-{item.DurationTicks}-{profile.VideoCodec}-{profile.Width}x{profile.Height}{(profile.NormalizeLoudness ? "-n" : string.Empty)}.ts"))
            : null;

    private void RemoveStale(HashSet<string> keep)
    {
        if (Directory is not { } dir || !System.IO.Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in System.IO.Directory.GetFiles(dir, "*.ts").Where(f => !keep.Contains(f)))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Couldn't remove {File}", file);
            }
        }
    }

    private async Task ConvertAsync(PoolItem ad, StreamProfile profile, string path, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".part";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", ad.Path! };
        if (!ad.HasAudio)
        {
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-shortest"]);
        }

        args.AddRange(["-map", "0:v:0", "-map", ad.HasAudio ? "0:a:0" : "1:a:0", "-sn", "-dn"]);
        args.AddRange(["-vf", FormattableString.Invariant(
            $"scale={profile.Width}:{profile.Height}:force_original_aspect_ratio=decrease,pad={profile.Width}:{profile.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p")]);
        args.AddRange(profile.VideoCodec == "hevc"
            ? ["-c:v", "libx265", "-preset", "medium", "-x265-params", "log-level=error"]
            : ["-c:v", "libx264", "-preset", "medium", "-crf", "20"]);
        args.AddRange(["-g", "50", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
        if (profile.NormalizeLoudness)
        {
            args.AddRange(["-af", "loudnorm=I=-16:TP=-1.5:LRA=11"]);
        }

        args.AddRange(["-f", "mpegts", temp]);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            File.Delete(temp);
            throw;
        }

        if (process.ExitCode == 0 && File.Exists(temp))
        {
            File.Move(temp, path, overwrite: true);
            return;
        }

        _logger.LogWarning("Couldn't convert commercial {Title}: {Errors}", ad.Title, (await errors.ConfigureAwait(false)).Trim());
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }
    }
}
