using System;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class SortingTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PoolItem Episode(char series, int episode)
        => new(Guid.NewGuid(), "x", TimeSpan.FromMinutes(10).Ticks, series.ToString())
        {
            SeriesId = new Guid(series, 0, 0, new byte[8]),
            SeasonNumber = 1,
            EpisodeNumber = episode,
        };

    // Canonical order: all of A, then B, then C.
    private static readonly PoolItem[] Pool =
    [
        Episode('A', 1), Episode('A', 2), Episode('A', 3), Episode('A', 4),
        Episode('B', 1), Episode('B', 2),
        Episode('C', 1), Episode('C', 2), Episode('C', 3),
    ];

    private static string Labels(ChannelTimeline timeline, int count)
        => string.Join(' ', timeline.GetSlots(Anchor, Anchor.AddMinutes(10 * count)).Select(s => s.Item!.Title + s.Item.EpisodeNumber));

    [Fact]
    public void RoundRobin_TakesOneFromEachSeriesInTurn()
    {
        var timeline = new ChannelTimeline("ch", Anchor, Pool, new TimelineOptions { Sorting = ChannelSorting.RoundRobin });

        Assert.Equal("A1 B1 C1 A2 B2 C2 A3 C3 A4", Labels(timeline, 9));
    }

    [Fact]
    public void Block_TakesBlockSizeFromEachSeriesInTurn()
    {
        var timeline = new ChannelTimeline("ch", Anchor, Pool, new TimelineOptions { Sorting = ChannelSorting.Block, BlockSize = 2 });

        Assert.Equal("A1 A2 B1 B2 C1 C2 A3 A4 C3", Labels(timeline, 9));
    }

    [Fact]
    public void Marathon_KeepsSeriesTogetherAndInOrder_AndReordersSeriesPerCycle()
    {
        var timeline = new ChannelTimeline("ch", Anchor, Pool, new TimelineOptions { Sorting = ChannelSorting.Marathon });
        var cycles = Enumerable.Range(0, 12)
            .Select(k => string.Join(' ', timeline.GetSlots(Anchor.AddMinutes(90 * k), Anchor.AddMinutes(90 * (k + 1))).Select(s => s.Item!.Title + s.Item.EpisodeNumber)))
            .ToList();

        Assert.All(cycles, c => Assert.Contains("A1 A2 A3 A4", c, StringComparison.Ordinal));
        Assert.All(cycles, c => Assert.Contains("C1 C2 C3", c, StringComparison.Ordinal));
        Assert.True(cycles.Distinct().Count() > 1);
    }

    [Fact]
    public void Weights_AirItemsMoreOftenUnderRandom_WithoutBackToBackRepeats()
    {
        var heavy = Episode('H', 1) with { Weight = 3 };
        var pool = new[] { heavy, Episode('A', 1), Episode('B', 1), Episode('C', 1), Episode('D', 1) };
        var timeline = new ChannelTimeline("ch", Anchor, pool, new TimelineOptions { Sorting = ChannelSorting.Random });

        var slots = timeline.GetSlots(Anchor, Anchor.AddDays(2)).ToList();

        Assert.Equal(7, timeline.PoolSize);
        var share = slots.Count(s => s.Item!.ItemId == heavy.ItemId) / (double)slots.Count;
        Assert.InRange(share, 3.0 / 7 - 0.01, 3.0 / 7 + 0.01);
        for (var i = 1; i < slots.Count; i++)
        {
            Assert.NotEqual(slots[i - 1].Item!.ItemId, slots[i].Item!.ItemId);
        }
    }

    [Fact]
    public void Weights_AreIgnoredByOrderedSorting()
    {
        var pool = new[] { Episode('H', 1) with { Weight = 3 }, Episode('A', 1) };

        var timeline = new ChannelTimeline("ch", Anchor, pool, new TimelineOptions { Sorting = ChannelSorting.Cyclic });

        Assert.Equal(2, timeline.PoolSize);
    }

    [Theory]
    [InlineData(new[] { 3, 1, 1, 1, 1 })]
    [InlineData(new[] { 2, 2, 1 })]
    [InlineData(new[] { 5, 2, 2, 1, 1, 1 })]
    [InlineData(new[] { 10, 3, 3, 3, 3, 3, 1, 1 })]
    [InlineData(new[] { 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 })]
    public void Weights_NeverRepeatBackToBack_WhenAvoidable(int[] weights)
    {
        var pool = weights.Select((w, i) => Episode((char)('A' + i), 1) with { Weight = w }).ToArray();

        for (var c = 0; c < 40; c++)
        {
            var timeline = new ChannelTimeline("ch-" + c, Anchor, pool, new TimelineOptions { Sorting = ChannelSorting.Random });
            var slots = timeline.GetSlots(Anchor, Anchor.AddMinutes(10 * weights.Sum() * 6)).ToList();
            for (var i = 1; i < slots.Count; i++)
            {
                Assert.NotEqual(slots[i - 1].Item!.ItemId, slots[i].Item!.ItemId);
            }
        }
    }
}
