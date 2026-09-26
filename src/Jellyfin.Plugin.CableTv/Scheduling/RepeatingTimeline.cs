using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// One programme repeated in fixed blocks, for channels that aren't built from library items: an outside stream
/// (<see cref="SlotKind.Stream"/>) or a weather channel drawn by the client (<see cref="SlotKind.Generated"/>).
/// </summary>
public sealed class RepeatingTimeline : IChannelTimeline
{
    private readonly long _anchorTicks;
    private readonly long _blockTicks;
    private readonly SlotKind _kind;
    private readonly PoolItem _item;

    /// <summary>
    /// Initializes a new instance of the <see cref="RepeatingTimeline"/> class.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <param name="anchorUtc">Instant blocks are aligned to.</param>
    /// <param name="kind">Slot kind of every block.</param>
    /// <param name="item">What airs: its title names the guide entry; for a stream, its path is the URL.</param>
    /// <param name="block">Length of each guide block.</param>
    public RepeatingTimeline(string channelId, DateTime anchorUtc, SlotKind kind, PoolItem item, TimeSpan block)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelId);
        ArgumentNullException.ThrowIfNull(item);
        ChannelId = channelId;
        _anchorTicks = anchorUtc.Ticks;
        _blockTicks = Math.Max(block.Ticks, TimeSpan.FromMinutes(5).Ticks);
        _kind = kind;
        _item = item with { DurationTicks = _blockTicks };
        var hash = StableHash.Add(StableHash.Start(), channelId);
        hash = StableHash.Add(hash, (long)kind);
        hash = StableHash.Add(hash, item.Title);
        hash = StableHash.Add(hash, item.Path ?? string.Empty);
        hash = StableHash.Add(hash, _blockTicks);
        Version = StableHash.ToHex(StableHash.Add(hash, _anchorTicks), 12);
    }

    /// <inheritdoc />
    public string ChannelId { get; }

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public long CycleTicks => _blockTicks;

    /// <inheritdoc />
    public int PoolSize => 1;

    /// <inheritdoc />
    public IEnumerable<ScheduledBlock> GetBlocks(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
        {
            yield break;
        }

        var index = Math.DivRem(fromUtc.Ticks - _anchorTicks, _blockTicks, out var remainder);
        if (remainder < 0)
        {
            index--;
        }

        for (var emitted = 0; emitted < ChannelTimeline.MaxSlotsPerQuery; emitted++, index++)
        {
            var start = _anchorTicks + (index * _blockTicks);
            if (start >= toUtc.Ticks)
            {
                yield break;
            }

            var blockId = StableHash.ToHex(StableHash.Add(StableHash.Add(StableHash.Start(), Version), index), 12);
            var startUtc = new DateTime(start, DateTimeKind.Utc);
            var endUtc = new DateTime(start + _blockTicks, DateTimeKind.Utc);
            var slot = new ScheduledSlot("s-" + blockId, _kind, startUtc, endUtc, _item, 0, _blockTicks, "g-" + blockId);
            yield return new ScheduledBlock(blockId, _item, startUtc, endUtc, [slot]);
        }
    }

    /// <inheritdoc />
    public IEnumerable<ScheduledSlot> GetSlots(DateTime fromUtc, DateTime toUtc) => TimelineSlots.Between(this, fromUtc, toUtc);

    /// <inheritdoc />
    public ScheduledSlot? GetSlotAt(DateTime atUtc) => TimelineSlots.At(this, atUtc);
}
