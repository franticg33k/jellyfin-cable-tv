using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// A smart filter for a <see cref="ContentSourceType.Rule"/> source: which series, movies and music videos qualify, by
/// their metadata. Every set condition must hold, except the "content" conditions (genres, keywords, title words,
/// studios, companies, members), where any one match is enough.
/// </summary>
/// <remarks>
/// Text comparisons ignore case. Genre, studio and company conditions match a substring of the item's value ("Comedy"
/// matches "Romantic Comedy"); keywords match exactly; title words match whole words. Keywords are Jellyfin tags, which
/// the TMDB metadata provider fills with TMDB keywords.
/// </remarks>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
public class ContentRule
{
    /// <summary>Gets or sets genres, any of which qualifies an item.</summary>
    public string[] Genres { get; set; } = [];

    /// <summary>Gets or sets genres an item must all have.</summary>
    public string[] RequireAllGenres { get; set; } = [];

    /// <summary>Gets or sets genres that rule an item out.</summary>
    public string[] ExcludeGenres { get; set; } = [];

    /// <summary>Gets or sets keywords (tags), any of which qualifies an item, on top of the genre condition.</summary>
    public string[] Keywords { get; set; } = [];

    /// <summary>Gets or sets genres a keyword match also needs one of; empty for none.</summary>
    public string[] KeywordGenres { get; set; } = [];

    /// <summary>
    /// Gets or sets broad genres that qualify an item only together with a keyword match (for example "Music" on a
    /// musicals channel), compared exactly.
    /// </summary>
    public string[] KeywordGatedGenres { get; set; } = [];

    /// <summary>Gets or sets keywords (tags) that rule an item out.</summary>
    public string[] ExcludeKeywords { get; set; } = [];

    /// <summary>
    /// Gets or sets words or phrases in the title, any of which qualifies an item. With no keyword, studio or company
    /// condition, they are the whole list: only matching titles qualify.
    /// </summary>
    public string[] TitleWords { get; set; } = [];

    /// <summary>Gets or sets words or phrases in the title that rule an item out.</summary>
    public string[] ExcludeTitleWords { get; set; } = [];

    /// <summary>Gets or sets exact titles that always qualify, whatever the other conditions say.</summary>
    public string[] AlwaysTitles { get; set; } = [];

    /// <summary>
    /// Gets or sets words or phrases in episode names: only those episodes air (for example holiday specials).
    /// </summary>
    public string[] EpisodeWords { get; set; } = [];

    /// <summary>Gets or sets studios, any of which qualifies an item; with genres, an item needs both.</summary>
    public string[] Studios { get; set; } = [];

    /// <summary>Gets or sets networks or production companies, any of which qualifies an item on its own.</summary>
    public string[] Companies { get; set; } = [];

    /// <summary>Gets or sets official ratings allowed ("PG", "TV-14"); empty for any.</summary>
    public string[] Ratings { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether unrated items pass the <see cref="Ratings"/> condition.</summary>
    public bool AllowUnrated { get; set; }

    /// <summary>Gets or sets official ratings that rule an item out.</summary>
    public string[] ExcludeRatings { get; set; } = [];

    /// <summary>Gets or sets the first production year allowed. Items without a year pass.</summary>
    public int? YearFrom { get; set; }

    /// <summary>Gets or sets the last production year allowed. Items without a year pass.</summary>
    public int? YearTo { get; set; }

    /// <summary>Gets or sets the lowest community rating (0–10) allowed; unrated items fail.</summary>
    public double? MinRating { get; set; }

    /// <summary>Gets or sets the shortest movie allowed, in minutes.</summary>
    public int? MinMinutes { get; set; }

    /// <summary>Gets or sets the longest movie allowed, in minutes.</summary>
    public int? MaxMinutes { get; set; }

    /// <summary>Gets or sets how recently an item must have been added to the library, in days.</summary>
    public int? AddedWithinDays { get; set; }

    /// <summary>Gets or sets how recently an item must have been released, in months.</summary>
    public int? ReleasedWithinMonths { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a kids channel: adult ratings are ruled out, and animation is
    /// allowed even when the genres don't name it (with <see cref="LockGenres"/>).
    /// </summary>
    public bool Kids { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether niche genres stay on their own channels: horror, reality, animation,
    /// documentary, war, western and talk items qualify only when the rule's genres name them (or the rule picks
    /// studios and no genres).
    /// </summary>
    public bool LockGenres { get; set; }

    /// <summary>
    /// Gets or sets curated members, "movie:{tmdb id}" or "tv:{tmdb id}": these qualify unless the year, title or
    /// excluded genre conditions rule them out.
    /// </summary>
    public string[] Members { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether only <see cref="Members"/> and <see cref="AlwaysTitles"/> qualify.</summary>
    public bool MembersOnly { get; set; }
}
