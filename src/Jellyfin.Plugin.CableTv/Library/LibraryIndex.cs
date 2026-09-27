using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.CableTv.Library;

/// <summary>
/// An in-memory snapshot of the library's series and standalone videos with inverted indexes, so channel sources are
/// resolved with dictionary lookups instead of a database query per source. Built once per rebuild and shared by every
/// channel.
/// </summary>
public sealed class LibraryIndex
{
    private readonly Dictionary<Guid, IndexedTitle> _byId;
    private readonly Dictionary<string, List<IndexedTitle>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IndexedTitle>> _byLooseKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IndexedTitle>> _byStudio = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IndexedTitle>> _byGenre = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IndexedTitle>> _byTag = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IndexedTitle>> _byRating = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IndexedTitle>> _byArtist = new(StringComparer.OrdinalIgnoreCase);
    private readonly (IndexedTitle Title, string Key)[] _keyed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryIndex"/> class.
    /// </summary>
    /// <param name="titles">Every series and standalone video in the library.</param>
    public LibraryIndex(IEnumerable<IndexedTitle> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var all = new List<IndexedTitle>();
        foreach (var raw in titles)
        {
            // A year in the name ("Doctor Who (2005)") stands in for missing metadata.
            var title = raw.Year is null && TitleNormalizer.SplitYear(raw.Name).Year is int year ? raw with { Year = year } : raw;
            all.Add(title);
        }

        Titles = all;
        _byId = all.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        _keyed = all.Select(t => (t, TitleNormalizer.Key(t.Name))).ToArray();
        foreach (var (title, key) in _keyed)
        {
            Add(_byKey, key, title);
            Add(_byLooseKey, key.Replace(" ", string.Empty, StringComparison.Ordinal), title);
            foreach (var studio in title.Studios)
            {
                Add(_byStudio, studio.Trim(), title);
            }

            foreach (var genre in title.Genres)
            {
                Add(_byGenre, genre.Trim(), title);
            }

            foreach (var tag in title.Tags)
            {
                Add(_byTag, tag.Trim(), title);
            }

            foreach (var artist in title.Artists)
            {
                Add(_byArtist, artist.Trim(), title);
            }

            if (!string.IsNullOrWhiteSpace(title.OfficialRating))
            {
                Add(_byRating, title.OfficialRating.Trim(), title);
            }
        }
    }

    /// <summary>Gets every indexed title.</summary>
    public IReadOnlyList<IndexedTitle> Titles { get; }

    /// <summary>Gets the studios and networks with how many titles each has.</summary>
    public IReadOnlyDictionary<string, List<IndexedTitle>> Studios => _byStudio;

    /// <summary>Gets the genres with their titles.</summary>
    public IReadOnlyDictionary<string, List<IndexedTitle>> Genres => _byGenre;

    /// <summary>Gets the ratings with their titles.</summary>
    public IReadOnlyDictionary<string, List<IndexedTitle>> Ratings => _byRating;

    /// <summary>Looks a title up by id.</summary>
    /// <param name="id">Item id.</param>
    /// <returns>The title, or null.</returns>
    public IndexedTitle? Get(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Finds library titles for a lineup entry like "Adventure Time" or "Charmed (1998)".
    /// </summary>
    /// <remarks>
    /// Exact keys win over loose ones. With a year, the closest title within two years wins (titles without a year
    /// count as a match); a remake decades apart is not a match. Without a year, every title with that name matches.
    /// </remarks>
    /// <param name="title">Title, optionally with "(Year)".</param>
    /// <param name="year">Year, when given separately (overrides one in <paramref name="title"/>).</param>
    /// <returns>The matching titles; empty when the library doesn't have it.</returns>
    public IReadOnlyList<IndexedTitle> Match(string title, int? year = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        var (bare, parsedYear) = TitleNormalizer.SplitYear(title);
        year ??= parsedYear;
        var key = TitleNormalizer.Key(bare);
        if (key.Length == 0)
        {
            return [];
        }

        if (!_byKey.TryGetValue(key, out var candidates)
            && !_byLooseKey.TryGetValue(key.Replace(" ", string.Empty, StringComparison.Ordinal), out candidates))
        {
            return [];
        }

        if (year is not int wanted)
        {
            return candidates;
        }

        int Distance(IndexedTitle t) => t.Year is int y ? Math.Abs(y - wanted) : 0;
        var best = candidates.Min(Distance);
        return best <= 2 ? candidates.Where(t => Distance(t) == best).ToList() : [];
    }

    /// <summary>Titles from any of the studios or networks.</summary>
    /// <param name="names">Studio or network names.</param>
    /// <returns>The titles.</returns>
    public IEnumerable<IndexedTitle> WithStudio(IEnumerable<string> names) => Lookup(_byStudio, names);

    /// <summary>Titles in any of the genres.</summary>
    /// <param name="names">Genre names.</param>
    /// <returns>The titles.</returns>
    public IEnumerable<IndexedTitle> WithGenre(IEnumerable<string> names) => Lookup(_byGenre, names);

    /// <summary>Titles with any of the tags.</summary>
    /// <param name="names">Tags.</param>
    /// <returns>The titles.</returns>
    public IEnumerable<IndexedTitle> WithTag(IEnumerable<string> names) => Lookup(_byTag, names);

    /// <summary>Albums by any of the artists.</summary>
    /// <param name="names">Artist names.</param>
    /// <returns>The albums.</returns>
    public IEnumerable<IndexedTitle> WithArtist(IEnumerable<string> names) => Lookup(_byArtist, names);

    /// <summary>Titles with any of the ratings.</summary>
    /// <param name="ratings">Official ratings.</param>
    /// <returns>The titles.</returns>
    public IEnumerable<IndexedTitle> WithRating(IEnumerable<string> ratings) => Lookup(_byRating, ratings);

    /// <summary>Titles from any of the years or ranges ("1994", "1985-1994", or a decade start like "1990" with <paramref name="decades"/>).</summary>
    /// <param name="values">Years or ranges.</param>
    /// <param name="decades">Whether a single year means the decade starting then.</param>
    /// <returns>The titles.</returns>
    public IEnumerable<IndexedTitle> InYears(IEnumerable<string> values, bool decades = false)
    {
        var ranges = values.Select(v => ParseRange(v, decades)).OfType<(int From, int To)>().ToArray();
        if (ranges.Length == 0)
        {
            return [];
        }

        return Titles.Where(t => t.Year is int y && ranges.Any(r => y >= r.From && y <= r.To));
    }

    /// <summary>Titles whose name contains any of the words or phrases, compared on normalized keys.</summary>
    /// <param name="words">Words or phrases.</param>
    /// <returns>The titles.</returns>
    public IEnumerable<IndexedTitle> WithKeyword(IEnumerable<string> words)
    {
        var keys = words.Select(TitleNormalizer.Key).Where(k => k.Length > 0).Select(k => " " + k + " ").ToArray();
        if (keys.Length == 0)
        {
            return [];
        }

        return _keyed.Where(k => keys.Any(w => (" " + k.Key + " ").Contains(w, StringComparison.Ordinal))).Select(k => k.Title);
    }

    /// <summary>
    /// Parses "1994", "1985-1994", or a decade start.
    /// </summary>
    /// <param name="value">Text.</param>
    /// <param name="decade">Whether a single year means its whole decade.</param>
    /// <returns>The range, or null.</returns>
    public static (int From, int To)? ParseRange(string value, bool decade = false)
    {
        var parts = (value ?? string.Empty).Split('-', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
        {
            return null;
        }

        if (parts.Length == 2)
        {
            return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to) && to >= from ? (from, to) : null;
        }

        return decade ? (from - (from % 10), from - (from % 10) + 9) : (from, from);
    }

    private static void Add(Dictionary<string, List<IndexedTitle>> index, string key, IndexedTitle title)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(title);
    }

    private static IEnumerable<IndexedTitle> Lookup(Dictionary<string, List<IndexedTitle>> index, IEnumerable<string> keys)
        => keys.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(k => index.TryGetValue(k, out var list) ? list : (IEnumerable<IndexedTitle>)[]);
}
