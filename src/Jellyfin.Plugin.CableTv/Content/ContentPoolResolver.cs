using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// Turns a channel's content sources into a de-duplicated pool in canonical order.
/// </summary>
public class ContentPoolResolver
{
    /// <summary>
    /// Item kinds a commercial source admits.
    /// </summary>
    private static readonly BaseItemKind[] CommercialKinds =
        [BaseItemKind.Video, BaseItemKind.MusicVideo, BaseItemKind.Trailer, BaseItemKind.Movie, BaseItemKind.Episode];

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IChapterManager _chapterManager;
    private readonly ILogger<ContentPoolResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentPoolResolver"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="chapterManager">Chapter manager.</param>
    /// <param name="logger">Logger.</param>
    public ContentPoolResolver(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IChapterManager chapterManager,
        ILogger<ContentPoolResolver> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _chapterManager = chapterManager;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the programme pool for a channel.
    /// </summary>
    /// <param name="channel">Channel definition.</param>
    /// <returns>The pool, in canonical order.</returns>
    public IReadOnlyList<PoolItem> Resolve(ChannelDefinition channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var pool = Resolve(channel.Sources, ParseKinds(channel.ItemTypes), channel.AllowSpecials);
        _logger.LogDebug("Channel {Channel} resolved {Count} items", channel.Id, pool.Count);
        return pool;
    }

    /// <summary>
    /// Resolves a commercial pool.
    /// </summary>
    /// <param name="sources">Commercial sources.</param>
    /// <returns>The commercials.</returns>
    public IReadOnlyList<PoolItem> ResolveCommercials(IEnumerable<ContentSource> sources)
        => Resolve(sources, CommercialKinds, allowSpecials: true);

    private IReadOnlyList<PoolItem> Resolve(IEnumerable<ContentSource> sources, BaseItemKind[] kinds, bool allowSpecials)
    {
        var found = new Dictionary<Guid, (Video Video, int Weight)>();

        foreach (var source in sources)
        {
            var weight = Math.Clamp(source.Weight, 1, 10);
            foreach (var item in ResolveSource(source, kinds))
            {
                if (item is Video video && IsSchedulable(video, kinds, allowSpecials))
                {
                    found[video.Id] = found.TryGetValue(video.Id, out var existing)
                        ? (video, Math.Max(existing.Weight, weight))
                        : (video, weight);
                }
            }
        }

        return found.Values
            .OrderBy(f => SortGroup(f.Video), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Video.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(f => f.Video.IndexNumber ?? int.MaxValue)
            .ThenBy(f => f.Video.ProductionYear ?? int.MaxValue)
            .ThenBy(f => f.Video.SortName ?? f.Video.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Video.Id)
            .Select(f => ToPoolItem(f.Video, f.Weight))
            .ToList();
    }

    private static string SortGroup(Video video)
        => video is Episode episode ? episode.SeriesName ?? string.Empty : string.Empty;

    private static BaseItemKind[] ParseKinds(IEnumerable<string> names)
    {
        var kinds = new List<BaseItemKind>();
        foreach (var name in names)
        {
            if (Enum.TryParse<BaseItemKind>(name, true, out var kind)
                && kind is BaseItemKind.Episode or BaseItemKind.Movie or BaseItemKind.MusicVideo or BaseItemKind.Video or BaseItemKind.Trailer)
            {
                kinds.Add(kind);
            }
        }

        return kinds.Count == 0 ? [BaseItemKind.Episode, BaseItemKind.Movie] : kinds.Distinct().ToArray();
    }

    private static bool IsSchedulable(Video video, BaseItemKind[] kinds, bool allowSpecials)
        => !video.IsVirtualItem
           && video.RunTimeTicks is > 0
           && !string.IsNullOrEmpty(video.Path)
           && video.ExtraType is null
           && kinds.Contains(video.GetBaseItemKind())
           && (allowSpecials || video is not Episode { ParentIndexNumber: 0 });

    private PoolItem ToPoolItem(Video video, int weight)
    {
        var streams = _mediaSourceManager.GetMediaStreams(video.Id);
        var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var chapters = _chapterManager.GetChapters(video.Id)
            .Select(c => c.StartPositionTicks)
            .Where(t => t > 0)
            .ToArray();

        var episode = video as Episode;
        var imagePath = video.GetImagePath(ImageType.Primary, 0)
                        ?? episode?.Series?.GetImagePath(ImageType.Primary, 0);

        return new PoolItem(
            video.Id,
            video.Id.ToString("N", CultureInfo.InvariantCulture),
            video.RunTimeTicks!.Value,
            episode?.SeriesName ?? video.Name)
        {
            EpisodeTitle = episode?.Name,
            SeasonNumber = episode?.ParentIndexNumber,
            EpisodeNumber = episode?.IndexNumber,
            SeriesId = episode?.SeriesId,
            IsMovie = video is Movie,
            Overview = video.Overview,
            Genres = video.Genres ?? [],
            OfficialRating = video.OfficialRating,
            ProductionYear = video.ProductionYear,
            ImagePath = imagePath,
            Path = video.Path,
            Weight = weight,
            ChapterTicks = chapters,
            VideoCodec = videoStream?.Codec,
            Width = videoStream?.Width,
            Height = videoStream?.Height,
            HasAudio = streams.Any(s => s.Type == MediaStreamType.Audio),
        };
    }

    private IEnumerable<BaseItem> ResolveSource(ContentSource source, BaseItemKind[] kinds)
    {
        switch (source.Type)
        {
            case ContentSourceType.Library:
            case ContentSourceType.Items:
                return source.Ids.SelectMany(id => Expand(_libraryManager.GetItemById(id), kinds));

            case ContentSourceType.Collection:
            case ContentSourceType.Playlist:
                return source.Ids
                    .Select(_libraryManager.GetItemById)
                    .OfType<Folder>()
                    .SelectMany(folder => folder.GetLinkedChildren())
                    .SelectMany(child => Expand(child, kinds));

            case ContentSourceType.Genre:
                return QueryWithSeries(kinds, q => q.Genres = source.Values);

            case ContentSourceType.Decade:
                var years = source.Values
                    .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : (int?)null)
                    .OfType<int>()
                    .SelectMany(start => Enumerable.Range(start - (start % 10), 10))
                    .Distinct()
                    .ToArray();
                return years.Length == 0 ? [] : QueryWithSeries(kinds, q => q.Years = years);

            default:
                _logger.LogWarning("Unknown content source type {Type}", source.Type);
                return [];
        }
    }

    /// <summary>
    /// Queries matching items plus matching series expanded to episodes, since genre and year usually live on the series.
    /// </summary>
    private IEnumerable<BaseItem> QueryWithSeries(BaseItemKind[] kinds, Action<InternalItemsQuery> filter)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = kinds.Contains(BaseItemKind.Episode) ? [.. kinds, BaseItemKind.Series] : kinds,
            Recursive = true,
            IsVirtualItem = false,
        };
        filter(query);

        return _libraryManager.GetItemList(query).SelectMany(item => Expand(item, kinds));
    }

    private IEnumerable<BaseItem> Expand(BaseItem? item, BaseItemKind[] kinds)
    {
        switch (item)
        {
            case null:
                return [];
            case Video video:
                return [video];
            case Folder folder:
                return folder.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = kinds,
                    Recursive = true,
                    IsVirtualItem = false,
                });
            default:
                return [];
        }
    }
}
