namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// How a channel orders its content pool within each cycle. Every mode plays each pool item once per cycle.
/// </summary>
public enum ChannelSorting
{
    /// <summary>
    /// A new deterministic shuffle of the whole pool on every cycle. The only mode that honours source weights.
    /// </summary>
    Random = 0,

    /// <summary>
    /// The pool's canonical order (series, season, episode; movies by year), repeated.
    /// </summary>
    Cyclic = 1,

    /// <summary>
    /// One episode from each series in turn: S1E1, S2E1, S3E1, S1E2, …
    /// </summary>
    RoundRobin = 2,

    /// <summary>
    /// <see cref="ChannelDefinition.BlockSize"/> consecutive episodes from each series in turn.
    /// </summary>
    Block = 3,

    /// <summary>
    /// Each series plays through in order, back to back; the order of series is reshuffled every cycle.
    /// </summary>
    Marathon = 4,
}
