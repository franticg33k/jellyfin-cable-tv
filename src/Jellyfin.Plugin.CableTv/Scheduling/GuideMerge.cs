using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// Folds runs of short music or trailer blocks into half-hour guide entries ("Rock Radio: Queen, ABBA, …"), so a
/// guide shows programmes rather than a sliver per song.
/// </summary>
public static class GuideMerge
{
    private static readonly long Bucket = TimeSpan.FromMinutes(30).Ticks;

    /// <summary>
    /// Merges music and trailer blocks that share a half hour; other blocks pass through.
    /// </summary>
    /// <param name="blocks">Blocks in order.</param>
    /// <param name="channelName">Channel name, the title of merged music.</param>
    /// <returns>Guide blocks.</returns>
    public static IEnumerable<ScheduledBlock> Merge(IEnumerable<ScheduledBlock> blocks, string channelName)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var run = new List<ScheduledBlock>();
        long runBucket = long.MinValue;

        foreach (var block in blocks)
        {
            var mergeable = block.Item is { IsAudio: true } or { IsTrailer: true };
            var bucket = block.StartUtc.Ticks / Bucket;
            if (run.Count > 0 && (!mergeable || bucket != runBucket || Kind(block) != Kind(run[0])))
            {
                yield return Fold(run, channelName);
                run.Clear();
            }

            if (mergeable)
            {
                run.Add(block);
                runBucket = bucket;
            }
            else
            {
                yield return block;
            }
        }

        if (run.Count > 0)
        {
            yield return Fold(run, channelName);
        }
    }

    private static bool Kind(ScheduledBlock block) => block.Item?.IsAudio == true;

    private static ScheduledBlock Fold(List<ScheduledBlock> run, string channelName)
    {
        if (run.Count == 1)
        {
            return run[0];
        }

        var first = run[0];
        var audio = first.Item!.IsAudio;
        var names = run
            .Select(b => audio ? b.Item!.Artist : b.Item!.Title)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var subtitle = string.Join(", ", names.Take(3)) + (names.Count > 3 ? ", …" : string.Empty);
        var item = new PoolItem(Guid.Empty, string.Empty, run[^1].EndUtc.Ticks - first.StartUtc.Ticks, audio ? channelName : "Coming Attractions")
        {
            IsAudio = audio,
            IsTrailer = !audio,
            EpisodeTitle = subtitle.Length > 0 ? subtitle : null,
        };
        return first with { Item = item, EndUtc = run[^1].EndUtc, Slots = run.SelectMany(b => b.Slots).ToList() };
    }
}
