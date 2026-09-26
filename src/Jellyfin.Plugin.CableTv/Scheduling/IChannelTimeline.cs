using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// A channel's schedule as a pure function of wall-clock time.
/// </summary>
public interface IChannelTimeline
{
    /// <summary>Gets the channel id.</summary>
    string ChannelId { get; }

    /// <summary>Gets a hash of everything the timeline depends on; clients refetch when it changes.</summary>
    string Version { get; }

    /// <summary>Gets the length of one full pass over the main pool.</summary>
    long CycleTicks { get; }

    /// <summary>Gets the number of blocks per cycle of the main pool (weighted copies included).</summary>
    int PoolSize { get; }

    /// <summary>
    /// Returns the blocks overlapping <c>[fromUtc, toUtc)</c>, in order.
    /// </summary>
    /// <param name="fromUtc">Window start.</param>
    /// <param name="toUtc">Window end.</param>
    /// <returns>The blocks.</returns>
    IEnumerable<ScheduledBlock> GetBlocks(DateTime fromUtc, DateTime toUtc);

    /// <summary>
    /// Returns the slots overlapping <c>[fromUtc, toUtc)</c>, in order and contiguous.
    /// </summary>
    /// <param name="fromUtc">Window start.</param>
    /// <param name="toUtc">Window end.</param>
    /// <returns>The slots.</returns>
    IEnumerable<ScheduledSlot> GetSlots(DateTime fromUtc, DateTime toUtc);

    /// <summary>
    /// Returns the slot airing at <paramref name="atUtc"/>, or null when nothing airs.
    /// </summary>
    /// <param name="atUtc">Instant to look up.</param>
    /// <returns>The airing slot.</returns>
    ScheduledSlot? GetSlotAt(DateTime atUtc);
}
