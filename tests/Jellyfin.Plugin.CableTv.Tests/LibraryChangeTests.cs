using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class LibraryChangeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ChangeDebounce Debounce() => new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1));

    [Fact]
    public void WaitsForTheChangesToStop()
    {
        var d = Debounce();
        Assert.False(d.TakeDue(T0));

        d.Record(T0);
        Assert.False(d.TakeDue(T0.AddMinutes(1)));
        d.Record(T0.AddMinutes(4));
        Assert.False(d.TakeDue(T0.AddMinutes(8)));
        Assert.True(d.TakeDue(T0.AddMinutes(9)));
        Assert.False(d.TakeDue(T0.AddMinutes(10)));
        Assert.False(d.Pending);
    }

    [Fact]
    public void KeepsAGapBetweenRebuilds()
    {
        var d = Debounce();
        d.Record(T0);
        Assert.True(d.TakeDue(T0.AddMinutes(6)));
        d.Record(T0.AddMinutes(7));
        Assert.False(d.TakeDue(T0.AddMinutes(13)));
        Assert.True(d.TakeDue(T0.AddMinutes(21)));
    }

    [Fact]
    public void AScanThatNeverStopsStillGetsARebuild()
    {
        var d = Debounce();
        for (var m = 0; m < 70; m++)
        {
            d.Record(T0.AddMinutes(m));
        }

        Assert.False(d.TakeDue(T0.AddMinutes(59)));
        Assert.True(d.TakeDue(T0.AddMinutes(61)));
    }

    [Fact]
    public void ARebuildForAnotherReasonClearsWhatWaited()
    {
        var d = Debounce();
        d.Record(T0);
        d.Rebuilt(T0.AddMinutes(1));
        Assert.False(d.Pending);
        Assert.False(d.TakeDue(T0.AddMinutes(30)));
    }

    [Fact]
    public void SignalIsReadOnce()
    {
        var s = new ChangeSignal();
        Assert.False(s.Take());
        s.Raise();
        s.Raise();
        Assert.True(s.Take());
        Assert.False(s.Take());
    }

    private sealed class Fixed(params ScheduledSlot[] slots) : IChannelTimeline
    {
        public string ChannelId => "c";

        public string Version => "v";

        public long CycleTicks => 0;

        public int PoolSize => slots.Length;

        public IEnumerable<ScheduledBlock> GetBlocks(DateTime fromUtc, DateTime toUtc)
            => slots.Select(s => new ScheduledBlock("b" + s.SlotId, s.Item, s.StartUtc, s.EndUtc, [s]));

        public IEnumerable<ScheduledSlot> GetSlots(DateTime fromUtc, DateTime toUtc) => slots;

        public ScheduledSlot? GetSlotAt(DateTime atUtc) => slots.FirstOrDefault(s => s.StartUtc <= atUtc && atUtc < s.EndUtc);
    }

    private static ScheduledSlot Slot(string id, string? path, SlotKind kind = SlotKind.Program)
        => new(id, kind, T0, T0.AddMinutes(30), new PoolItem(Guid.NewGuid(), "m", 1, "Movie") { Path = path }, 0, 1, "g");

    [Fact]
    public void AMissingFileAirsAsFillerAndAsksForARebuild()
    {
        var missing = new List<string?>();
        var timeline = new AvailableTimeline(
            new Fixed(Slot("a", "/media/here.mkv"), Slot("b", "/media/gone.mkv")),
            path => path == "/media/here.mkv",
            item => missing.Add(item.Path),
            () => T0);

        var slots = timeline.GetSlots(T0, T0.AddHours(1)).ToList();
        Assert.Equal(SlotKind.Program, slots[0].Kind);
        Assert.NotNull(slots[0].Item);
        Assert.Equal(SlotKind.Filler, slots[1].Kind);
        Assert.Null(slots[1].Item);
        Assert.Equal("b", slots[1].SlotId);
        Assert.Equal(["/media/gone.mkv"], missing);

        var block = timeline.GetBlocks(T0, T0.AddHours(1)).Last();
        Assert.Equal(SlotKind.Filler, block.Slots[0].Kind);
        Assert.NotNull(block.Item);
        Assert.Equal("v", timeline.Version);
    }

    [Fact]
    public void LeavesStreamsAndSlotsWithoutAPathAlone()
    {
        var timeline = new AvailableTimeline(
            new Fixed(Slot("a", null), Slot("b", "http://x/stream", SlotKind.Stream), Slot("c", "/gone", SlotKind.Generated)),
            _ => false);
        Assert.All(timeline.GetSlots(T0, T0.AddHours(1)), s => Assert.NotEqual(SlotKind.Filler, s.Kind));
    }

    [Fact]
    public void ChecksEachFileOnceAWhileAndTrustsAnUnreadableShare()
    {
        var now = T0;
        var calls = 0;
        var timeline = new AvailableTimeline(new Fixed(Slot("a", "/x")), _ => { calls++; return true; }, null, () => now);
        _ = timeline.GetSlots(T0, T0.AddHours(1)).ToList();
        _ = timeline.GetSlots(T0, T0.AddHours(1)).ToList();
        Assert.Equal(1, calls);
        now = T0.AddMinutes(1);
        _ = timeline.GetSlots(T0, T0.AddHours(1)).ToList();
        Assert.Equal(2, calls);

        var flaky = new AvailableTimeline(new Fixed(Slot("a", "/y")), _ => throw new System.IO.IOException("mount gone"), null, () => T0);
        Assert.Equal(SlotKind.Program, flaky.GetSlots(T0, T0.AddHours(1)).Single().Kind);
    }
}
