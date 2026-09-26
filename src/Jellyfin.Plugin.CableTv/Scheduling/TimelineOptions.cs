using System;
using System.Collections.Generic;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// Everything besides the pool that shapes a channel's timeline.
/// </summary>
public sealed record TimelineOptions
{
    /// <summary>Gets the pool ordering.</summary>
    public ChannelSorting Sorting { get; init; } = ChannelSorting.Random;

    /// <summary>Gets the episodes per series run for <see cref="ChannelSorting.Block"/>.</summary>
    public int BlockSize { get; init; } = 3;

    /// <summary>Gets the commercials breaks are filled from. Empty means breaks are filler.</summary>
    public IReadOnlyList<PoolItem> Commercials { get; init; } = [];

    /// <summary>Gets the grid each programme block is padded to; zero disables fill-to-grid.</summary>
    public TimeSpan Grid { get; init; }

    /// <summary>Gets where programmes are split for a break.</summary>
    public MidBreakMode MidBreak { get; init; } = MidBreakMode.None;

    /// <summary>Gets the length of each break when fill-to-grid is off. Zero disables breaks.</summary>
    public TimeSpan BreakLength { get; init; }

    /// <summary>Gets a value indicating whether any break time is planned.</summary>
    internal bool PlansBreaks => Grid > TimeSpan.Zero || (BreakLength > TimeSpan.Zero && Commercials.Count > 0);
}
