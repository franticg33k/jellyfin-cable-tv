using System;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// A playable library item as the scheduler sees it. Pools are passed to the engine already in canonical order.
/// </summary>
/// <param name="ItemId">Jellyfin item id.</param>
/// <param name="MediaSourceId">Media source to direct-play.</param>
/// <param name="DurationTicks">Run time in ticks; must be positive.</param>
/// <param name="Title">Guide title (series name for episodes).</param>
public sealed record PoolItem(Guid ItemId, string MediaSourceId, long DurationTicks, string Title)
{
    /// <summary>Gets the episode title, when the item is an episode.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>Gets the season number.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the episode number.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets the series id, when the item is an episode.</summary>
    public Guid? SeriesId { get; init; }

    /// <summary>Gets a value indicating whether the item is a movie.</summary>
    public bool IsMovie { get; init; }

    /// <summary>Gets the overview.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the genres.</summary>
    public string[] Genres { get; init; } = [];

    /// <summary>Gets the official rating.</summary>
    public string? OfficialRating { get; init; }

    /// <summary>Gets the production year.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the local primary image path used for guide artwork.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Gets the local media file path.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the key items are grouped by for series-aware orderings: the series id, or the item id.</summary>
    public Guid GroupKey => SeriesId ?? ItemId;

    /// <summary>Gets how many times the item airs per cycle under random sorting.</summary>
    public int Weight { get; init; } = 1;

    /// <summary>Gets the chapter start positions in ticks, used for chapter mid-breaks.</summary>
    public long[] ChapterTicks { get; init; } = [];

    /// <summary>Gets the video codec, for example "h264" or "hevc".</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Gets the video width.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the video height.</summary>
    public int? Height { get; init; }

    /// <summary>Gets a value indicating whether the item has an audio stream.</summary>
    public bool HasAudio { get; init; } = true;

    /// <summary>Gets the "S02E05" style episode label, or null.</summary>
    public string? EpisodeLabel => SeasonNumber is int s && EpisodeNumber is int e
        ? FormattableString.Invariant($"S{s:00}E{e:00}")
        : null;
}
