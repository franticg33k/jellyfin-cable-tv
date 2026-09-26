using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// A channel's schedule as a pure function of wall-clock time.
/// </summary>
/// <remarks>
/// Time from the anchor is cut into cycles, each exactly as long as the whole pool. Cycle <c>k</c> plays every pool item once,
/// in canonical order (<see cref="ChannelSorting.Cyclic"/>) or in a shuffle seeded by the channel id and <c>k</c>
/// (<see cref="ChannelSorting.Random"/>). Nothing is stored, so a restart or a second server computes the same timeline,
/// and the guide never drifts from what plays. The timeline changes only when the channel or its pool changes, which
/// changes <see cref="Version"/>.
/// </remarks>
public sealed class ChannelTimeline
{
    /// <summary>
    /// Upper bound on slots returned by one query, as a guard against pathological pools of tiny items.
    /// </summary>
    public const int MaxSlotsPerQuery = 20_000;

    private readonly PoolItem[] _pool;
    private readonly ChannelSorting _sorting;
    private readonly long _anchorTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelTimeline"/> class.
    /// </summary>
    /// <param name="channelId">Stable channel id; seeds the shuffle.</param>
    /// <param name="sorting">Pool ordering.</param>
    /// <param name="anchorUtc">Fixed instant the timeline is computed from.</param>
    /// <param name="pool">Pool in canonical order. Items without a positive duration are dropped.</param>
    public ChannelTimeline(string channelId, ChannelSorting sorting, DateTime anchorUtc, IEnumerable<PoolItem> pool)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(pool);

        ChannelId = channelId;
        _sorting = sorting;
        _anchorTicks = DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc).Ticks;
        _pool = pool.Where(p => p.DurationTicks > 0).ToArray();
        CycleTicks = _pool.Sum(p => p.DurationTicks);

        var hash = StableHash.Add(StableHash.Start(), channelId);
        hash = StableHash.Add(hash, (long)sorting);
        hash = StableHash.Add(hash, _anchorTicks);
        foreach (var item in _pool)
        {
            hash = StableHash.Add(hash, item.ItemId);
            hash = StableHash.Add(hash, item.DurationTicks);
        }

        Version = StableHash.ToHex(hash, 12);
    }

    /// <summary>Gets the channel id.</summary>
    public string ChannelId { get; }

    /// <summary>Gets a hash of everything the timeline depends on; clients refetch when it changes.</summary>
    public string Version { get; }

    /// <summary>Gets the length of one full pass over the pool.</summary>
    public long CycleTicks { get; }

    /// <summary>Gets the number of schedulable items.</summary>
    public int PoolSize => _pool.Length;

    /// <summary>
    /// Returns the slot airing at <paramref name="atUtc"/>, or null for an empty pool.
    /// </summary>
    /// <param name="atUtc">Instant to look up.</param>
    /// <returns>The airing slot.</returns>
    public ScheduledSlot? GetSlotAt(DateTime atUtc)
        => GetSlots(atUtc, atUtc.AddTicks(1)).FirstOrDefault();

    /// <summary>
    /// Returns the slots overlapping <c>[fromUtc, toUtc)</c>, in order.
    /// </summary>
    /// <param name="fromUtc">Window start.</param>
    /// <param name="toUtc">Window end.</param>
    /// <returns>The slots.</returns>
    public IEnumerable<ScheduledSlot> GetSlots(DateTime fromUtc, DateTime toUtc)
    {
        if (_pool.Length == 0 || toUtc <= fromUtc)
        {
            yield break;
        }

        var fromTicks = fromUtc.Ticks;
        var toTicks = toUtc.Ticks;

        var cycle = FloorDiv(fromTicks - _anchorTicks, CycleTicks);
        var cycleStart = _anchorTicks + (cycle * CycleTicks);
        var order = GetOrder(cycle);
        var starts = PrefixStarts(order);

        // Last slot whose start is at or before fromTicks.
        var index = Array.BinarySearch(starts, fromTicks - cycleStart);
        if (index < 0)
        {
            index = ~index - 1;
        }

        for (var emitted = 0; emitted < MaxSlotsPerQuery; emitted++)
        {
            if (index == order.Length)
            {
                cycle++;
                cycleStart += CycleTicks;
                order = GetOrder(cycle);
                starts = PrefixStarts(order);
                index = 0;
            }

            var start = cycleStart + starts[index];
            if (start >= toTicks)
            {
                yield break;
            }

            var item = _pool[order[index]];
            var id = SlotId(cycle, index);
            yield return new ScheduledSlot(
                "s-" + id,
                SlotKind.Program,
                new DateTime(start, DateTimeKind.Utc),
                new DateTime(start + item.DurationTicks, DateTimeKind.Utc),
                item,
                0,
                item.DurationTicks,
                "g-" + id);

            index++;
        }
    }

    private static long FloorDiv(long a, long b)
    {
        var q = Math.DivRem(a, b, out var r);
        return r < 0 ? q - 1 : q;
    }

    private long[] PrefixStarts(int[] order)
    {
        var starts = new long[order.Length];
        long acc = 0;
        for (var i = 0; i < order.Length; i++)
        {
            starts[i] = acc;
            acc += _pool[order[i]].DurationTicks;
        }

        return starts;
    }

    private string SlotId(long cycle, int index)
    {
        var hash = StableHash.Add(StableHash.Start(), ChannelId);
        hash = StableHash.Add(hash, Version);
        hash = StableHash.Add(hash, cycle);
        hash = StableHash.Add(hash, index);
        return StableHash.ToHex(hash, 12);
    }

    private int[] GetOrder(long cycle)
    {
        if (_sorting == ChannelSorting.Cyclic)
        {
            return Enumerable.Range(0, _pool.Length).ToArray();
        }

        var order = BaseShuffle(cycle);

        // Avoid airing the same item twice in a row across a cycle boundary. The swap only touches positions 0 and 1,
        // so for pools larger than two the previous cycle's last item is the same in its base and adjusted shuffles.
        if (order.Length > 2 && order[0] == BaseShuffle(cycle - 1)[^1])
        {
            (order[0], order[1]) = (order[1], order[0]);
        }

        return order;
    }

    private int[] BaseShuffle(long cycle)
    {
        var order = Enumerable.Range(0, _pool.Length).ToArray();
        var rng = new SplitMix64(StableHash.Add(StableHash.Add(StableHash.Start(), ChannelId), cycle));
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = rng.NextInt(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }
}
