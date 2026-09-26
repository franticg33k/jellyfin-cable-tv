namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// Where a programme is split for a commercial break.
/// </summary>
public enum MidBreakMode
{
    /// <summary>Breaks only between programmes.</summary>
    None = 0,

    /// <summary>One break at the programme's halfway point.</summary>
    Halfway = 1,

    /// <summary>One break at the chapter marker nearest halfway; falls back to halfway when there is none.</summary>
    Chapter = 2,
}
