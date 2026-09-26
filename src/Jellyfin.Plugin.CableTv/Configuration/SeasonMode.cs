namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// How a seasonal lineup combines with the channel's usual content.
/// </summary>
public enum SeasonMode
{
    /// <summary>Seasonal content airs alongside the usual content.</summary>
    Mix = 0,

    /// <summary>Only seasonal content airs.</summary>
    Replace = 1,
}
