using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// A channel timeline shaped by time slots, restricted hours, seasonal lineups and premieres.
/// </summary>
/// <remarks>
/// <para>
/// The schedule is planned one local day at a time. The day is cut into segments at every rule boundary; each segment
/// takes its content from one pool (a slot's, a seasonal lineup's, or the main pool minus items outside their allowed
/// hours) and is packed with whole programme blocks, so a slot starts on time rather than cutting into a programme.
/// Minutes left at a segment's end are filled with commercials, then filler.
/// </para>
/// <para>
/// Everything is still a pure function of the date: where a segment starts in its pool's sequence is estimated from
/// how much airtime that pool has had since the anchor, so episodes carry on from day to day without any stored state
/// (an episode can occasionally be skipped or repeated at a segment start).
/// </para>
/// </remarks>
public sealed class RuledTimeline : IChannelTimeline
{
    private const int MaxCachedDays = 64;
    private const int FitLookahead = 40;

    private readonly ScheduleRules _rules;
    private readonly ChannelTimeline _main;
    private readonly IReadOnlyList<PoolItem> _mainPool;
    private readonly TimelineOptions _options;
    private readonly DateTime _anchorUtc;
    private readonly DateOnly _anchorDay;
    private readonly ChannelTimeline[] _slots;
    private readonly ChannelTimeline[] _seasons;
    private readonly ConcurrentDictionary<string, ChannelTimeline> _filtered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, IReadOnlyList<ScheduledBlock>> _days = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RuledTimeline"/> class.
    /// </summary>
    /// <param name="channelId">Stable channel id.</param>
    /// <param name="anchorUtc">Fixed instant the schedule is computed from.</param>
    /// <param name="mainPool">Main pool, canonical order.</param>
    /// <param name="options">Sorting and break settings for the main pool; slots override the sorting.</param>
    /// <param name="rules">The rules.</param>
    public RuledTimeline(string channelId, DateTime anchorUtc, IReadOnlyList<PoolItem> mainPool, TimelineOptions options, ScheduleRules rules)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(mainPool);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rules);

        ChannelId = channelId;
        _rules = rules;
        _mainPool = mainPool;
        _options = options;
        _anchorUtc = DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc);
        _anchorDay = LocalDate(_anchorUtc);
        _main = new ChannelTimeline(channelId, _anchorUtc, mainPool, options);
        _slots = rules.Slots
            .Select(s => new ChannelTimeline(channelId + "/slot/" + s.Name, _anchorUtc, s.Pool, options with { Sorting = s.Sorting }))
            .ToArray();
        _seasons = rules.Seasons
            .Select(s => new ChannelTimeline(channelId + "/season/" + s.Name, _anchorUtc, s.Pool, options))
            .ToArray();
        Version = ComputeVersion();
    }

    /// <inheritdoc />
    public string ChannelId { get; }

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public long CycleTicks => _main.CycleTicks;

    /// <inheritdoc />
    public int PoolSize => _main.PoolSize;

    /// <inheritdoc />
    public ScheduledSlot? GetSlotAt(DateTime atUtc) => TimelineSlots.At(this, atUtc);

    /// <inheritdoc />
    public IEnumerable<ScheduledSlot> GetSlots(DateTime fromUtc, DateTime toUtc) => TimelineSlots.Between(this, fromUtc, toUtc);

    /// <inheritdoc />
    public IEnumerable<ScheduledBlock> GetBlocks(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
        {
            yield break;
        }

        var emitted = 0;
        for (var day = LocalDate(fromUtc); DayStart(day) < toUtc; day = day.AddDays(1))
        {
            foreach (var block in PlanDay(day))
            {
                if (block.EndUtc <= fromUtc)
                {
                    continue;
                }

                if (block.StartUtc >= toUtc || emitted++ >= ChannelTimeline.MaxSlotsPerQuery)
                {
                    yield break;
                }

                yield return block;
            }
        }
    }

    private static ScheduledBlock Tag(ScheduledBlock block, string? lineup, bool premiere = false)
        => block with
        {
            Lineup = lineup,
            IsPremiere = premiere,
            Slots = block.Slots.Select(s => s with { Lineup = lineup, IsPremiere = premiere }).ToList(),
        };

    private static ScheduledBlock Truncate(ScheduledBlock block, DateTime end)
    {
        var slots = block.Slots
            .Where(s => s.StartUtc < end)
            .Select(s => s.EndUtc <= end ? s : s with { EndUtc = end, OutPointTicks = s.InPointTicks + (end - s.StartUtc).Ticks })
            .ToList();
        return block with { EndUtc = end, Slots = slots };
    }

    private DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, _rules.Zone));

    private DateTime LocalToUtc(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        if (_rules.Zone.IsInvalidTime(local))
        {
            // Inside a daylight-saving gap: the clock jumps over it, so start at the end of the jump.
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, _rules.Zone);
    }

    private DateTime DayStart(DateOnly day) => LocalToUtc(day, TimeOnly.MinValue);

    /// <summary>
    /// The UTC intervals, within the day, that a daily window starting on each applicable day covers.
    /// </summary>
    private IEnumerable<(DateTime Start, DateTime End)> WindowIntervals(TimeWindow window, Func<DateOnly, bool> appliesOn, DateOnly day)
    {
        var dayStart = DayStart(day);
        var dayEnd = DayStart(day.AddDays(1));
        foreach (var startDay in new[] { day.AddDays(-1), day })
        {
            if (!appliesOn(startDay))
            {
                continue;
            }

            var start = LocalToUtc(startDay, window.Start);
            var end = window.CrossesMidnight ? LocalToUtc(startDay.AddDays(1), window.End) : LocalToUtc(startDay, window.End);
            if (start < dayStart)
            {
                start = dayStart;
            }

            if (end > dayEnd)
            {
                end = dayEnd;
            }

            if (end > start)
            {
                yield return (start, end);
            }
        }
    }

    private IReadOnlyList<ScheduledBlock> PlanDay(DateOnly day)
    {
        if (_days.TryGetValue(day.DayNumber, out var cached))
        {
            return cached;
        }

        var planned = BuildDay(day);
        if (_days.Count >= MaxCachedDays)
        {
            _days.Clear();
        }

        _days[day.DayNumber] = planned;
        return planned;
    }

    private List<ScheduledBlock> BuildDay(DateOnly day)
    {
        var dayStart = DayStart(day);
        var dayEnd = DayStart(day.AddDays(1));
        var dayIndex = day.DayNumber - _anchorDay.DayNumber;

        var slotIntervals = _rules.Slots
            .Select(rule => WindowIntervals(rule.Window, rule.AppliesOn, day).ToList())
            .ToList();
        var restrictionIntervals = _rules.Restrictions
            .Select(rule => WindowIntervals(rule.Window, _ => true, day).ToList())
            .ToList();

        (PoolItem Item, DateTime Start, DateTime End)? premiere = null;
        if (_rules.Premiere is { } rule && (rule.Days.Count == 0 || rule.Days.Contains(day.DayOfWeek)))
        {
            var since = dayStart.AddDays(-Math.Max(1, rule.WithinDays));
            var candidates = _mainPool
                .Where(p => p.DateCreated is DateTime added && added >= since && added < dayStart)
                .GroupBy(p => p.ItemId)
                .Select(g => g.First())
                .OrderBy(p => p.DateCreated)
                .ThenBy(p => p.ItemId)
                .ToList();
            if (candidates.Count > 0)
            {
                var item = candidates[Math.Abs(dayIndex) % candidates.Count];
                var start = LocalToUtc(day, rule.At);
                var end = start.AddTicks(_main.BlockTicksFor(item));
                if (end > dayEnd)
                {
                    end = dayEnd;
                }

                premiere = (item, start, end);
            }
        }

        var season = Array.FindIndex(_rules.Seasons.ToArray(), s => s.Covers(day));

        // Cut the day at every boundary and label each piece with the pool that airs in it.
        var points = new SortedSet<DateTime> { dayStart, dayEnd };
        foreach (var (start, end) in slotIntervals.SelectMany(i => i).Concat(restrictionIntervals.SelectMany(i => i)))
        {
            points.Add(start);
            points.Add(end);
        }

        if (premiere is { } p)
        {
            points.Add(p.Start);
            points.Add(p.End);
        }

        var segments = new List<(DateTime Start, DateTime End, string Key)>();
        var ordered = points.Where(t => t >= dayStart && t <= dayEnd).ToList();
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var (a, b) = (ordered[i], ordered[i + 1]);
            string key;
            if (premiere is { } pr && a >= pr.Start && a < pr.End)
            {
                key = "P";
            }
            else if (slotIntervals.FindIndex(list => list.Any(w => a >= w.Start && a < w.End)) is var slot and >= 0)
            {
                key = "S" + slot;
            }
            else
            {
                var excluded = 0L;
                for (var r = 0; r < restrictionIntervals.Count && r < 62; r++)
                {
                    if (!restrictionIntervals[r].Any(w => a >= w.Start && a < w.End))
                    {
                        excluded |= 1L << r;
                    }
                }

                key = "D" + season + ":" + excluded;
            }

            if (segments.Count > 0 && segments[^1].Key == key && segments[^1].End == a)
            {
                segments[^1] = (segments[^1].Start, b, key);
            }
            else
            {
                segments.Add((a, b, key));
            }
        }

        var blocks = new List<ScheduledBlock>();
        var airedToday = new Dictionary<string, long>(StringComparer.Ordinal);
        var dailyTotals = segments.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Sum(s => (s.End - s.Start).Ticks), StringComparer.Ordinal);
        foreach (var (start, end, key) in segments)
        {
            airedToday.TryGetValue(key, out var before);
            airedToday[key] = before + (end - start).Ticks;

            if (key == "P" && premiere is { } pm)
            {
                var block = Tag(_main.BuildBlock(pm.Item, pm.Start.Ticks, Seed(key, start, 0)), null, premiere: true);
                blocks.Add(block.EndUtc > end ? Truncate(block, end) : block);
                continue;
            }

            var (timeline, lineup) = Resolve(key);
            blocks.AddRange(Pack(timeline, lineup, key, start, end, ((long)dayIndex * dailyTotals[key]) + before));
        }

        return blocks;
    }

    private (ChannelTimeline Timeline, string? Lineup) Resolve(string key)
    {
        if (key[0] == 'S')
        {
            var index = int.Parse(key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
            return (_slots[index], _rules.Slots[index].Name);
        }

        // "D{season}:{excluded restrictions}"
        var parts = key[1..].Split(':');
        var season = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var excluded = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var lineup = season >= 0 ? _rules.Seasons[season].Name : null;
        if (excluded == 0)
        {
            return (season >= 0 ? _seasons[season] : _main, lineup);
        }

        var timeline = _filtered.GetOrAdd(key, _ =>
        {
            var hidden = new HashSet<Guid>();
            for (var r = 0; r < _rules.Restrictions.Count && r < 62; r++)
            {
                if ((excluded & (1L << r)) != 0)
                {
                    hidden.UnionWith(_rules.Restrictions[r].ItemIds);
                }
            }

            var pool = (season >= 0 ? _rules.Seasons[season].Pool : _mainPool).Where(p => !hidden.Contains(p.ItemId));
            return new ChannelTimeline(ChannelId + "/" + key, _anchorUtc, pool, _options);
        });
        return (timeline, lineup);
    }

    private List<ScheduledBlock> Pack(ChannelTimeline timeline, string? lineup, string key, DateTime start, DateTime end, long airtimeBefore)
    {
        var blocks = new List<ScheduledBlock>();
        if (timeline.PoolSize == 0)
        {
            blocks.Add(Tag(OffAir(key, start, end), lineup));
            return blocks;
        }

        var average = Math.Max(1, timeline.AverageBlockTicks);
        var n = airtimeBefore / average;
        var cursor = start.Ticks;
        var endTicks = end.Ticks;
        var index = 0;

        while (cursor < endTicks)
        {
            var item = timeline.SequenceItem(n, out var length);
            if (cursor + length > endTicks)
            {
                // Try the next few items for one that fits the time left.
                var found = false;
                for (var k = 1; k <= FitLookahead; k++)
                {
                    var candidate = timeline.SequenceItem(n + k, out var candidateLength);
                    if (cursor + candidateLength <= endTicks)
                    {
                        (item, length, n, found) = (candidate, candidateLength, n + k, true);
                        break;
                    }
                }

                if (!found)
                {
                    break;
                }
            }

            blocks.Add(Tag(timeline.BuildBlock(item, cursor, Seed(key, start, index++)), lineup));
            cursor += length;
            n++;
        }

        var gap = endTicks - cursor;
        if (gap <= 0)
        {
            return blocks;
        }

        if (blocks.Count == 0)
        {
            // Nothing fits: air the next programme and cut it at the segment's end.
            var item = timeline.SequenceItem(n, out _);
            blocks.Add(Truncate(Tag(timeline.BuildBlock(item, cursor, Seed(key, start, index)), lineup), end));
            return blocks;
        }

        var last = blocks[^1];
        var fill = timeline.BuildGap(cursor, gap, Seed(key, start, -1), "s-" + last.BlockId + "-gap", "g-" + last.BlockId);
        blocks[^1] = last with { EndUtc = end, Slots = [.. last.Slots, .. fill.Select(f => f with { Lineup = lineup })] };
        return blocks;
    }

    private ScheduledBlock OffAir(string key, DateTime start, DateTime end)
    {
        var id = StableHash.ToHex(Seed(key, start, -2), 12);
        var slot = new ScheduledSlot("s-" + id, SlotKind.Filler, start, end, null, 0, (end - start).Ticks, "g-" + id);
        return new ScheduledBlock(id, null, start, end, [slot]);
    }

    private ulong Seed(string key, DateTime segmentStart, int index)
    {
        var hash = StableHash.Add(StableHash.Start(), ChannelId);
        hash = StableHash.Add(hash, Version);
        hash = StableHash.Add(hash, key);
        hash = StableHash.Add(hash, segmentStart.Ticks);
        return StableHash.Add(hash, index);
    }

    private string ComputeVersion()
    {
        var hash = StableHash.Add(StableHash.Start(), _main.Version);
        hash = StableHash.Add(hash, _rules.Zone.Id);
        for (var i = 0; i < _slots.Length; i++)
        {
            var rule = _rules.Slots[i];
            hash = StableHash.Add(hash, rule.Name + "|" + rule.Window + "|" + string.Join(',', rule.Days));
            hash = StableHash.Add(hash, _slots[i].Version);
        }

        for (var i = 0; i < _seasons.Length; i++)
        {
            var rule = _rules.Seasons[i];
            hash = StableHash.Add(hash, FormattableString.Invariant($"{rule.Name}|{rule.From}|{rule.To}"));
            hash = StableHash.Add(hash, _seasons[i].Version);
        }

        foreach (var rule in _rules.Restrictions)
        {
            hash = StableHash.Add(hash, rule.Window.ToString());
            foreach (var id in rule.ItemIds.Order())
            {
                hash = StableHash.Add(hash, id);
            }
        }

        if (_rules.Premiere is { } premiere)
        {
            hash = StableHash.Add(hash, FormattableString.Invariant($"{premiere.At}|{string.Join(',', premiere.Days)}|{premiere.WithinDays}"));
            foreach (var item in _mainPool.Where(p => p.DateCreated is not null))
            {
                hash = StableHash.Add(hash, item.DateCreated!.Value.Ticks);
            }
        }

        return StableHash.ToHex(hash, 12);
    }
}
