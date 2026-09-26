using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.CableTv.Packs;

/// <summary>
/// A channel the library could fill.
/// </summary>
/// <param name="Group">Networks, TV genres, Movie genres, Decades, Kids, Holidays or Collections.</param>
/// <param name="Channel">The channel, ready to add.</param>
/// <param name="Titles">How many shows or movies it would draw from.</param>
/// <param name="Exists">Whether a channel with this id or name is already configured.</param>
public sealed record ChannelSuggestion(
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("channel")] ChannelDefinition Channel,
    [property: JsonPropertyName("titles")] int Titles,
    [property: JsonPropertyName("exists")] bool Exists);

/// <summary>
/// Proposes channels from what the library holds: one per TV network, TV and movie genre, decade, and collection, plus
/// kids and holiday channels. Everything is counted from the shared library index, so it is cheap to call.
/// </summary>
public class ChannelSuggester
{
    private const int MaxPerGroup = 40;

    private static readonly Dictionary<string, string> TvGenreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Animation"] = "Cartoons",
        ["Comedy"] = "Sitcoms",
        ["Kids"] = "Kids TV",
        ["Children"] = "Kids TV",
        ["Documentary"] = "Documentaries",
        ["Reality"] = "Reality TV",
        ["Sci-Fi & Fantasy"] = "Sci-Fi & Fantasy",
        ["Science Fiction"] = "Sci-Fi",
        ["Action & Adventure"] = "Action",
        ["Crime"] = "Crime TV",
        ["Mystery"] = "Mystery TV",
        ["Drama"] = "Drama TV",
        ["Soap"] = "Soaps",
        ["Talk"] = "Talk Shows",
        ["News"] = "News",
        ["Western"] = "Westerns",
        ["Game Show"] = "Game Shows",
        ["Anime"] = "Anime",
    };

    private static readonly string[] KidsRatings = ["TV-Y", "TV-Y7", "TV-Y7-FV", "TV-G", "G"];

    private static readonly string[] ChristmasWords = ["christmas", "xmas", "holiday", "santa", "grinch", "rudolph", "frosty", "nutcracker", "scrooge", "elf"];

    private static readonly string[] HalloweenWords = ["halloween", "haunted", "haunting", "ghost", "ghosts", "witch", "witches", "vampire", "zombie", "zombies", "monster", "monsters", "pumpkin", "spooky"];

    private readonly ContentPoolResolver _resolver;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelSuggester"/> class.
    /// </summary>
    /// <param name="resolver">Pool resolver.</param>
    /// <param name="libraryManager">Library manager.</param>
    public ChannelSuggester(ContentPoolResolver resolver, ILibraryManager libraryManager)
    {
        _resolver = resolver;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Suggests channels.
    /// </summary>
    /// <param name="config">Current configuration.</param>
    /// <param name="minTitles">Fewest shows or movies a channel needs (at least 2).</param>
    /// <returns>The suggestions, grouped, largest first within each group.</returns>
    public IReadOnlyList<ChannelSuggestion> Suggest(PluginConfiguration config, int minTitles = 3)
    {
        ArgumentNullException.ThrowIfNull(config);
        var min = Math.Max(minTitles, 2);
        var index = _resolver.FreshContext().Index;
        var numbers = new ChannelDefaults.NumberAllocator(config.Channels);
        var ids = config.Channels.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = config.Channels.Select(c => c.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<ChannelSuggestion>();
        var suggestedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string group, string name, int from, int titles, Action<ChannelDefinition>? tweak, params ContentSource[] sources)
        {
            var id = ChannelDefaults.IdFor(name);
            if (!suggestedIds.Add(id))
            {
                return;
            }

            var exists = ids.Contains(id) || names.Contains(name);
            var channel = ChannelDefaults.Create(name, exists ? string.Empty : numbers.Take(null, from), config.CommercialSources.Length > 0, sources);
            tweak?.Invoke(channel);
            suggestions.Add(new ChannelSuggestion(group, channel, titles, exists));
        }

        static ContentSource Source(ContentSourceType type, params string[] values) => new() { Type = type, Values = values };

        // TV networks. Jellyfin's TMDB provider stores a series' original network among its studios.
        foreach (var (studio, count) in Top(index.Studios, TitleKind.Series, min))
        {
            Add("Networks", studio, 100, count, c =>
            {
                c.LogoUrl = "studio:" + studio;
                c.ItemTypes = ["Episode"];
                c.Premieres = new PremiereDefinition { Enabled = true, Time = "20:00", WithinDays = 7 };
            }, Source(ContentSourceType.Studio, studio));
        }

        foreach (var (genre, count) in Top(index.Genres, TitleKind.Series, min))
        {
            Add("TV genres", TvGenreNames.GetValueOrDefault(genre) ?? genre + " TV", 200, count, c => c.ItemTypes = ["Episode"], Source(ContentSourceType.Genre, genre));
        }

        foreach (var (genre, count) in Top(index.Genres, TitleKind.Movie, Math.Max(min, 5)))
        {
            Add("Movie genres", genre + " Movies", 300, count, MovieChannel, Source(ContentSourceType.Genre, genre));
        }

        foreach (var (decade, count, kind) in Decades(index, min))
        {
            var label = decade.ToString(CultureInfo.InvariantCulture);
            var shortLabel = (decade % 100).ToString("00", CultureInfo.InvariantCulture) + "s";
            if (kind == TitleKind.Series)
            {
                Add("Decades", shortLabel + " TV", 400, count, c => c.ItemTypes = ["Episode"], Source(ContentSourceType.Decade, label));
            }
            else
            {
                Add("Decades", shortLabel + " Movies", 450, count, MovieChannel, Source(ContentSourceType.Decade, label));
            }
        }

        var kids = index.WithRating(KidsRatings).Where(t => t.Kind is TitleKind.Series or TitleKind.Movie).DistinctBy(t => t.Id).Count();
        if (kids >= min)
        {
            Add("Kids", "Kids", 500, kids, null, Source(ContentSourceType.Rating, KidsRatings));
        }

        var christmas = index.WithKeyword(ChristmasWords).Count();
        if (christmas >= 2)
        {
            Add("Holidays", "Holiday Classics", 600, christmas, c => c.Sorting = ChannelSorting.Random, Source(ContentSourceType.Keyword, ChristmasWords));
        }

        var halloween = index.WithKeyword(HalloweenWords).Count();
        if (halloween >= 2)
        {
            Add("Holidays", "Halloween", 600, halloween, c => c.Sorting = ChannelSorting.Random, Source(ContentSourceType.Keyword, HalloweenWords));
        }

        foreach (var (collection, count) in Collections(min))
        {
            Add("Collections", collection.Name, 700, count, c =>
            {
                c.Sorting = ChannelSorting.Cyclic;
                c.ItemTypes = ["Episode", "Movie", "MusicVideo", "Video"];
            }, new ContentSource { Type = ContentSourceType.Collection, Ids = [collection.Id] });
        }

        return suggestions;
    }

    private static void MovieChannel(ChannelDefinition channel)
    {
        channel.ItemTypes = ["Movie"];
        channel.Sorting = ChannelSorting.Random;
    }

    private static IEnumerable<(string Name, int Count)> Top(IReadOnlyDictionary<string, List<IndexedTitle>> index, TitleKind kind, int min)
        => index
            .Select(e => (Name: e.Key, Count: e.Value.Where(t => t.Kind == kind).DistinctBy(t => t.Id).Count()))
            .Where(e => e.Count >= min)
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPerGroup);

    private static IEnumerable<(int Decade, int Count, TitleKind Kind)> Decades(LibraryIndex index, int min)
        => index.Titles
            .Where(t => t.Year is not null && t.Kind is TitleKind.Series or TitleKind.Movie)
            .GroupBy(t => (Decade: t.Year!.Value - (t.Year.Value % 10), t.Kind))
            .Select(g => (g.Key.Decade, Count: g.Count(), g.Key.Kind))
            .Where(g => g.Count >= (g.Kind == TitleKind.Movie ? Math.Max(min, 5) : min))
            .OrderBy(g => g.Kind)
            .ThenBy(g => g.Decade);

    private IEnumerable<(BaseItem Collection, int Count)> Collections(int min)
        => _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true, Limit = 200 })
            .OfType<BoxSet>()
            .Select(b => ((BaseItem)b, Count: b.LinkedChildren.Length))
            .Where(b => b.Count >= min)
            .OrderByDescending(b => b.Count)
            .Take(MaxPerGroup);
}
