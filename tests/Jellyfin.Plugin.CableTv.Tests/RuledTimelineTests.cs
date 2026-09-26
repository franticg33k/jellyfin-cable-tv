using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class RuledTimelineTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static List<PoolItem> Pool(string series, int count, double minutes)
        => Enumerable.Range(1, count)
            .Select(e => new PoolItem(Guid.NewGuid(), "m", TimeSpan.FromMinutes(minutes).Ticks, series)
            {
                SeriesId = new Guid(series.GetHashCode(StringComparison.Ordinal), 0, 0, new byte[8]),
                SeasonNumber = 1,
                EpisodeNumber = e,
            })
            .ToList();

    private static TimeWindow Window(string text)
    {
        Assert.True(TimeWindow.TryParse(text, out var window));
        return window;
    }

    private static void AssertContiguous(IReadOnlyList<ScheduledSlot> slots)
    {
        for (var i = 1; i < slots.Count; i++)
        {
            Assert.Equal(slots[i - 1].EndUtc, slots[i].StartUtc);
        }
    }

    private static DateTime Local(TimeZoneInfo zone, int year, int month, int day, int hour, int minute = 0)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), zone);

    [Fact]
    public void TimeSlot_AirsItsOwnPool_StartingExactlyOnTime()
    {
        var main = Pool("Main", 20, 22);
        var cartoons = Pool("Cartoons", 10, 11);
        var rules = new ScheduleRules
        {
            Zone = NewYork,
            Slots = [new TimeSlotRule("Cartoons", Window("07:00-10:00"), [], cartoons, ChannelSorting.Cyclic)],
        };
        var timeline = new RuledTimeline("ch", Anchor, main, new TimelineOptions(), rules);
        var slotStart = Local(NewYork, 2026, 9, 26, 7);
        var slotEnd = Local(NewYork, 2026, 9, 26, 10);

        var day = timeline.GetSlots(Local(NewYork, 2026, 9, 26, 0), Local(NewYork, 2026, 9, 27, 0)).ToList();

        AssertContiguous(day);
        Assert.Contains(day, s => s.StartUtc == slotStart && s.Kind == SlotKind.Program && s.Item!.Title == "Cartoons");
        Assert.All(day.Where(s => s.StartUtc >= slotStart && s.StartUtc < slotEnd && s.Item is not null), s => Assert.Equal("Cartoons", s.Item!.Title));
        Assert.All(day.Where(s => (s.StartUtc < slotStart || s.StartUtc >= slotEnd) && s.Item is not null), s => Assert.Equal("Main", s.Item!.Title));
        Assert.DoesNotContain(day, s => s.StartUtc < slotStart && s.EndUtc > slotStart);
        Assert.DoesNotContain(day, s => s.StartUtc < slotEnd && s.EndUtc > slotEnd);
        Assert.All(day.Where(s => s.StartUtc >= slotStart && s.StartUtc < slotEnd), s => Assert.Equal("Cartoons", s.Lineup));
    }

    [Fact]
    public void TimeSlot_Days_LimitWhenItApplies()
    {
        var rules = new ScheduleRules
        {
            Zone = TimeZoneInfo.Utc,
            Slots = [new TimeSlotRule("Saturday", Window("09:00-12:00"), ScheduleRules.ParseDays(["Sat"]), Pool("Sat", 5, 30), ChannelSorting.Random)],
        };
        var timeline = new RuledTimeline("ch", Anchor, Pool("Main", 10, 30), new TimelineOptions(), rules);

        var saturday = timeline.GetSlotAt(new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc));
        var sunday = timeline.GetSlotAt(new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Sat", saturday!.Item!.Title);
        Assert.Equal("Main", sunday!.Item!.Title);
    }

    [Fact]
    public void TimeSlot_CanRunPastMidnight()
    {
        var rules = new ScheduleRules
        {
            Zone = TimeZoneInfo.Utc,
            Slots = [new TimeSlotRule("Late", Window("23:00-02:00"), ScheduleRules.ParseDays(["Fri"]), Pool("Late", 5, 30), ChannelSorting.Random)],
        };
        var timeline = new RuledTimeline("ch", Anchor, Pool("Main", 10, 30), new TimelineOptions(), rules);

        // Friday 2026-09-25 23:00 to Saturday 02:00.
        Assert.Equal("Late", timeline.GetSlotAt(new DateTime(2026, 9, 25, 23, 30, 0, DateTimeKind.Utc))!.Item!.Title);
        Assert.Equal("Late", timeline.GetSlotAt(new DateTime(2026, 9, 26, 1, 30, 0, DateTimeKind.Utc))!.Item!.Title);
        Assert.Equal("Main", timeline.GetSlotAt(new DateTime(2026, 9, 26, 2, 30, 0, DateTimeKind.Utc))!.Item!.Title);
        Assert.Equal("Main", timeline.GetSlotAt(new DateTime(2026, 9, 26, 23, 30, 0, DateTimeKind.Utc))!.Item!.Title);
    }

    [Fact]
    public void RestrictedHours_KeepItemsInsideTheirWindow()
    {
        var family = Pool("Family", 10, 30);
        var late = Pool("Late", 10, 30);
        var rules = new ScheduleRules
        {
            Zone = TimeZoneInfo.Utc,
            Restrictions = [new RestrictionRule(late.Select(p => p.ItemId).ToHashSet(), Window("21:00-05:00"))],
        };
        var timeline = new RuledTimeline("ch", Anchor, [.. family, .. late], new TimelineOptions(), rules);

        var week = timeline.GetSlots(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc)).ToList();

        AssertContiguous(week);
        foreach (var slot in week.Where(s => s.Item?.Title == "Late"))
        {
            var startHour = slot.StartUtc.TimeOfDay;
            var endHour = slot.EndUtc.TimeOfDay;
            Assert.True(startHour >= TimeSpan.FromHours(21) || startHour < TimeSpan.FromHours(5), $"Late item started at {slot.StartUtc}");
            Assert.True(endHour >= TimeSpan.FromHours(21) || endHour <= TimeSpan.FromHours(5), $"Late item ended at {slot.EndUtc}");
        }

        Assert.Contains(week, s => s.Item?.Title == "Late");
        Assert.Contains(week, s => s.Item?.Title == "Family" && s.StartUtc.Hour == 12);
    }

    [Fact]
    public void Season_ReplacesTheLineupOnItsDates_AndCanWrapTheYear()
    {
        var rules = new ScheduleRules
        {
            Zone = TimeZoneInfo.Utc,
            Seasons = [new SeasonalRule("Holidays", (12, 20), (1, 2), Pool("Holiday", 5, 30))],
        };
        var timeline = new RuledTimeline("ch", Anchor, Pool("Main", 10, 30), new TimelineOptions(), rules);

        Assert.Equal("Holiday", timeline.GetSlotAt(new DateTime(2026, 12, 24, 12, 0, 0, DateTimeKind.Utc))!.Item!.Title);
        Assert.Equal("Holiday", timeline.GetSlotAt(new DateTime(2027, 1, 1, 12, 0, 0, DateTimeKind.Utc))!.Item!.Title);
        Assert.Equal("Main", timeline.GetSlotAt(new DateTime(2027, 1, 3, 12, 0, 0, DateTimeKind.Utc))!.Item!.Title);
        Assert.Equal("Holidays", timeline.GetSlotAt(new DateTime(2026, 12, 24, 12, 0, 0, DateTimeKind.Utc))!.Lineup);
    }

    [Fact]
    public void Premiere_AirsANewItemAtItsTime_MarkedAsPremiere()
    {
        var main = Pool("Main", 10, 30);
        var fresh = new PoolItem(Guid.NewGuid(), "m", TimeSpan.FromMinutes(44).Ticks, "New Show") { DateCreated = new DateTime(2026, 9, 24, 15, 0, 0, DateTimeKind.Utc) };
        var rules = new ScheduleRules { Zone = TimeZoneInfo.Utc, Premiere = new PremiereRule(new TimeOnly(20, 0), [], 3) };
        var timeline = new RuledTimeline("ch", Anchor, [.. main, fresh], new TimelineOptions(), rules);

        var premiere = timeline.GetSlotAt(new DateTime(2026, 9, 25, 20, 10, 0, DateTimeKind.Utc));
        var tooLate = timeline.GetBlocks(new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(fresh.ItemId, premiere!.Item!.ItemId);
        Assert.True(premiere.IsPremiere);
        Assert.Equal(new DateTime(2026, 9, 25, 20, 0, 0, DateTimeKind.Utc), premiere.StartUtc);
        Assert.DoesNotContain(tooLate, b => b.IsPremiere);
    }

    [Fact]
    public void DaylightSavingDays_StayContiguous()
    {
        var rules = new ScheduleRules
        {
            Zone = NewYork,
            Slots = [new TimeSlotRule("Night", Window("01:00-04:00"), [], Pool("Night", 5, 25), ChannelSorting.Random)],
        };
        var timeline = new RuledTimeline("ch", Anchor, Pool("Main", 10, 22), new TimelineOptions { Grid = TimeSpan.FromMinutes(30) }, rules);

        foreach (var (month, day) in new[] { (3, 8), (11, 1) })
        {
            var changeDay = Local(NewYork, 2026, month, day, 12);
            var slots = timeline.GetSlots(changeDay.AddDays(-1), changeDay.AddDays(1)).ToList();
            AssertContiguous(slots);
            Assert.True(slots.Count > 20);
        }
    }

    [Fact]
    public void EmptySlotPool_IsOffAir()
    {
        var rules = new ScheduleRules
        {
            Zone = TimeZoneInfo.Utc,
            Slots = [new TimeSlotRule("Dark", Window("02:00-06:00"), [], [], ChannelSorting.Random)],
        };
        var timeline = new RuledTimeline("ch", Anchor, Pool("Main", 10, 30), new TimelineOptions(), rules);

        var slot = timeline.GetSlotAt(new DateTime(2026, 9, 26, 3, 0, 0, DateTimeKind.Utc));

        Assert.Equal(SlotKind.Filler, slot!.Kind);
        Assert.Null(slot.Item);
        Assert.Equal(new DateTime(2026, 9, 26, 2, 0, 0, DateTimeKind.Utc), slot.StartUtc);
        Assert.Equal(new DateTime(2026, 9, 26, 6, 0, 0, DateTimeKind.Utc), slot.EndUtc);
    }

    [Fact]
    public void Cyclic_Slot_CarriesOnFromDayToDay()
    {
        var episodes = Pool("Serial", 100, 30);
        var rules = new ScheduleRules
        {
            Zone = TimeZoneInfo.Utc,
            Slots = [new TimeSlotRule("Serial", Window("18:00-19:00"), [], episodes, ChannelSorting.Cyclic)],
        };
        var timeline = new RuledTimeline("ch", Anchor, Pool("Main", 10, 30), new TimelineOptions(), rules);

        var firsts = Enumerable.Range(0, 5)
            .Select(d => timeline.GetSlotAt(new DateTime(2026, 2, 1, 18, 5, 0, DateTimeKind.Utc).AddDays(d))!.Item!.EpisodeNumber!.Value)
            .ToList();

        for (var i = 1; i < firsts.Count; i++)
        {
            Assert.Equal((firsts[i - 1] + 2 - 1) % 100 + 1, firsts[i]);
        }
    }

    [Fact]
    public void Schedule_IsDeterministic_WithUniqueSlotIds()
    {
        ScheduleRules Rules(List<PoolItem> slotPool) => new()
        {
            Zone = NewYork,
            Slots = [new TimeSlotRule("Evening", Window("19:00-22:00"), [], slotPool, ChannelSorting.Random)],
        };
        var main = Pool("Main", 15, 22);
        var slot = Pool("Evening", 8, 44);
        var options = new TimelineOptions { Grid = TimeSpan.FromMinutes(30), Commercials = [new PoolItem(Guid.NewGuid(), "ad", TimeSpan.FromSeconds(30).Ticks, "Ad")] };
        var from = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

        var a = new RuledTimeline("ch", Anchor, main, options, Rules(slot)).GetSlots(from, from.AddDays(7)).ToList();
        var b = new RuledTimeline("ch", Anchor, main, options, Rules(slot)).GetSlots(from, from.AddDays(7)).ToList();

        Assert.Equal(a.Select(s => (s.SlotId, s.StartUtc, s.Item?.ItemId)), b.Select(s => (s.SlotId, s.StartUtc, s.Item?.ItemId)));
        Assert.Equal(a.Count, a.Select(s => s.SlotId).Distinct().Count());
        AssertContiguous(a);
    }

    [Fact]
    public void Parsing_AcceptsTheDocumentedFormats()
    {
        Assert.True(TimeWindow.TryParse("21:00-05:00", out var window));
        Assert.True(window.CrossesMidnight);
        Assert.True(TimeWindow.TryParseTime("24:00", out var midnight));
        Assert.Equal(TimeOnly.MinValue, midnight);
        Assert.False(TimeWindow.TryParse("9pm-5am", out _));
        Assert.True(SeasonalRule.TryParseMonthDay("12-01", out var md));
        Assert.Equal((12, 1), md);
        Assert.False(SeasonalRule.TryParseMonthDay("02-30", out _));
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Saturday, DayOfWeek.Thursday], ScheduleRules.ParseDays(["mon", "Saturday", "Th", "x"]));
    }
}
