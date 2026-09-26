using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// A channel's schedule as a pure function of wall-clock time.
/// </summary>
/// <remarks>
/// <para>
/// Time from the anchor is cut into cycles, each exactly as long as one pass over the pool. Cycle <c>k</c> airs every pool
/// item once, in an order that depends only on the sorting mode, the channel id and <c>k</c>. Nothing is stored, so a
/// restart or a second server computes the same timeline, and the guide never drifts from what plays.
/// </para>
/// <para>
/// Each pool item airs as a <em>block</em>: the programme, optionally split by a mid-break, followed by an end break, the
/// whole padded to the grid. A block's length depends only on its item, so cycle arithmetic stays O(1); which
/// commercials fill a break is chosen by a generator seeded with the block's id, so it is deterministic too.
/// </para>
/// </remarks>
public sealed class ChannelTimeline : IChannelTimeline
{
    /// <summary>
    /// Upper bound on slots returned by one query, as a guard against pathological pools of tiny items.
    /// </summary>
    public const int MaxSlotsPerQuery = 20_000;

    private const int MaxCommercialsPerBreak = 60;
    private static readonly long MinSplitProgramTicks = TimeSpan.FromMinutes(5).Ticks;

    private readonly PoolItem[] _pool;
    private readonly bool _hasCopies;
    private readonly long[] _blockTicks;
    private readonly PoolItem[] _commercials;
    private readonly TimelineOptions _options;
    private readonly long _anchorTicks;
    private CachedOrder? _lastOrder;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelTimeline"/> class with no breaks.
    /// </summary>
    /// <param name="channelId">Stable channel id; seeds every shuffle.</param>
    /// <param name="sorting">Pool ordering.</param>
    /// <param name="anchorUtc">Fixed instant the timeline is computed from.</param>
    /// <param name="pool">Pool in canonical order.</param>
    public ChannelTimeline(string channelId, ChannelSorting sorting, DateTime anchorUtc, IEnumerable<PoolItem> pool)
        : this(channelId, anchorUtc, pool, new TimelineOptions { Sorting = sorting })
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelTimeline"/> class.
    /// </summary>
    /// <param name="channelId">Stable channel id; seeds every shuffle.</param>
    /// <param name="anchorUtc">Fixed instant the timeline is computed from.</param>
    /// <param name="pool">Pool in canonical order. Items without a positive duration are dropped.</param>
    /// <param name="options">Sorting and break settings.</param>
    public ChannelTimeline(string channelId, DateTime anchorUtc, IEnumerable<PoolItem> pool, TimelineOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(options);

        ChannelId = channelId;
        _options = options;
        _anchorTicks = DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc).Ticks;
        _commercials = options.Commercials.Where(c => c.DurationTicks > 0).ToArray();

        var items = pool.Where(p => p.DurationTicks > 0);
        if (options.Sorting == ChannelSorting.Random)
        {
            // Weighted items air several times per cycle; the shuffle spreads the copies out.
            items = items.SelectMany(p => Enumerable.Repeat(p, Math.Clamp(p.Weight, 1, 10)));
        }

        _pool = items.ToArray();
        _hasCopies = _pool.Select(p => p.ItemId).Distinct().Count() < _pool.Length;
        _blockTicks = _pool.Select(BlockTicks).ToArray();
        CycleTicks = _blockTicks.Sum();
        Version = ComputeVersion();
    }

    /// <summary>Gets the channel id.</summary>
    public string ChannelId { get; }

    /// <summary>Gets a hash of everything the timeline depends on; clients refetch when it changes.</summary>
    public string Version { get; }

    /// <summary>Gets the length of one full pass over the pool.</summary>
    public long CycleTicks { get; }

    /// <summary>Gets the number of blocks per cycle (weighted copies included).</summary>
    public int PoolSize => _pool.Length;

    /// <inheritdoc />
    public ScheduledSlot? GetSlotAt(DateTime atUtc) => TimelineSlots.At(this, atUtc);

    /// <inheritdoc />
    public IEnumerable<ScheduledSlot> GetSlots(DateTime fromUtc, DateTime toUtc) => TimelineSlots.Between(this, fromUtc, toUtc);

    /// <summary>
    /// Returns the blocks overlapping <c>[fromUtc, toUtc)</c>, in order.
    /// </summary>
    /// <param name="fromUtc">Window start.</param>
    /// <param name="toUtc">Window end.</param>
    /// <returns>The blocks.</returns>
    public IEnumerable<ScheduledBlock> GetBlocks(DateTime fromUtc, DateTime toUtc)
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

        // Last block whose start is at or before fromTicks.
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

            yield return BuildBlock(_pool[order[index]], _blockTicks[order[index]], BlockHash(cycle, index), start);
            index++;
        }
    }

    private static long FloorDiv(long a, long b)
    {
        var q = Math.DivRem(a, b, out var r);
        return r < 0 ? q - 1 : q;
    }

    private static long CeilDiv(long a, long b) => (a + b - 1) / b;

    private static long RoundToSecond(long ticks) => ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;

    private long BlockTicks(PoolItem item)
    {
        var program = item.DurationTicks;
        var grid = _options.Grid.Ticks;
        if (grid > 0)
        {
            return CeilDiv(program, grid) * grid;
        }

        if (_options.BreakLength > TimeSpan.Zero && _commercials.Length > 0)
        {
            var breaks = SplitPoint(item) is null ? 1 : 2;
            return program + (breaks * _options.BreakLength.Ticks);
        }

        return program;
    }

    /// <summary>
    /// Where the programme is split for a mid-break, or null for no mid-break.
    /// </summary>
    private long? SplitPoint(PoolItem item)
    {
        var program = item.DurationTicks;
        if (_options.MidBreak == MidBreakMode.None || !_options.PlansBreaks || program < MinSplitProgramTicks)
        {
            return null;
        }

        var half = program / 2;
        if (_options.MidBreak == MidBreakMode.Chapter)
        {
            var chapter = item.ChapterTicks
                .Where(c => c > program / 4 && c < program * 3 / 4)
                .OrderBy(c => Math.Abs(c - half))
                .Cast<long?>()
                .FirstOrDefault();
            if (chapter is not null)
            {
                return chapter;
            }
        }

        return half;
    }

    /// <summary>Gets the mean block length, used to estimate how far through its sequence a rule segment is.</summary>
    internal long AverageBlockTicks => _pool.Length == 0 ? 0 : CycleTicks / _pool.Length;

    /// <summary>
    /// Returns the item at position <paramref name="n"/> of the channel's endless sequence (cycle after cycle) and its block length.
    /// </summary>
    internal PoolItem SequenceItem(long n, out long blockTicks)
    {
        var cycle = FloorDiv(n, _pool.Length);
        var position = (int)(n - (cycle * _pool.Length));
        // A reference swap, so concurrent readers see either the old or the new pair, never a mix.
        var cached = Volatile.Read(ref _lastOrder);
        int[] order;
        if (cached is not null && cached.Cycle == cycle)
        {
            order = cached.Order;
        }
        else
        {
            order = GetOrder(cycle);
            Volatile.Write(ref _lastOrder, new CachedOrder(cycle, order));
        }

        blockTicks = _blockTicks[order[position]];
        return _pool[order[position]];
    }

    /// <summary>Gets the block length an item airs as on this channel.</summary>
    internal long BlockTicksFor(PoolItem item) => BlockTicks(item);

    /// <summary>
    /// Builds a block for <paramref name="item"/> at <paramref name="start"/>; <paramref name="seed"/> names it and picks its commercials.
    /// </summary>
    internal ScheduledBlock BuildBlock(PoolItem item, long start, ulong seed) => BuildBlock(item, BlockTicks(item), seed, start);

    /// <summary>
    /// Fills a gap with commercials, then filler, as slots in an existing guide group.
    /// </summary>
    internal IReadOnlyList<ScheduledSlot> BuildGap(long start, long length, ulong seed, string idPrefix, string guideGroup)
    {
        var slots = new List<ScheduledSlot>();
        var rng = new SplitMix64(seed);
        var cursor = start;
        foreach (var (item, ticks) in FillBreak(length, ref rng))
        {
            slots.Add(new ScheduledSlot(
                idPrefix + "-" + slots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item is null ? SlotKind.Filler : SlotKind.Commercial,
                new DateTime(cursor, DateTimeKind.Utc),
                new DateTime(cursor + ticks, DateTimeKind.Utc),
                item,
                0,
                ticks,
                guideGroup));
            cursor += ticks;
        }

        return slots;
    }

    private List<(PoolItem? Item, long Ticks)> FillBreak(long length, ref SplitMix64 random)
    {
        var parts = new List<(PoolItem? Item, long Ticks)>();
        var remaining = length;
        PoolItem? previous = null;
        for (var i = 0; i < MaxCommercialsPerBreak && remaining > 0; i++)
        {
            var fits = _commercials.Where(c => c.DurationTicks <= remaining).ToArray();
            if (fits.Length > 1 && previous is not null)
            {
                fits = fits.Where(c => c.ItemId != previous.ItemId).ToArray();
            }

            if (fits.Length == 0)
            {
                break;
            }

            var pick = fits[random.NextInt(fits.Length)];
            parts.Add((pick, pick.DurationTicks));
            remaining -= pick.DurationTicks;
            previous = pick;
        }

        if (remaining > 0)
        {
            parts.Add((null, remaining));
        }

        return parts;
    }

    private ScheduledBlock BuildBlock(PoolItem item, long blockTicks, ulong blockHash, long start)
    {
        var blockId = StableHash.ToHex(blockHash, 12);
        var guideGroup = "g-" + blockId;

        var program = item.DurationTicks;
        var breakTicks = blockTicks - program;
        var split = breakTicks > 0 ? SplitPoint(item) : null;

        long midBreak = 0;
        if (split is not null)
        {
            midBreak = _options.Grid > TimeSpan.Zero ? RoundToSecond(breakTicks / 2) : _options.BreakLength.Ticks;
        }

        var endBreak = breakTicks - midBreak;

        var slots = new List<ScheduledSlot>();
        var rng = new SplitMix64(blockHash);
        var cursor = start;

        void Add(SlotKind kind, PoolItem? slotItem, long inPoint, long outPoint)
        {
            var length = outPoint - inPoint;
            if (length <= 0)
            {
                return;
            }

            var id = slots.Count == 0 ? "s-" + blockId : "s-" + blockId + "-" + slots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            slots.Add(new ScheduledSlot(
                id,
                kind,
                new DateTime(cursor, DateTimeKind.Utc),
                new DateTime(cursor + length, DateTimeKind.Utc),
                slotItem,
                inPoint,
                outPoint,
                guideGroup));
            cursor += length;
        }

        void AddBreak(long length, ref SplitMix64 random)
        {
            foreach (var (part, ticks) in FillBreak(length, ref random))
            {
                Add(part is null ? SlotKind.Filler : SlotKind.Commercial, part, 0, ticks);
            }
        }

        if (split is long at)
        {
            Add(SlotKind.Program, item, 0, at);
            AddBreak(midBreak, ref rng);
            Add(SlotKind.Program, item, at, program);
        }
        else
        {
            Add(SlotKind.Program, item, 0, program);
        }

        AddBreak(endBreak, ref rng);

        return new ScheduledBlock(
            blockId,
            item,
            new DateTime(start, DateTimeKind.Utc),
            new DateTime(start + blockTicks, DateTimeKind.Utc),
            slots);
    }

    private long[] PrefixStarts(int[] order)
    {
        var starts = new long[order.Length];
        long acc = 0;
        for (var i = 0; i < order.Length; i++)
        {
            starts[i] = acc;
            acc += _blockTicks[order[i]];
        }

        return starts;
    }

    private ulong BlockHash(long cycle, int position)
    {
        var hash = StableHash.Add(StableHash.Start(), ChannelId);
        hash = StableHash.Add(hash, Version);
        hash = StableHash.Add(hash, cycle);
        return StableHash.Add(hash, position);
    }

    private string ComputeVersion()
    {
        var hash = StableHash.Add(StableHash.Start(), ChannelId);
        hash = StableHash.Add(hash, (long)_options.Sorting);
        hash = StableHash.Add(hash, _options.Sorting is ChannelSorting.Block ? _options.BlockSize : 0);
        hash = StableHash.Add(hash, _anchorTicks);
        hash = StableHash.Add(hash, _options.Grid.Ticks);
        hash = StableHash.Add(hash, (long)_options.MidBreak);
        hash = StableHash.Add(hash, _options.BreakLength.Ticks);
        foreach (var item in _pool)
        {
            hash = StableHash.Add(hash, item.ItemId);
            hash = StableHash.Add(hash, item.DurationTicks);
            if (_options.MidBreak == MidBreakMode.Chapter)
            {
                foreach (var chapter in item.ChapterTicks)
                {
                    hash = StableHash.Add(hash, chapter);
                }
            }
        }

        foreach (var commercial in _commercials)
        {
            hash = StableHash.Add(hash, commercial.ItemId);
            hash = StableHash.Add(hash, commercial.DurationTicks);
        }

        return StableHash.ToHex(hash, 12);
    }

    private int[] GetOrder(long cycle)
    {
        switch (_options.Sorting)
        {
            case ChannelSorting.Cyclic:
                return Enumerable.Range(0, _pool.Length).ToArray();
            case ChannelSorting.RoundRobin:
                return Interleave(1);
            case ChannelSorting.Block:
                return Interleave(Math.Max(1, _options.BlockSize));
            case ChannelSorting.Marathon:
                return Marathon(cycle);
            default:
                if (_hasCopies)
                {
                    return WeightedOrder(cycle);
                }

                var order = RandomOrder(cycle);
                var previousLast = _pool[RandomOrder(cycle - 1)[^1]].ItemId;
                if (order.Length > 2 && _pool[order[0]].ItemId == previousLast)
                {
                    // Don't air the previous cycle's last item again first thing. The last position is never touched,
                    // so the next cycle's check against this one still holds.
                    for (var j = 1; j < order.Length - 1; j++)
                    {
                        (order[0], order[j]) = (order[j], order[0]);
                        if (Differs(order[0], previousLast) && NoRepeats(order, 0, j + 1))
                        {
                            break;
                        }

                        (order[0], order[j]) = (order[j], order[0]);
                    }
                }

                return order;
        }
    }

    private bool NoRepeats(int[] order, int from, int to)
    {
        for (var i = Math.Max(from, 1); i <= Math.Min(to, order.Length - 1); i++)
        {
            if (!Differs(order[i], order[i - 1]))
            {
                return false;
            }
        }

        return true;
    }

    private bool Differs(int a, int b) => _pool[a].ItemId != _pool[b].ItemId;

    private bool Differs(int a, Guid itemId) => _pool[a].ItemId != itemId;

    private List<List<int>> Groups() => Groups(p => p.GroupKey);

    private List<List<int>> Groups(Func<PoolItem, Guid> key)
    {
        var groups = new List<List<int>>();
        var byKey = new Dictionary<Guid, List<int>>();
        for (var i = 0; i < _pool.Length; i++)
        {
            if (!byKey.TryGetValue(key(_pool[i]), out var group))
            {
                group = [];
                byKey[key(_pool[i])] = group;
                groups.Add(group);
            }

            group.Add(i);
        }

        return groups;
    }

    private int[] Interleave(int take)
    {
        var groups = Groups();
        var order = new List<int>(_pool.Length);
        for (var round = 0; order.Count < _pool.Length; round++)
        {
            foreach (var group in groups)
            {
                order.AddRange(group.Skip(round * take).Take(take));
            }
        }

        return order.ToArray();
    }

    private int[] Marathon(long cycle)
    {
        var groups = Groups();
        var rng = CycleRng(cycle);
        for (var i = groups.Count - 1; i > 0; i--)
        {
            var j = rng.NextInt(i + 1);
            (groups[i], groups[j]) = (groups[j], groups[i]);
        }

        return groups.SelectMany(g => g).ToArray();
    }

    private int[] RandomOrder(long cycle)
    {
        var order = Enumerable.Range(0, _pool.Length).ToArray();
        var rng = CycleRng(cycle);
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = rng.NextInt(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    /// <summary>
    /// Random order for a pool holding weighted copies, with no item twice in a row, within or across cycles.
    /// </summary>
    /// <remarks>
    /// The cycle's last item is drawn first, from its own generator, so the next cycle can read it without computing
    /// this whole order. The rest is built greedily: each step draws a copy at random (so heavier items come up more
    /// often) among items other than the one just placed, except that an item holding more than half the remaining
    /// places must go now or it could not be spread out. Pools where one item outweighs all the others together can't
    /// avoid repeats; they get as few as the greedy pass manages.
    /// </remarks>
    private int[] WeightedOrder(long cycle)
    {
        var groups = Groups(p => p.ItemId);
        var rng = CycleRng(cycle);
        var last = DrawLastGroup(groups, cycle);
        var lastIndex = groups[last][^1];
        var remaining = groups.Select((g, i) => i == last ? g.Take(g.Count - 1).ToList() : g.ToList()).ToList();

        var order = new int[_pool.Length];
        order[^1] = lastIndex;
        var previous = _pool[DrawLastGroupIndex(cycle - 1)].ItemId;
        var left = _pool.Length - 1;

        for (var position = 0; position < _pool.Length - 1; position++, left--)
        {
            var avoid = previous;
            var candidates = Enumerable.Range(0, remaining.Count)
                .Where(g => remaining[g].Count > 0 && _pool[remaining[g][0]].ItemId != avoid)
                .ToList();
            if (position == _pool.Length - 2 && candidates.Count > 1)
            {
                candidates.RemoveAll(g => g == last);
            }

            if (candidates.Count == 0)
            {
                // Only the item just placed is left; a repeat can't be avoided.
                candidates = Enumerable.Range(0, remaining.Count).Where(g => remaining[g].Count > 0).ToList();
            }

            // The item fixed at the end can't take the place right before it either, so it has one place fewer.
            var forced = candidates
                .Where(g => remaining[g].Count * 2 > left - (g == last ? 1 : 0))
                .DefaultIfEmpty(-1)
                .MaxBy(g => g < 0 ? 0 : remaining[g].Count);
            int chosen;
            if (forced >= 0)
            {
                chosen = forced;
            }
            else
            {
                var draw = rng.NextInt(candidates.Sum(g => remaining[g].Count));
                chosen = candidates.First(g => (draw -= remaining[g].Count) < 0);
            }

            var group = remaining[chosen];
            order[position] = group[^1];
            group.RemoveAt(group.Count - 1);
            previous = _pool[order[position]].ItemId;
        }

        return order;
    }

    private int DrawLastGroupIndex(long cycle)
    {
        var groups = Groups(p => p.ItemId);
        return groups[DrawLastGroup(groups, cycle)][^1];
    }

    private int DrawLastGroup(List<List<int>> groups, long cycle)
    {
        var rng = new SplitMix64(StableHash.Add(StableHash.Add(StableHash.Add(StableHash.Start(), ChannelId), cycle), "last"));
        var draw = rng.NextInt(_pool.Length);
        for (var g = 0; g < groups.Count; g++)
        {
            draw -= groups[g].Count;
            if (draw < 0)
            {
                return g;
            }
        }

        return groups.Count - 1;
    }

    private SplitMix64 CycleRng(long cycle)
        => new(StableHash.Add(StableHash.Add(StableHash.Start(), ChannelId), cycle));

    private sealed record CachedOrder(long Cycle, int[] Order);
}
