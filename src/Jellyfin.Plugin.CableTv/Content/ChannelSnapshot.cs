using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// A channel definition together with the timeline built from its resolved pool.
/// </summary>
/// <param name="Definition">Channel definition.</param>
/// <param name="Timeline">Timeline built from the pool.</param>
public sealed record ChannelSnapshot(ChannelDefinition Definition, ChannelTimeline Timeline);
