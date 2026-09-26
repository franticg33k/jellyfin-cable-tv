using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Library;
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
/// <remarks>
/// Most sources are answered from a <see cref="LibraryIndex"/> shared by the whole rebuild (see <see cref="ResolveContext"/>).
/// Converting an item for the scheduler reads its media streams and chapters, so conversions are cached across rebuilds
/// and redone only when the item changes.
/// </remarks>
public class ContentPoolResolver
{
    /// <summary>
    /// Kinds of standalone items the library index holds; episodes are reached through their series.
    /// </summary>
    private static readonly BaseItemKind[] IndexedKinds = [BaseItemKind.Series, BaseItemKind.Movie, BaseItemKind.MusicVideo, BaseItemKind.Video];

    /// <summary>How long previews, suggestions and import checks reuse the last library index.</summary>
    private static readonly TimeSpan SharedContextLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, (DateTime Modified, PoolItem Item)> _converted = new();
    private SharedEntry? _shared;

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
    /// Starts a rebuild: channels resolved with the same context share one library scan.
    /// </summary>
    /// <returns>The context.</returns>
    public ResolveContext CreateContext() => new(BuildIndex);

    /// <summary>
    /// A recent context for interactive work (previews, suggestions, import checks), so clicking around the settings
    /// page doesn't rescan the library each time.
    /// </summary>
    /// <returns>The context.</returns>
    public ResolveContext SharedContext()
    {
        var shared = Volatile.Read(ref _shared);
        if (shared is not null && DateTime.UtcNow - shared.Created < SharedContextLifetime)
        {
            return shared.Context;
        }

        var context = CreateContext();
        Volatile.Write(ref _shared, new SharedEntry(DateTime.UtcNow, context));
        return context;
    }

    /// <summary>
    /// A context over the library as it is now, for explicit actions (suggestions, imports, exports) that should see
    /// metadata edited a moment ago. It becomes the shared context too.
    /// </summary>
    /// <returns>The context.</returns>
    public ResolveContext FreshContext()
    {
        var context = CreateContext();
        ShareContext(context);
        return context;
    }

    /// <summary>
    /// Offers a rebuild's context to interactive work.
    /// </summary>
    /// <param name="context">The context.</param>
    public void ShareContext(ResolveContext context) => Volatile.Write(ref _shared, new SharedEntry(DateTime.UtcNow, context));

    /// <summary>
    /// Forgets cached conversions of items no rebuild uses any more.
    /// </summary>
    /// <param name="context">The finished rebuild.</param>
    public void Trim(ResolveContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var id in _converted.Keys)
        {
            if (!context.UsedItems.ContainsKey(id))
            {
                _converted.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// Resolves the programme pool for a channel.
    /// </summary>
    /// <param name="channel">Channel definition.</param>
    /// <param name="context">Rebuild context; a fresh one when omitted.</param>
    /// <returns>The pool, in canonical order.</returns>
    public IReadOnlyList<PoolItem> Resolve(ChannelDefinition channel, ResolveContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var pool = Resolve(channel.Sources, ParseKinds(channel.ItemTypes), channel.AllowSpecials, context ?? CreateContext());
        _logger.LogDebug("Channel {Channel} resolved {Count} items", channel.Id, pool.Count);
        return pool;
    }

    /// <summary>
    /// Resolves a commercial pool.
    /// </summary>
    /// <param name="sources">Commercial sources.</param>
    /// <param name="context">Rebuild context; a fresh one when omitted.</param>
    /// <returns>The commercials.</returns>
    public IReadOnlyList<PoolItem> ResolveCommercials(IEnumerable<ContentSource> sources, ResolveContext? context = null)
        => Resolve(sources, CommercialKinds, allowSpecials: true, context ?? CreateContext());

    /// <summary>
    /// Counts what each title source of a channel matches, for import previews.
    /// </summary>
    /// <param name="titles">Lineup titles, "Title" or "Title (Year)".</param>
    /// <param name="context">Rebuild context.</param>
    /// <returns>The titles found and the ones missing.</returns>
    public (List<string> Found, List<string> Missing) MatchTitles(IEnumerable<string> titles, ResolveContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var found = new List<string>();
        var missing = new List<string>();
        foreach (var title in titles)
        {
            (context.Index.Match(title).Count > 0 ? found : missing).Add(title);
        }

        return (found, missing);
    }

    /// <summary>
    /// Checks which pinned episodes ("Show Title :: Episode Title") the library has, for import previews.
    /// </summary>
    /// <param name="values">Pinned episodes.</param>
    /// <param name="context">Rebuild context.</param>
    /// <returns>The episodes found and the ones missing.</returns>
    public (List<string> Found, List<string> Missing) MatchEpisodes(IEnumerable<string> values, ResolveContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var found = new List<string>();
        var missing = new List<string>();
        foreach (var value in values)
        {
            (PinnedEpisodes(value, context).Any() ? found : missing).Add(value);
        }

        return (found, missing);
    }

    /// <summary>
    /// The shows and movies a channel airs, for a lineup CSV export: its title sources as written, or else the series
    /// and movies its other sources resolve to.
    /// </summary>
    /// <param name="channel">Channel.</param>
    /// <param name="context">Rebuild context.</param>
    /// <returns>Titles with years, in canonical order.</returns>
    public IReadOnlyList<(string Title, int? Year)> TitlesOf(ChannelDefinition channel, ResolveContext context)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(context);
        var written = channel.Sources.Where(s => s.Type == ContentSourceType.Titles && !s.Exclude).SelectMany(s => s.Values).ToList();
        if (written.Count > 0)
        {
            return written.Select(TitleNormalizer.SplitYear).DistinctBy(t => (TitleNormalizer.Key(t.Title), t.Year)).ToList();
        }

        // Only which titles, not their media details, so no conversion to pool items.
        var kinds = ParseKinds(channel.ItemTypes);
        var titles = new List<(string Title, int? Year)>();
        var seen = new HashSet<Guid>();
        var excluded = channel.Sources.Where(s => s.Exclude)
            .SelectMany(s => ResolveSource(s, kinds, context))
            .Select(i => i.Id)
            .ToHashSet();
        foreach (var item in channel.Sources.Where(s => !s.Exclude).SelectMany(s => ResolveSource(s, kinds, context)))
        {
            if (excluded.Contains(item.Id))
            {
                continue;
            }

            var id = item is Episode episode ? episode.SeriesId : item.Id;
            if (seen.Add(id))
            {
                var indexed = context.Index.Get(id);
                titles.Add(indexed is not null
                    ? (indexed.Name, indexed.Year)
                    : ((item as Episode)?.SeriesName ?? item.Name ?? string.Empty, item.ProductionYear));
            }
        }

        return titles.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IReadOnlyList<PoolItem> Resolve(IEnumerable<ContentSource> sources, BaseItemKind[] kinds, bool allowSpecials, ResolveContext context)
    {
        var found = new Dictionary<Guid, (Video Video, int Weight)>();
        var excluded = new HashSet<Guid>();

        foreach (var source in sources)
        {
            var weight = Math.Clamp(source.Weight, 1, 10);
            foreach (var item in ResolveSource(source, kinds, context))
            {
                if (item is not Video video || !IsSchedulable(video, kinds, allowSpecials))
                {
                    continue;
                }

                if (source.Exclude)
                {
                    excluded.Add(video.Id);
                }
                else
                {
                    found[video.Id] = found.TryGetValue(video.Id, out var existing)
                        ? (video, Math.Max(existing.Weight, weight))
                        : (video, weight);
                }
            }
        }

        foreach (var id in excluded)
        {
            found.Remove(id);
        }

        foreach (var id in found.Keys)
        {
            context.UsedItems.TryAdd(id, 0);
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
        if (_converted.TryGetValue(video.Id, out var cached) && cached.Modified == video.DateModified)
        {
            return cached.Item.Weight == weight ? cached.Item : cached.Item with { Weight = weight };
        }

        var converted = Convert(video);
        _converted[video.Id] = (video.DateModified, converted);
        return weight == 1 ? converted : converted with { Weight = weight };
    }

    private PoolItem Convert(Video video)
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
            DateCreated = video.DateCreated == default ? null : DateTime.SpecifyKind(video.DateCreated, DateTimeKind.Utc),
            ChapterTicks = chapters,
            VideoCodec = videoStream?.Codec,
            Width = videoStream?.Width,
            Height = videoStream?.Height,
            HasAudio = streams.Any(s => s.Type == MediaStreamType.Audio),
        };
    }

    private LibraryIndex BuildIndex()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = IndexedKinds,
            Recursive = true,
            IsVirtualItem = false,
        });

        var titles = new List<IndexedTitle>(items.Count);
        foreach (var item in items)
        {
            if (item is Video { ExtraType: not null })
            {
                continue;
            }

            var kind = item switch
            {
                Series => TitleKind.Series,
                Movie => TitleKind.Movie,
                MusicVideo => TitleKind.MusicVideo,
                _ => TitleKind.Video,
            };
            titles.Add(new IndexedTitle(item.Id, kind, item.Name ?? string.Empty, item.ProductionYear)
            {
                Genres = item.Genres ?? [],
                Studios = item.Studios ?? [],
                Tags = item.Tags ?? [],
                OfficialRating = item.OfficialRating,
            });
        }

        _logger.LogInformation("Indexed {Count} series and videos for Cable TV", titles.Count);
        return new LibraryIndex(titles);
    }

    private IEnumerable<BaseItem> ResolveSource(ContentSource source, BaseItemKind[] kinds, ResolveContext context)
    {
        switch (source.Type)
        {
            case ContentSourceType.Library:
            case ContentSourceType.Items:
                return source.Ids.SelectMany(id => Expand(_libraryManager.GetItemById(id), kinds, context));

            case ContentSourceType.Collection:
            case ContentSourceType.Playlist:
                return source.Ids
                    .Select(_libraryManager.GetItemById)
                    .OfType<Folder>()
                    .SelectMany(folder => folder.GetLinkedChildren())
                    .SelectMany(child => Expand(child, kinds, context));

            case ContentSourceType.Genre:
                return FromIndex(context.Index.WithGenre(source.Values), kinds, context);

            case ContentSourceType.Decade:
                return FromIndex(context.Index.InYears(source.Values, decades: true), kinds, context);

            case ContentSourceType.Years:
                return FromIndex(context.Index.InYears(source.Values), kinds, context);

            case ContentSourceType.Studio:
                return FromIndex(context.Index.WithStudio(source.Values), kinds, context);

            case ContentSourceType.Tag:
                return FromIndex(context.Index.WithTag(source.Values), kinds, context);

            case ContentSourceType.Rating:
                return FromIndex(context.Index.WithRating(source.Values), kinds, context);

            case ContentSourceType.Keyword:
                return FromIndex(context.Index.WithKeyword(source.Values), kinds, context);

            case ContentSourceType.Titles:
                return FromIndex(source.Values.SelectMany(v => context.Index.Match(v)), kinds, context);

            case ContentSourceType.Episodes:
                return source.Values.SelectMany(v => PinnedEpisodes(v, context));

            default:
                _logger.LogWarning("Unknown content source type {Type}", source.Type);
                return [];
        }
    }

    /// <summary>
    /// Episodes named by "Show Title :: Episode Title" (compared on normalized keys).
    /// </summary>
    private IEnumerable<BaseItem> PinnedEpisodes(string value, ResolveContext context)
    {
        var parts = value.Split("::", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return [];
        }

        var episodeKey = TitleNormalizer.Key(parts[1]);
        return context.Index.Match(parts[0])
            .Where(t => t.Kind == TitleKind.Series)
            .SelectMany(t => Children(t.Id, [BaseItemKind.Episode], context))
            .Where(e => TitleNormalizer.Key(e.Name ?? string.Empty) == episodeKey);
    }

    private IEnumerable<BaseItem> FromIndex(IEnumerable<IndexedTitle> titles, BaseItemKind[] kinds, ResolveContext context)
    {
        foreach (var title in titles.DistinctBy(t => t.Id))
        {
            if (title.Kind == TitleKind.Series)
            {
                if (kinds.Contains(BaseItemKind.Episode))
                {
                    foreach (var episode in Children(title.Id, [BaseItemKind.Episode], context))
                    {
                        yield return episode;
                    }
                }
            }
            else if (_libraryManager.GetItemById(title.Id) is { } item)
            {
                yield return item;
            }
        }
    }

    private IReadOnlyList<BaseItem> Children(Guid folderId, BaseItemKind[] kinds, ResolveContext context)
    {
        if (context.Children.TryGetValue(folderId, out var cached))
        {
            return cached;
        }

        var children = _libraryManager.GetItemById(folderId) is Folder folder
            ? folder.GetItemList(new InternalItemsQuery { IncludeItemTypes = kinds, Recursive = true, IsVirtualItem = false })
            : [];
        context.Children[folderId] = children;
        return children;
    }

    private IEnumerable<BaseItem> Expand(BaseItem? item, BaseItemKind[] kinds, ResolveContext context)
    {
        switch (item)
        {
            case null:
                return [];
            case Video video:
                return [video];
            case Series series:
                return kinds.Contains(BaseItemKind.Episode) ? Children(series.Id, [BaseItemKind.Episode], context) : [];
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

    private sealed record SharedEntry(DateTime Created, ResolveContext Context);
}
