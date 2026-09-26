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

    /// <summary>
    /// Series and movies by title, matched loosely against the library: each value is "Title" or "Title (Year)"; the year
    /// picks between titles that share a name. Titles that aren't in the library are skipped.
    /// </summary>
    Titles = 6,

    /// <summary>
    /// Series and movies from any studio or TV network in <see cref="ContentSource.Values"/> (for example "NBC").
    /// </summary>
    Studio = 7,

    /// <summary>
    /// Items carrying any tag in <see cref="ContentSource.Values"/>.
    /// </summary>
    Tag = 8,

    /// <summary>
    /// Items with any official rating in <see cref="ContentSource.Values"/> (for example "TV-Y", "TV-G", "G").
    /// </summary>
    Rating = 9,

    /// <summary>
    /// Items from any year or year range in <see cref="ContentSource.Values"/> ("1994" or "1985-1994").
    /// </summary>
    Years = 10,

    /// <summary>
    /// Series and movies whose title contains any word or phrase in <see cref="ContentSource.Values"/> (for example "Christmas").
    /// </summary>
    Keyword = 11,

    /// <summary>
    /// Specific episodes by name: each value is "Show Title :: Episode Title".
    /// </summary>
    Episodes = 12,
}
