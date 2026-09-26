using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class ChannelTimelineTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<PoolItem> Pool(params int[] minutes)
        => minutes
            .Select((m, i) => new PoolItem(
                new Guid(i + 1, 0, 0, new byte[8]),
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan.FromMinutes(m).Ticks,
                "Item " + (i + 1)))
            .ToList();

    [Fact]
    public void EmptyPool_HasNoSlots()
    {
        var timeline = new ChannelTimeline("ch", ChannelSorting.Random, Anchor, []);

        Assert.Empty(timeline.GetSlots(Anchor, Anchor.AddDays(1)));
        Assert.Null(timeline.GetSlotAt(Anchor));
    }

    [Fact]
    public void ItemsWithoutDuration_AreDropped()
    {
        var pool = Pool(30, 0, 22);

        var timeline = new ChannelTimeline("ch", ChannelSorting.Cyclic, Anchor, pool);

        Assert.Equal(2, timeline.PoolSize);
        Assert.Equal(TimeSpan.FromMinutes(52).Ticks, timeline.CycleTicks);
    }

    [Fact]
    public void Cyclic_PlaysPoolInOrderFromAnchor()
    {
        var pool = Pool(30, 20, 10);
        var timeline = new ChannelTimeline("ch", ChannelSorting.Cyclic, Anchor, pool);

        var slots = timeline.GetSlots(Anchor, Anchor.AddMinutes(120)).ToList();

        Assert.Equal(
            ["Item 1", "Item 2", "Item 3", "Item 1", "Item 2", "Item 3"],
            slots.Select(s => s.Item!.Title));
        Assert.Equal(Anchor, slots[0].StartUtc);
        Assert.Equal(Anchor.AddMinutes(60), slots[3].StartUtc);
    }

    [Theory]
    [InlineData(ChannelSorting.Cyclic)]
    [InlineData(ChannelSorting.Random)]
    public void Slots_AreContiguous(ChannelSorting sorting)
    {
        var timeline = new ChannelTimeline("ch", sorting, Anchor, Pool(22, 44, 23, 90, 7, 30));

        var slots = timeline.GetSlots(Anchor.AddDays(12).AddMinutes(13), Anchor.AddDays(14)).ToList();

        Assert.True(slots.Count > 10);
        for (var i = 1; i < slots.Count; i++)
        {
            Assert.Equal(slots[i - 1].EndUtc, slots[i].StartUtc);
        }
    }

    [Theory]
    [InlineData(ChannelSorting.Cyclic)]
    [InlineData(ChannelSorting.Random)]
    public void Window_StartsWithSlotAiringAtFrom_AndStopsBeforeTo(ChannelSorting sorting)
    {
        var timeline = new ChannelTimeline("ch", sorting, Anchor, Pool(22, 44, 23, 90, 7, 30));
        var from = new DateTime(2026, 9, 26, 20, 15, 3, DateTimeKind.Utc);
        var to = from.AddHours(3);

        var slots = timeline.GetSlots(from, to).ToList();

        Assert.True(slots[0].StartUtc <= from && slots[0].EndUtc > from);
        Assert.True(slots[^1].StartUtc < to && slots[^1].EndUtc >= to);
    }

    [Fact]
    public void Random_IsDeterministicAcrossInstances()
    {
        var from = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc);

        var a = new ChannelTimeline("ch-0412", ChannelSorting.Random, Anchor, Pool(22, 44, 23, 90, 7, 30, 11, 60))
            .GetSlots(from, from.AddDays(2)).ToList();
        var b = new ChannelTimeline("ch-0412", ChannelSorting.Random, Anchor, Pool(22, 44, 23, 90, 7, 30, 11, 60))
            .GetSlots(from, from.AddDays(2)).ToList();

        Assert.Equal(a, b);
    }

    [Fact]
    public void Random_DiffersBetweenChannels()
    {
        var from = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc);
        var pool = Pool(22, 44, 23, 90, 7, 30, 11, 60, 25, 25);

        var a = new ChannelTimeline("ch-a", ChannelSorting.Random, Anchor, pool).GetSlots(from, from.AddDays(1)).Select(s => s.Item!.ItemId);
        var b = new ChannelTimeline("ch-b", ChannelSorting.Random, Anchor, pool).GetSlots(from, from.AddDays(1)).Select(s => s.Item!.ItemId);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Random_EveryCyclePlaysEachItemOnce()
    {
        var pool = Pool(22, 44, 23, 90, 7, 30, 11);
        var timeline = new ChannelTimeline("ch", ChannelSorting.Random, Anchor, pool);
        var cycle = TimeSpan.FromTicks(timeline.CycleTicks);

        for (var k = 0; k < 20; k++)
        {
            var start = Anchor + (cycle * k);
            var slots = timeline.GetSlots(start, start + cycle).ToList();
            Assert.Equal(pool.Count, slots.Count);
            Assert.Equal(pool.Select(p => p.ItemId).Order(), slots.Select(s => s.Item!.ItemId).Order());
            Assert.Equal(start, slots[0].StartUtc);
        }
    }

    [Fact]
    public void Random_NeverRepeatsAnItemAcrossCycleBoundary()
    {
        var timeline = new ChannelTimeline("ch", ChannelSorting.Random, Anchor, Pool(10, 10, 10));

        var slots = timeline.GetSlots(Anchor, Anchor.AddDays(5)).ToList();

        for (var i = 1; i < slots.Count; i++)
        {
            Assert.NotEqual(slots[i - 1].Item!.ItemId, slots[i].Item!.ItemId);
        }
    }

    [Fact]
    public void SlotAt_MatchesWindowQuery()
    {
        var timeline = new ChannelTimeline("ch", ChannelSorting.Random, Anchor, Pool(22, 44, 23, 90, 7, 30));
        var from = new DateTime(2026, 9, 26, 18, 0, 0, DateTimeKind.Utc);

        foreach (var slot in timeline.GetSlots(from, from.AddHours(12)))
        {
            var mid = slot.StartUtc + ((slot.EndUtc - slot.StartUtc) / 2);
            Assert.Equal(slot, timeline.GetSlotAt(mid));
            Assert.Equal(slot, timeline.GetSlotAt(slot.StartUtc));
        }
    }

    [Fact]
    public void TimesBeforeAnchor_AreScheduled()
    {
        var timeline = new ChannelTimeline("ch", ChannelSorting.Cyclic, Anchor, Pool(30, 30));

        var slot = timeline.GetSlotAt(Anchor.AddMinutes(-10));

        Assert.NotNull(slot);
        Assert.Equal("Item 2", slot.Item!.Title);
        Assert.Equal(Anchor.AddMinutes(-30), slot.StartUtc);
    }

    [Fact]
    public void Version_ChangesWithPool_AndIsStableOtherwise()
    {
        var a = new ChannelTimeline("ch", ChannelSorting.Random, Anchor, Pool(22, 44));
        var b = new ChannelTimeline("ch", ChannelSorting.Random, Anchor, Pool(22, 44));
        var c = new ChannelTimeline("ch", ChannelSorting.Random, Anchor, Pool(22, 45));
        var d = new ChannelTimeline("ch", ChannelSorting.Cyclic, Anchor, Pool(22, 44));

        Assert.Equal(a.Version, b.Version);
        Assert.NotEqual(a.Version, c.Version);
        Assert.NotEqual(a.Version, d.Version);
    }

    [Fact]
    public void SlotIds_AreStableAndUnique()
    {
        var timeline = new ChannelTimeline("ch", ChannelSorting.Random, Anchor, Pool(22, 44, 23));
        var from = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc);

        var first = timeline.GetSlots(from, from.AddDays(1)).ToList();
        var second = timeline.GetSlots(from.AddHours(6), from.AddDays(1)).ToList();

        Assert.Equal(first.Count, first.Select(s => s.SlotId).Distinct().Count());
        Assert.Equal(second.Select(s => s.SlotId), first.SkipWhile(s => s.SlotId != second[0].SlotId).Select(s => s.SlotId));
    }

    [Fact]
    public void Query_IsCapped()
    {
        var timeline = new ChannelTimeline("ch", ChannelSorting.Cyclic, Anchor, [new PoolItem(Guid.NewGuid(), "x", 1, "tiny")]);

        Assert.Equal(ChannelTimeline.MaxSlotsPerQuery, timeline.GetSlots(Anchor, Anchor.AddDays(1)).Count());
    }
}
