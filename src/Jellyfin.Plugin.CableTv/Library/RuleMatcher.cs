using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Library;

/// <summary>
/// Decides which library titles a <see cref="ContentRule"/> takes.
/// </summary>
/// <remarks>
/// The rule semantics follow Nostalgex's channel filter (MIT, github.com/chadMueller/Nostalgex), so its channel lineups
/// behave the same here: exclusions and gates first, then any one content match (title words, keywords, companies, or
/// studios and genres) qualifies.
/// </remarks>
public sealed class RuleMatcher
{
    // Niche genre families that stay on channels naming them, as bit flags.
    private const int Horror = 1, Reality = 2, Animation = 4, Documentary = 8, War = 16, Western = 32, Talk = 64;

    private static readonly Dictionary<string, int> NicheGenres = new(StringComparer.Ordinal)
    {
        ["horror"] = Horror,
        ["reality"] = Reality,
        ["game show"] = Reality,
        ["game-show"] = Reality,
        ["reality-tv"] = Reality,
        ["animation"] = Animation,
        ["animated"] = Animation,
        ["cartoon"] = Animation,
        ["documentary"] = Documentary,
        ["docuseries"] = Documentary,
        ["war"] = War,
        ["war & politics"] = War,
        ["western"] = Western,
        ["talk show"] = Talk,
        ["talk"] = Talk,
        ["news"] = Talk,
    };

    private static readonly HashSet<string> AdultRatings = new(["R", "NC-17", "TV-MA", "18", "18+", "X", "NR"], StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<IndexedTitle, Facts> FactsCache = new();

    private readonly ContentRule _rule;
    private readonly HashSet<string> _members;
    private readonly HashSet<string> _always;
    private readonly HashSet<string> _ratings;
    private readonly HashSet<string> _excludedRatings;
    private readonly HashSet<string> _keywords;
    private readonly HashSet<string> _excludedKeywords;
    private readonly string[] _genres;
    private readonly string[] _requireAllGenres;
    private readonly string[] _excludedGenres;
    private readonly string[] _keywordGenres;
    private readonly string[] _gatedGenres;
    private readonly string[] _studios;
    private readonly string[] _companies;
    private readonly bool _familySafe;
    private readonly int _allowedNiches;
    private readonly bool _realityOnly;
    private readonly PhraseSet? _titleWords;
    private readonly PhraseSet? _excludedTitleWords;
    private readonly PhraseSet? _episodeWords;
    private readonly DateTime _now;

    /// <summary>
    /// Initializes a new instance of the <see cref="RuleMatcher"/> class.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="nowUtc">The current time, for the recency conditions.</param>
    public RuleMatcher(ContentRule rule, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rule = rule;
        _now = nowUtc;
        _members = rule.Members.Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 0).ToHashSet(StringComparer.Ordinal);
        _always = rule.AlwaysTitles.Select(Haystack).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        _ratings = Set(rule.Ratings);
        _excludedRatings = Set(rule.ExcludeRatings);
        _keywords = Set(rule.Keywords);
        _excludedKeywords = Set(rule.ExcludeKeywords);
        _genres = Clean(rule.Genres);
        _requireAllGenres = Clean(rule.RequireAllGenres);
        _excludedGenres = Clean(rule.ExcludeGenres);
        _keywordGenres = Clean(rule.KeywordGenres);
        _gatedGenres = Clean(rule.KeywordGatedGenres);
        _studios = Clean(rule.Studios);
        _companies = Clean(rule.Companies);
        _familySafe = rule.Kids || _genres.Contains("family", StringComparer.Ordinal);
        var named = NicheFlags(_genres);
        _allowedNiches = _studios.Length > 0 && _genres.Length == 0 ? ~0 : named;
        if (rule.Kids || rule.TitleWords.Length > 0)
        {
            _allowedNiches |= Animation;
        }

        _realityOnly = (named & Reality) != 0;
        _titleWords = PhraseSet.Create(rule.TitleWords);
        _excludedTitleWords = PhraseSet.Create(rule.ExcludeTitleWords);
        _episodeWords = PhraseSet.Create(rule.EpisodeWords);
    }

    /// <summary>Gets a value indicating whether the rule picks episodes by name.</summary>
    public bool FiltersEpisodes => _episodeWords is not null;

    /// <summary>
    /// Whether an episode's name passes the rule's episode condition.
    /// </summary>
    /// <param name="episodeName">Episode name.</param>
    /// <returns>True when it airs.</returns>
    public bool MatchesEpisode(string? episodeName)
        => _episodeWords is null || (!string.IsNullOrEmpty(episodeName) && _episodeWords.IsMatch(Tokens(episodeName)));

    /// <summary>
    /// Whether a title qualifies.
    /// </summary>
    /// <param name="title">Library title.</param>
    /// <returns>True when it does.</returns>
    public bool Matches(IndexedTitle title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var r = _rule;
        var facts = FactsCache.GetValue(title, t => new Facts(t));
        var name = facts.Name;
        var words = facts.Words;
        var genres = facts.Genres;
        var rating = facts.Rating;

        if (_familySafe && AdultRatings.Contains(rating))
        {
            return false;
        }

        if (r.LockGenres && !PassesGenreLocks(facts.Niches))
        {
            return false;
        }

        if (_always.Contains(name))
        {
            return true;
        }

        if (_members.Count > 0 && facts.MemberKey is { } key && _members.Contains(key))
        {
            return PassesYears(title)
                   && (_titleWords is null || _titleWords.IsMatch(words))
                   && !(_excludedTitleWords?.IsMatch(words) ?? false)
                   && !AnyContains(genres, _excludedGenres);
        }

        if (r.MembersOnly)
        {
            return false;
        }

        if (_excludedTitleWords?.IsMatch(words) ?? false)
        {
            return false;
        }

        if (!PassesYears(title))
        {
            return false;
        }

        if (_ratings.Count > 0 && !(rating.Length == 0 ? r.AllowUnrated : _ratings.Contains(rating)))
        {
            return false;
        }

        if (rating.Length > 0 && _excludedRatings.Contains(rating))
        {
            return false;
        }

        if (title.Minutes is int minutes && ((r.MinMinutes is int min && minutes < min) || (r.MaxMinutes is int max && minutes > max)))
        {
            return false;
        }

        if (AnyContains(genres, _excludedGenres))
        {
            return false;
        }

        // Excluded keywords only count when the item has keywords at all.
        var tags = facts.Tags;
        if (_excludedKeywords.Count > 0 && tags.Any(_excludedKeywords.Contains))
        {
            return false;
        }

        if (!PassesGates(title))
        {
            return false;
        }

        if (_requireAllGenres.Any(want => !genres.Any(g => g.Contains(want, StringComparison.Ordinal))))
        {
            return false;
        }

        var hasTitleWords = _titleWords is not null;
        var hasKeywords = _keywords.Count > 0;
        var hasStudios = _studios.Length > 0;
        var hasCompanies = _companies.Length > 0;
        var hasGenres = _genres.Length > 0;
        var titleMatch = hasTitleWords && _titleWords!.IsMatch(words);

        // A title list on its own is the whole lineup.
        if (hasTitleWords && !hasKeywords && !hasStudios && !hasCompanies)
        {
            return titleMatch;
        }

        if (!hasTitleWords && !hasKeywords && !hasStudios && !hasCompanies && !hasGenres)
        {
            return true;
        }

        var keywordMatch = hasKeywords
                           && tags.Any(_keywords.Contains)
                           && (_keywordGenres.Length == 0 || AnyContains(genres, _keywordGenres));
        if (titleMatch || keywordMatch)
        {
            return true;
        }

        var studios = facts.Studios;
        if (hasCompanies && AnyContains(studios, _companies))
        {
            return true;
        }

        var genreMatch = AnyContains(genres, _genres);
        if (hasStudios)
        {
            return AnyContains(studios, _studios) && (!hasGenres || genreMatch);
        }

        if (!hasGenres || !genreMatch)
        {
            return false;
        }

        // Broad genres need a keyword too, so a rock film doesn't land on a musicals channel.
        return _gatedGenres.Length == 0 || !genres.Any(g => _gatedGenres.Contains(g, StringComparer.Ordinal));
    }

    /// <summary>
    /// Lowercases a title and drops a trailing " (year)".
    /// </summary>
    /// <param name="title">Title.</param>
    /// <returns>The comparable form.</returns>
    internal static string Haystack(string? title)
        => TitleNormalizer.SplitYear((title ?? string.Empty).ToLowerInvariant()).Title;

    private static HashSet<string> Set(IEnumerable<string> values)
        => values.Select(v => v.Trim()).Where(v => v.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string[] Clean(IEnumerable<string> values)
        => values.Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0).ToArray();

    private static bool AnyContains(string[] haystack, string[] wanted)
    {
        foreach (var h in haystack)
        {
            foreach (var w in wanted)
            {
                if (h.Contains(w, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Splits text into lowercase words: runs of letters and digits.
    /// </summary>
    private static string[] Tokens(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (isWord && start < 0)
            {
                start = i;
            }
            else if (!isWord && start >= 0)
            {
                words.Add(text[start..i].ToLowerInvariant());
                start = -1;
            }
        }

        return words.ToArray();
    }

    private static int NicheFlags(string[] genres)
    {
        var flags = 0;
        foreach (var genre in genres)
        {
            flags |= NicheGenres.GetValueOrDefault(genre);
        }

        return flags;
    }

    // A reality channel takes only reality.
    private bool PassesGenreLocks(int niches)
        => (niches & ~_allowedNiches) == 0 && (!_realityOnly || (niches & Reality) != 0);

    private bool PassesYears(IndexedTitle title)
        => title.Year is not int year
           || ((_rule.YearFrom is not int from || year >= from) && (_rule.YearTo is not int to || year <= to));

    private bool PassesGates(IndexedTitle title)
    {
        if (_rule.MinRating is double min && (title.CommunityRating is not float score || score < min))
        {
            return false;
        }

        if (_rule.AddedWithinDays is int days && days > 0 && (title.DateAdded is not DateTime added || added < _now.AddDays(-days)))
        {
            return false;
        }

        if (_rule.ReleasedWithinMonths is int months && months > 0)
        {
            if (title.PremiereDate is DateTime released)
            {
                return released >= _now.AddMonths(-months);
            }

            if (title.Year is int year)
            {
                return year >= _now.Year - (((months + 11) / 12) - 1);
            }
        }

        return true;
    }

    /// <summary>
    /// A title's metadata in the form rules compare, worked out once per title.
    /// </summary>
    private sealed class Facts
    {
        public Facts(IndexedTitle title)
        {
            Name = Haystack(title.Kind == TitleKind.MusicVideo && title.Artists.Length > 0 ? title.Artists[0] + " " + title.Name : title.Name);
            Words = Tokens(Name);
            Genres = Clean(title.Genres);
            Niches = NicheFlags(Genres);
            MemberKey = title.MemberKey;
            Studios = Clean(title.Studios);
            Tags = title.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
            Rating = title.OfficialRating?.Trim() ?? string.Empty;
        }

        public string Name { get; }

        public string[] Words { get; }

        public int Niches { get; }

        public string? MemberKey { get; }

        public string[] Genres { get; }

        public string[] Studios { get; }

        public string[] Tags { get; }

        public string Rating { get; }
    }

    /// <summary>
    /// Words and phrases matched as whole words ("elf" matches "Elf (2003)", not "Shelf"), indexed by their first word.
    /// </summary>
    private sealed class PhraseSet
    {
        private readonly Dictionary<string, List<string[]>> _byFirst = new(StringComparer.Ordinal);

        private PhraseSet()
        {
        }

        public static PhraseSet? Create(IEnumerable<string> phrases)
        {
            var set = new PhraseSet();
            foreach (var phrase in phrases.Select(Tokens).Where(t => t.Length > 0))
            {
                if (!set._byFirst.TryGetValue(phrase[0], out var list))
                {
                    list = [];
                    set._byFirst[phrase[0]] = list;
                }

                list.Add(phrase);
            }

            return set._byFirst.Count == 0 ? null : set;
        }

        public bool IsMatch(string[] words)
        {
            for (var i = 0; i < words.Length; i++)
            {
                if (!_byFirst.TryGetValue(words[i], out var phrases))
                {
                    continue;
                }

                foreach (var phrase in phrases)
                {
                    if (phrase.Length <= words.Length - i && phrase.AsSpan().SequenceEqual(words.AsSpan(i, phrase.Length)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
