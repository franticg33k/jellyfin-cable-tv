using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// Slot queries shared by every timeline, derived from its blocks.
/// </summary>
internal static class TimelineSlots
{
    public static ScheduledSlot? At(IChannelTimeline timeline, DateTime atUtc)
        => Between(timeline, atUtc, atUtc.AddTicks(1)).FirstOrDefault();

    public static IEnumerable<ScheduledSlot> Between(IChannelTimeline timeline, DateTime fromUtc, DateTime toUtc)
    {
        var emitted = 0;
        foreach (var block in timeline.GetBlocks(fromUtc, toUtc))
        {
            foreach (var slot in block.Slots)
            {
                if (slot.EndUtc <= fromUtc)
                {
                    continue;
                }

                if (slot.StartUtc >= toUtc || emitted >= ChannelTimeline.MaxSlotsPerQuery)
                {
                    yield break;
                }

                emitted++;
                yield return slot;
            }
        }
    }
}
