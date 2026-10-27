using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// Wraps a channel's timeline so that a scheduled file that has gone missing (deleted or moved since the last rebuild)
/// airs as filler instead of failing to play. The schedule is otherwise untouched, so nothing reshuffles; the next
/// rebuild drops the item.
/// </summary>
public sealed class AvailableTimeline : IChannelTimeline
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IChannelTimeline _inner;
    private readonly Func<string, bool> _exists;
    private readonly Action<PoolItem>? _onMissing;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, (bool Exists, DateTime At)> _checked = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="AvailableTimeline"/> class.
    /// </summary>
    /// <param name="inner">The timeline to wrap.</param>
    /// <param name="exists">Tells whether a file exists.</param>
    /// <param name="onMissing">Called when a scheduled file is missing.</param>
    /// <param name="clock">The time (UTC); the system clock when omitted.</param>
    public AvailableTimeline(IChannelTimeline inner, Func<string, bool> exists, Action<PoolItem>? onMissing = null, Func<DateTime>? clock = null)
    {
        _inner = inner;
        _exists = exists;
        _onMissing = onMissing;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    public string ChannelId => _inner.ChannelId;

    /// <inheritdoc />
    public string Version => _inner.Version;

    /// <inheritdoc />
    public long CycleTicks => _inner.CycleTicks;

    /// <inheritdoc />
    public int PoolSize => _inner.PoolSize;

    /// <inheritdoc />
    public IEnumerable<ScheduledBlock> GetBlocks(DateTime fromUtc, DateTime toUtc)
        => _inner.GetBlocks(fromUtc, toUtc).Select(block => block with { Slots = block.Slots.Select(Check).ToList() });

    /// <inheritdoc />
    public IEnumerable<ScheduledSlot> GetSlots(DateTime fromUtc, DateTime toUtc)
        => _inner.GetSlots(fromUtc, toUtc).Select(Check);

    /// <inheritdoc />
    public ScheduledSlot? GetSlotAt(DateTime atUtc)
        => _inner.GetSlotAt(atUtc) is { } slot ? Check(slot) : null;

    private ScheduledSlot Check(ScheduledSlot slot)
    {
        if (slot.Kind is SlotKind.Stream or SlotKind.Generated or SlotKind.Filler || slot.Item is not { Path: { Length: > 0 } path } item)
        {
            return slot;
        }

        var now = _clock();
        if (_checked.TryGetValue(path, out var known) && now - known.At < CacheFor ? known.Exists : Probe(path, now))
        {
            return slot;
        }

        _onMissing?.Invoke(item);
        return slot with { Kind = SlotKind.Filler, Item = null };
    }

    private bool Probe(string path, DateTime now)
    {
        bool exists;
        try
        {
            exists = _exists(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // An unreadable share isn't proof the file is gone; let playback try.
            exists = true;
        }

        _checked[path] = (exists, now);
        return exists;
    }
}
