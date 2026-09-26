using System;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// One resolved entry on a channel's timeline.
/// </summary>
/// <param name="SlotId">Deterministic slot id.</param>
/// <param name="Kind">What the slot plays.</param>
/// <param name="StartUtc">Wall-clock start.</param>
/// <param name="EndUtc">Wall-clock end.</param>
/// <param name="Item">The item played; null for filler, which clients render as static or black.</param>
/// <param name="InPointTicks">Offset into the item where the slot starts.</param>
/// <param name="OutPointTicks">Offset into the item where the slot ends.</param>
/// <param name="GuideGroup">Groups breaks with their programme so a guide shows one block.</param>
public sealed record ScheduledSlot(
    string SlotId,
    SlotKind Kind,
    DateTime StartUtc,
    DateTime EndUtc,
    PoolItem? Item,
    long InPointTicks,
    long OutPointTicks,
    string GuideGroup);
