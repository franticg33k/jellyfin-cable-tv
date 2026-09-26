using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// A programme together with its breaks and grid padding: one guide entry.
/// </summary>
/// <param name="BlockId">Deterministic block id; the guide group of its slots.</param>
/// <param name="Item">The programme.</param>
/// <param name="StartUtc">Block start.</param>
/// <param name="EndUtc">Block end.</param>
/// <param name="Slots">Every slot in the block, contiguous from <paramref name="StartUtc"/> to <paramref name="EndUtc"/>.</param>
public sealed record ScheduledBlock(string BlockId, PoolItem Item, DateTime StartUtc, DateTime EndUtc, IReadOnlyList<ScheduledSlot> Slots);
