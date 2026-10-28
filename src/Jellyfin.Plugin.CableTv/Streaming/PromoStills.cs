using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Library;
using Jellyfin.Plugin.CableTv.Scheduling;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Streaming;

/// <summary>
/// Makes the promo cards that air during breaks and gaps, and keeps them in the plugin's data folder.
/// </summary>
public sealed class PromoStills
{
    private static readonly TimeSpan MinFiller = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(2);

    private readonly ILogger<PromoStills> _logger;
    private bool _renderFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromoStills"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public PromoStills(ILogger<PromoStills> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The card for a filler slot.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="filler">The filler slot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Path of a PNG, or null when the slot airs as plain filler (too short, nothing to announce, cards off).</returns>
    public async Task<string?> StillAsync(ChannelSnapshot channel, ScheduledSlot filler, CancellationToken cancellationToken)
    {
        if (_renderFailed || Plugin.Instance is not { } plugin || !plugin.Configuration.PromoCards || filler.EndUtc - filler.StartUtc < MinFiller)
        {
            return null;
        }

        var info = PromoPlanner.Pick(channel.Timeline, filler, ResolveZone(plugin.Configuration.TimeZone));
        if (info is null)
        {
            return null;
        }

        var directory = Path.Combine(plugin.DataFolderPath, "promos");
        var hash = StableHash.ToHex(StableHash.Add(StableHash.Add(StableHash.Start(), info.Key + "|" + channel.Definition.Number + "|" + channel.Definition.Name), PromoCardRenderer.StyleVersion), 10);
        var path = Path.Combine(directory, TitleNormalizer.Slug(channel.Definition.Id) + "-" + hash + ".png");
        if (File.Exists(path))
        {
            return path;
        }

        try
        {
            Directory.CreateDirectory(directory);
            Prune(directory);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, PromoCardRenderer.RenderPng(info, channel.Definition.Number, channel.Definition.Name), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is TypeLoadException or DllNotFoundException or TypeInitializationException)
        {
            _renderFailed = true;
            _logger.LogWarning(ex, "Couldn't draw promo cards; breaks air as plain filler");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't save a promo card");
            return null;
        }
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(directory, "*.png").Where(f => DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > KeepFor))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old cards are only clutter.
        }
    }
}
