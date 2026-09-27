using System.Collections.Generic;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Logos;
using Jellyfin.Plugin.CableTv.Scheduling;
using Jellyfin.Plugin.CableTv.Streaming;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// A channel definition together with the timeline built from its resolved pool.
/// </summary>
/// <param name="Definition">Channel definition.</param>
/// <param name="Timeline">Timeline built from the pool.</param>
/// <param name="Stream">Format of the channel's continuous Live TV stream.</param>
/// <param name="Logo">The channel's logo, or null.</param>
public sealed record ChannelSnapshot(ChannelDefinition Definition, IChannelTimeline Timeline, StreamProfile Stream, LogoRef? Logo = null)
{
    /// <summary>Gets the commercials the channel's breaks draw from.</summary>
    public IReadOnlyList<PoolItem> Commercials { get; init; } = [];
}
