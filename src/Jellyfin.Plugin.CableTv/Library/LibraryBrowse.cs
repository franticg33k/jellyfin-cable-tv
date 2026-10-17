using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.CableTv.Library;

/// <summary>
/// How the library browser orders its results.
/// </summary>
public enum BrowseSort
{
    /// <summary>By title.</summary>
    Name = 0,

    /// <summary>Newest release first.</summary>
    Year = 1,

    /// <summary>Best rated first.</summary>
    Rating = 2,

    /// <summary>Most recently added first.</summary>
    Added = 3,
}

/// <summary>
/// What the library browser is looking for. Empty lists and null values don't filter.
/// </summary>
/// <param name="Search">Words in the title (accents and punctuation are ignored).</param>
/// <param name="Kinds">Kinds to show; empty means movies and shows.</param>
/// <param name="Genres">Any of these genres.</param>
/// <param name="Studios">Any of these studios or networks.</param>
/// <param name="Ratings">Any of these official ratings.</param>
/// <param name="YearFrom">Earliest year.</param>
/// <param name="YearTo">Latest year.</param>
/// <param name="MinRating">Lowest community rating.</param>
/// <param name="Sort">Order.</param>
/// <param name="Skip">Results to skip, for paging.</param>
/// <param name="Take">Results to return.</param>
public sealed record BrowseQuery(
    string? Search,
    IReadOnlyList<TitleKind> Kinds,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> Ratings,
    int? YearFrom,
    int? YearTo,
    float? MinRating,
    BrowseSort Sort,
    int Skip,
    int Take);

/// <summary>
/// One page of browser results.
/// </summary>
/// <param name="Total">How many titles match in all.</param>
/// <param name="Items">The page.</param>
public sealed record BrowsePage(int Total, IReadOnlyList<IndexedTitle> Items);

/// <summary>
/// The values the browser's filters offer.
/// </summary>
/// <param name="Genres">Genres with their title counts, most used first.</param>
/// <param name="Studios">Studios and networks with counts, most used first (the largest 200).</param>
/// <param name="Ratings">Official ratings with counts.</param>
/// <param name="MinYear">Earliest year in the library.</param>
/// <param name="MaxYear">Latest year in the library.</param>
public sealed record BrowseFacets(
    IReadOnlyList<KeyValuePair<string, int>> Genres,
    IReadOnlyList<KeyValuePair<string, int>> Studios,
    IReadOnlyList<KeyValuePair<string, int>> Ratings,
    int? MinYear,
    int? MaxYear);

/// <summary>
/// Searches and filters the library's titles, for picking individual movies and shows.
/// </summary>
public static class LibraryBrowse
{
    /// <summary>The most results one page may hold.</summary>
    public const int MaxPage = 120;

    /// <summary>
    /// Finds the titles that match a query.
    /// </summary>
    /// <param name="index">The library.</param>
    /// <param name="query">What to look for.</param>
    /// <returns>The requested page and the total.</returns>
    public static BrowsePage Run(LibraryIndex index, BrowseQuery query)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(query);

        var kinds = query.Kinds.Count > 0 ? query.Kinds.ToHashSet() : [TitleKind.Movie, TitleKind.Series];
        var words = (query.Search is { Length: > 0 } s ? TitleNormalizer.Key(s) : string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var genres = new HashSet<string>(query.Genres, StringComparer.OrdinalIgnoreCase);
        var studios = new HashSet<string>(query.Studios, StringComparer.OrdinalIgnoreCase);
        var ratings = new HashSet<string>(query.Ratings, StringComparer.OrdinalIgnoreCase);

        bool Matches(IndexedTitle t)
        {
            if (!kinds.Contains(t.Kind)
                || (genres.Count > 0 && !t.Genres.Any(genres.Contains))
                || (studios.Count > 0 && !t.Studios.Any(studios.Contains))
                || (ratings.Count > 0 && (t.OfficialRating is null || !ratings.Contains(t.OfficialRating))))
            {
                return false;
            }

            if ((query.YearFrom is int from && (t.Year is not int y1 || y1 < from))
                || (query.YearTo is int to && (t.Year is not int y2 || y2 > to))
                || (query.MinRating is float min && (t.CommunityRating is not float r || r < min)))
            {
                return false;
            }

            if (words.Length == 0)
            {
                return true;
            }

            var key = TitleNormalizer.Key(t.Name);
            return words.All(w => key.Contains(w, StringComparison.Ordinal));
        }

        IEnumerable<IndexedTitle> found = index.Titles.Where(Matches);
        found = query.Sort switch
        {
            BrowseSort.Year => found.OrderByDescending(t => t.Year ?? 0).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            BrowseSort.Rating => found.OrderByDescending(t => t.CommunityRating ?? 0).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            BrowseSort.Added => found.OrderByDescending(t => t.DateAdded ?? DateTime.MinValue).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
            _ => found.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Year ?? 0),
        };

        var all = found.ToList();
        var page = all.Skip(Math.Max(query.Skip, 0)).Take(Math.Clamp(query.Take, 1, MaxPage)).ToList();
        return new BrowsePage(all.Count, page);
    }

    /// <summary>
    /// Lists what the filters can offer, from movies and shows only.
    /// </summary>
    /// <param name="index">The library.</param>
    /// <returns>The filter values.</returns>
    public static BrowseFacets Facets(LibraryIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var titles = index.Titles.Where(t => t.Kind is TitleKind.Movie or TitleKind.Series).ToList();

        static List<KeyValuePair<string, int>> Count(IEnumerable<string> values, int limit)
            => values.Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(g => new KeyValuePair<string, int>(g.First(), g.Count()))
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();

        var years = titles.Where(t => t.Year is > 0).Select(t => t.Year!.Value).ToList();
        return new BrowseFacets(
            Count(titles.SelectMany(t => t.Genres), 100),
            Count(titles.SelectMany(t => t.Studios), 200),
            Count(titles.Select(t => t.OfficialRating ?? string.Empty), 40),
            years.Count > 0 ? years.Min() : null,
            years.Count > 0 ? years.Max() : null);
    }
}
