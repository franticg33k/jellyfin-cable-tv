using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CableTv.Scheduling;
using Jellyfin.Plugin.CableTv.Streaming;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class PromoPlannerTests
{
    // 2026-03-02 is a Monday; UTC is used as the time zone so the local times are easy to read.
    private static readonly DateTime Day = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Blocks(params ScheduledBlock[] blocks) : IChannelTimeline
    {
        public string ChannelId => "c";

        public string Version => "v";

        public long CycleTicks => 0;

        public int PoolSize => blocks.Length;

        public IEnumerable<ScheduledBlock> GetBlocks(DateTime fromUtc, DateTime toUtc)
            => blocks.Where(b => b.EndUtc > fromUtc && b.StartUtc < toUtc);

        public IEnumerable<ScheduledSlot> GetSlots(DateTime fromUtc, DateTime toUtc) => GetBlocks(fromUtc, toUtc).SelectMany(b => b.Slots);

        public ScheduledSlot? GetSlotAt(DateTime atUtc) => GetSlots(atUtc, atUtc.AddTicks(1)).FirstOrDefault();
    }

    private static PoolItem Show(string title, string? episode = null)
        => new(Guid.NewGuid(), "m", TimeSpan.FromMinutes(30).Ticks, title) { EpisodeTitle = episode };

    private static PoolItem Movie(string title, int year, string rating)
        => new(Guid.NewGuid(), "m", TimeSpan.FromMinutes(136).Ticks, title) { IsMovie = true, ProductionYear = year, OfficialRating = rating };

    private static ScheduledBlock Block(string id, PoolItem item, int startMinute, int minutes, params ScheduledSlot[] slots)
    {
        var start = Day.AddHours(18).AddMinutes(startMinute);
        return new ScheduledBlock(id, item, start, start.AddMinutes(minutes), slots);
    }

    private static ScheduledSlot Filler(string id, DateTime start, int seconds)
        => new(id, SlotKind.Filler, start, start.AddSeconds(seconds), null, 0, 0, "g");

    /// <summary>A slot id that lands on the "up next" variant, found by trying ids.</summary>
    private static string IdWhere(bool movieVariant)
    {
        for (var i = 0; i < 100; i++)
        {
            var id = "slot-" + i;
            var hash = 17;
            foreach (var c in id)
            {
                hash = unchecked((hash * 31) + c);
            }

            if (((uint)hash % 3 == 0) == movieVariant)
            {
                return id;
            }
        }

        throw new InvalidOperationException();
    }

    [Fact]
    public void AGapAnnouncesTheNextProgramme()
    {
        var gap = Filler(IdWhere(false), Day.AddHours(18).AddMinutes(30), 60);
        var timeline = new Blocks(
            Block("a", Show("Friends"), 0, 30),
            Block("b", Show("Seinfeld", "The Contest"), 31, 30));
        var info = PromoPlanner.Pick(timeline, gap, TimeZoneInfo.Utc);

        Assert.NotNull(info);
        Assert.Equal(PromoKind.UpNext, info.Kind);
        Assert.Equal("COMING UP NEXT", info.Heading);
        Assert.Equal("Seinfeld", info.Title);
        Assert.Equal("The Contest", info.Subtitle);
        Assert.Equal("6:31 PM", info.TimeText);
    }

    [Fact]
    public void ABreakInsideAProgrammeAnnouncesWhatFollowsIt()
    {
        var breakSlot = Filler(IdWhere(false), Day.AddHours(18).AddMinutes(10), 90);
        var timeline = new Blocks(
            Block("a", Show("Friends", "The One With the Pilot"), 0, 30, breakSlot),
            Block("b", Show("Seinfeld"), 30, 30));
        var info = PromoPlanner.Pick(timeline, breakSlot, TimeZoneInfo.Utc);

        Assert.Equal("Seinfeld", info!.Title);
        Assert.Equal("COMING UP NEXT", info.Heading);
    }

    [Fact]
    public void SomeBreaksPromoteALaterMovie()
    {
        var gap = Filler(IdWhere(true), Day.AddHours(18).AddMinutes(30), 60);
        var timeline = new Blocks(
            Block("a", Show("Friends"), 0, 30),
            Block("b", Show("Seinfeld"), 31, 30),
            Block("c", Movie("The Matrix", 1999, "R"), 120, 136));
        var info = PromoPlanner.Pick(timeline, gap, TimeZoneInfo.Utc);

        Assert.Equal(PromoKind.Tonight, info!.Kind);
        Assert.Equal("TONIGHT AT 8:00 PM", info.Heading);
        Assert.Equal("The Matrix", info.Title);
        Assert.Null(info.Subtitle);
        Assert.Equal("1999  ·  R  ·  2 h 16 min", info.Details);
    }

    [Fact]
    public void WithoutALaterMovieItFallsBackToTheNextProgramme()
    {
        var gap = Filler(IdWhere(true), Day.AddHours(18).AddMinutes(30), 60);
        var timeline = new Blocks(Block("a", Show("Friends"), 0, 30), Block("b", Show("Seinfeld"), 31, 30));
        Assert.Equal(PromoKind.UpNext, PromoPlanner.Pick(timeline, gap, TimeZoneInfo.Utc)!.Kind);
    }

    [Fact]
    public void NothingComingUpMeansNoCard()
    {
        var gap = Filler("x", Day.AddHours(18).AddMinutes(30), 60);
        Assert.Null(PromoPlanner.Pick(new Blocks(Block("a", Show("Friends"), 0, 30)), gap, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ADayAfterIsNamed()
    {
        var gap = Filler(IdWhere(true), Day.AddHours(22), 60);
        var movie = new ScheduledBlock("m", Movie("Heat", 1995, "R"), Day.AddDays(1).AddHours(6), Day.AddDays(1).AddHours(8), []);
        var next = new ScheduledBlock("n", Show("News"), Day.AddHours(22).AddMinutes(1), Day.AddHours(22).AddMinutes(31), []);
        var info = PromoPlanner.Pick(new Blocks(next, movie), gap, TimeZoneInfo.Utc);

        Assert.Equal("TOMORROW AT 6:00 AM", info!.Heading);
    }
}
