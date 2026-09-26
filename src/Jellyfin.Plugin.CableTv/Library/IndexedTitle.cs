using System;

namespace Jellyfin.Plugin.CableTv.Library;

/// <summary>
/// What kind of library item a title is.
/// </summary>
public enum TitleKind
{
    /// <summary>A TV series; its episodes are what airs.</summary>
    Series = 0,

    /// <summary>A movie.</summary>
    Movie = 1,

    /// <summary>A music video.</summary>
    MusicVideo = 2,

    /// <summary>Any other standalone video (home videos, clips).</summary>
    Video = 3,
}

/// <summary>
/// A series or standalone video, with the metadata channels are built from.
/// </summary>
/// <param name="Id">Jellyfin item id.</param>
/// <param name="Kind">Series, movie, music video or video.</param>
/// <param name="Name">Title.</param>
/// <param name="Year">Production year (for a series, the year it started).</param>
public sealed record IndexedTitle(Guid Id, TitleKind Kind, string Name, int? Year)
{
    /// <summary>Gets the genres.</summary>
    public string[] Genres { get; init; } = [];

    /// <summary>Gets the studios; for series these include the original TV networks.</summary>
    public string[] Studios { get; init; } = [];

    /// <summary>Gets the tags.</summary>
    public string[] Tags { get; init; } = [];

    /// <summary>Gets the official rating, for example "TV-PG".</summary>
    public string? OfficialRating { get; init; }
}
