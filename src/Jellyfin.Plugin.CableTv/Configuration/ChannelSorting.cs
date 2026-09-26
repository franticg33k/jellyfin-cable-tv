namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// How a channel orders its content pool.
/// </summary>
public enum ChannelSorting
{
    /// <summary>
    /// A new deterministic shuffle of the whole pool on every cycle.
    /// </summary>
    Random = 0,

    /// <summary>
    /// The pool's canonical order (series, season, episode; movies by year), repeated.
    /// </summary>
    Cyclic = 1,
}
