using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class BreakPlanningTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PoolItem Item(int n, double minutes, params double[] chapterMinutes)
        => new(new Guid(n, 0, 0, new byte[8]), "m" + n, TimeSpan.FromMinutes(minutes).Ticks, "Show " + n)
        {
            ChapterTicks = chapterMinutes.Select(m => TimeSpan.FromMinutes(m).Ticks).ToArray(),
        };

    private static PoolItem Ad(int n, int seconds)
        => new(new Guid(1000 + n, 0, 0, new byte[8]), "a" + n, TimeSpan.FromSeconds(seconds).Ticks, "Ad " + n);

    private static readonly IReadOnlyList<PoolItem> Ads = [Ad(1, 30), Ad(2, 15), Ad(3, 60), Ad(4, 45)];

    [Fact]
    public void Grid_PadsEveryBlockToTheGrid_AndSlotsStayContiguous()
    {
        var timeline = new ChannelTimeline("ch", Anchor, [Item(1, 22), Item(2, 44), Item(3, 25.5)], new TimelineOptions
        {
            Sorting = ChannelSorting.Cyclic,
            Grid = TimeSpan.FromMinutes(30),
            Commercials = Ads,
        });

        var blocks = timeline.GetBlocks(Anchor, Anchor.AddHours(4)).ToList();

        Assert.All(blocks, b => Assert.Equal(0, (b.StartUtc - Anchor).Ticks % TimeSpan.FromMinutes(30).Ticks));
        Assert.Equal([30.0, 60.0, 30.0], blocks.Take(3).Select(b => (b.EndUtc - b.StartUtc).TotalMinutes));
        foreach (var block in blocks)
        {
            Assert.Equal(block.StartUtc, block.Slots[0].StartUtc);
            Assert.Equal(block.EndUtc, block.Slots[^1].EndUtc);
            for (var i = 1; i < block.Slots.Count; i++)
            {
                Assert.Equal(block.Slots[i - 1].EndUtc, block.Slots[i].StartUtc);
            }

            Assert.All(block.Slots, s => Assert.Equal("g-" + block.BlockId, s.GuideGroup));
        }
    }

    [Fact]
    public void Grid_FillsBreaksWithCommercials_ThenFiller()
    {
        var timeline = new ChannelTimeline("ch", Anchor, [Item(1, 22)], new TimelineOptions
        {
            Sorting = ChannelSorting.Cyclic,
            Grid = TimeSpan.FromMinutes(30),
            Commercials = Ads,
        });

        var block = timeline.GetBlocks(Anchor, Anchor.AddMinutes(1)).Single();

        Assert.Equal(SlotKind.Program, block.Slots[0].Kind);
        var breakSlots = block.Slots.Skip(1).ToList();
        Assert.NotEmpty(breakSlots);
        Assert.All(breakSlots.SkipLast(1), s => Assert.Equal(SlotKind.Commercial, s.Kind));
        Assert.True(breakSlots.Count(s => s.Kind == SlotKind.Commercial) >= 8);
        Assert.All(breakSlots.Where(s => s.Kind == SlotKind.Filler), s => Assert.Null(s.Item));
        for (var i = 2; i < block.Slots.Count; i++)
        {
            if (block.Slots[i].Kind == SlotKind.Commercial && block.Slots[i - 1].Kind == SlotKind.Commercial)
            {
                Assert.NotEqual(block.Slots[i - 1].Item!.ItemId, block.Slots[i].Item!.ItemId);
            }
        }
    }

    [Fact]
    public void GridWithoutCommercials_PadsWithFiller()
    {
        var timeline = new ChannelTimeline("ch", Anchor, [Item(1, 22)], new TimelineOptions
        {
            Sorting = ChannelSorting.Cyclic,
            Grid = TimeSpan.FromMinutes(30),
        });

        var slots = timeline.GetBlocks(Anchor, Anchor.AddMinutes(1)).Single().Slots;

        Assert.Equal([SlotKind.Program, SlotKind.Filler], slots.Select(s => s.Kind));
        Assert.Equal(8, (slots[1].EndUtc - slots[1].StartUtc).TotalMinutes);
    }

    [Fact]
    public void HalfwayMidBreak_SplitsProgrammeOnSameItem()
    {
        var timeline = new ChannelTimeline("ch", Anchor, [Item(1, 22)], new TimelineOptions
        {
            Sorting = ChannelSorting.Cyclic,
            Grid = TimeSpan.FromMinutes(30),
            MidBreak = MidBreakMode.Halfway,
            Commercials = Ads,
        });

        var slots = timeline.GetBlocks(Anchor, Anchor.AddMinutes(1)).Single().Slots;
        var programs = slots.Where(s => s.Kind == SlotKind.Program).ToList();

        Assert.Equal(2, programs.Count);
        Assert.Equal(programs[0].Item!.ItemId, programs[1].Item!.ItemId);
        Assert.Equal(0, programs[0].InPointTicks);
        Assert.Equal(TimeSpan.FromMinutes(11).Ticks, programs[0].OutPointTicks);
        Assert.Equal(programs[0].OutPointTicks, programs[1].InPointTicks);
        Assert.Equal(TimeSpan.FromMinutes(22).Ticks, programs[1].OutPointTicks);
        Assert.Equal(TimeSpan.FromMinutes(4), programs[1].StartUtc - programs[0].EndUtc);
    }

    [Fact]
    public void ChapterMidBreak_UsesChapterNearestHalfway()
    {
        var timeline = new ChannelTimeline("ch", Anchor, [Item(1, 40, 3, 17, 24, 37)], new TimelineOptions
        {
            Sorting = ChannelSorting.Cyclic,
            Grid = TimeSpan.FromMinutes(60),
            MidBreak = MidBreakMode.Chapter,
            Commercials = Ads,
        });

        var first = timeline.GetBlocks(Anchor, Anchor.AddMinutes(1)).Single().Slots[0];

        Assert.Equal(TimeSpan.FromMinutes(17).Ticks, first.OutPointTicks);
    }

    [Fact]
    public void FixedBreaks_WithoutGrid_AddBreakLengthPerBreak()
    {
        var timeline = new ChannelTimeline("ch", Anchor, [Item(1, 22), Item(2, 3)], new TimelineOptions
        {
            Sorting = ChannelSorting.Cyclic,
            BreakLength = TimeSpan.FromMinutes(2),
            MidBreak = MidBreakMode.Halfway,
            Commercials = Ads,
        });

        var blocks = timeline.GetBlocks(Anchor, Anchor.AddMinutes(40)).Take(2).ToList();

        // 22 min + mid and end breaks; the 3 min item is too short to split.
        Assert.Equal(26, (blocks[0].EndUtc - blocks[0].StartUtc).TotalMinutes);
        Assert.Equal(5, (blocks[1].EndUtc - blocks[1].StartUtc).TotalMinutes);
    }

    [Fact]
    public void Breaks_AreDeterministic()
    {
        TimelineOptions Options() => new() { Grid = TimeSpan.FromMinutes(30), MidBreak = MidBreakMode.Halfway, Commercials = Ads };
        var from = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc);

        var a = new ChannelTimeline("ch", Anchor, [Item(1, 22), Item(2, 21), Item(3, 23)], Options()).GetSlots(from, from.AddDays(1));
        var b = new ChannelTimeline("ch", Anchor, [Item(1, 22), Item(2, 21), Item(3, 23)], Options()).GetSlots(from, from.AddDays(1));

        Assert.Equal(a.Select(s => (s.SlotId, s.Item?.ItemId, s.StartUtc)), b.Select(s => (s.SlotId, s.Item?.ItemId, s.StartUtc)));
    }

    [Fact]
    public void SlotsInsideABlock_AreFoundByWindowQueries()
    {
        var timeline = new ChannelTimeline("ch", Anchor, [Item(1, 22)], new TimelineOptions
        {
            Grid = TimeSpan.FromMinutes(30),
            Commercials = Ads,
        });

        var slot = timeline.GetSlotAt(Anchor.AddMinutes(25));

        Assert.NotNull(slot);
        Assert.NotEqual(SlotKind.Program, slot.Kind);
        Assert.True(slot.StartUtc <= Anchor.AddMinutes(25) && slot.EndUtc > Anchor.AddMinutes(25));
    }
}
