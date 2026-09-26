using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using Jellyfin.Plugin.CableTv.Streaming;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// A channel definition together with the timeline built from its resolved pool.
/// </summary>
/// <param name="Definition">Channel definition.</param>
/// <param name="Timeline">Timeline built from the pool.</param>
/// <param name="Stream">Format of the channel's continuous Live TV stream.</param>
public sealed record ChannelSnapshot(ChannelDefinition Definition, ChannelTimeline Timeline, StreamProfile Stream);
