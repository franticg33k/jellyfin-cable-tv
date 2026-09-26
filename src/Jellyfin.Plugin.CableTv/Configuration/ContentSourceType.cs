namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// Where a channel's content comes from.
/// </summary>
public enum ContentSourceType
{
    /// <summary>
    /// Every playable item under a library (<see cref="ContentSource.Ids"/> are library folder ids).
    /// </summary>
    Library = 0,

    /// <summary>
    /// Items in a collection (box set).
    /// </summary>
    Collection = 1,

    /// <summary>
    /// Items in a playlist.
    /// </summary>
    Playlist = 2,

    /// <summary>
    /// Items matching any genre in <see cref="ContentSource.Values"/>.
    /// </summary>
    Genre = 3,

    /// <summary>
    /// Items from any decade in <see cref="ContentSource.Values"/>, written as the first year ("1990").
    /// </summary>
    Decade = 4,

    /// <summary>
    /// Specific items: series, seasons, episodes, movies or folders.
    /// </summary>
    Items = 5,
}
