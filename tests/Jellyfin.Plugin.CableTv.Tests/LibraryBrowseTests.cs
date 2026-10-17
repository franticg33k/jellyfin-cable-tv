using System;
using System.Linq;
using Jellyfin.Plugin.CableTv.Library;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class LibraryBrowseTests
{
    private static IndexedTitle T(string name, TitleKind kind, int? year, string[]? genres = null, string? rating = null, float? score = null, string[]? studios = null, int addedDay = 1)
        => new(Guid.NewGuid(), kind, name, year)
        {
            Genres = genres ?? [],
            OfficialRating = rating,
            CommunityRating = score,
            Studios = studios ?? [],
            DateAdded = new DateTime(2026, 1, addedDay, 0, 0, 0, DateTimeKind.Utc),
        };

    private static LibraryIndex Library() => new(
    [
        T("The Matrix", TitleKind.Movie, 1999, ["Action", "Sci-Fi"], "R", 8.7f, ["Warner Bros."], 3),
        T("Matrix Reloaded", TitleKind.Movie, 2003, ["Action"], "R", 7.2f, ["Warner Bros."], 2),
        T("Friends", TitleKind.Series, 1994, ["Comedy"], "TV-14", 8.4f, ["NBC"], 5),
        T("Café Society", TitleKind.Movie, 2016, ["Comedy", "Drama"], "PG-13", 6.6f, null, 1),
        T("Thriller", TitleKind.MusicVideo, 1983, ["Music"]),
        T("No Year Film", TitleKind.Movie, null, ["Drama"]),
    ]);

    private static BrowseQuery Q(string? search = null, TitleKind[]? kinds = null, string[]? genres = null, string[]? studios = null, string[]? ratings = null, int? from = null, int? to = null, float? min = null, BrowseSort sort = BrowseSort.Name, int skip = 0, int take = 50)
        => new(search, kinds ?? [], genres ?? [], studios ?? [], ratings ?? [], from, to, min, sort, skip, take);

    private static string[] Names(BrowsePage page) => page.Items.Select(t => t.Name).ToArray();

    [Fact]
    public void DefaultsToMoviesAndShows()
    {
        var page = LibraryBrowse.Run(Library(), Q());
        Assert.Equal(5, page.Total);
        Assert.DoesNotContain("Thriller", Names(page));
    }

    [Fact]
    public void SearchIgnoresAccentsAndCaseAndNeedsEveryWord()
    {
        Assert.Equal(["Café Society"], Names(LibraryBrowse.Run(Library(), Q("cafe SOCIETY"))));
        Assert.Equal(["Matrix Reloaded", "The Matrix"], Names(LibraryBrowse.Run(Library(), Q("matrix"))));
        Assert.Empty(LibraryBrowse.Run(Library(), Q("matrix friends")).Items);
    }

    [Fact]
    public void FiltersCombine()
    {
        Assert.Equal(["Matrix Reloaded", "The Matrix"], Names(LibraryBrowse.Run(Library(), Q(genres: ["action"]))));
        Assert.Equal(["The Matrix"], Names(LibraryBrowse.Run(Library(), Q(genres: ["Action"], to: 2000))));
        Assert.Equal(["Friends"], Names(LibraryBrowse.Run(Library(), Q(kinds: [TitleKind.Series]))));
        Assert.Equal(["Friends"], Names(LibraryBrowse.Run(Library(), Q(studios: ["nbc"]))));
        Assert.Equal(["Café Society"], Names(LibraryBrowse.Run(Library(), Q(ratings: ["PG-13"]))));
        Assert.Equal(["Friends", "The Matrix"], Names(LibraryBrowse.Run(Library(), Q(min: 8f))));
    }

    [Fact]
    public void YearFiltersDropTitlesWithoutAYear()
        => Assert.DoesNotContain("No Year Film", Names(LibraryBrowse.Run(Library(), Q(from: 1900, to: 2100))));

    [Fact]
    public void SortsAndPages()
    {
        Assert.Equal(["Café Society", "Matrix Reloaded", "The Matrix", "Friends"], Names(LibraryBrowse.Run(Library(), Q(sort: BrowseSort.Year, take: 4))));
        Assert.Equal(["The Matrix", "Friends"], Names(LibraryBrowse.Run(Library(), Q(sort: BrowseSort.Rating, take: 2))));
        var byAdded = LibraryBrowse.Run(Library(), Q(sort: BrowseSort.Added, take: 2));
        Assert.Equal(["Friends", "The Matrix"], Names(byAdded));
        Assert.Equal(5, byAdded.Total);
        Assert.Equal(["Matrix Reloaded", "Café Society"], Names(LibraryBrowse.Run(Library(), Q(sort: BrowseSort.Added, skip: 2, take: 2))));
    }

    [Fact]
    public void FacetsCountMoviesAndShowsOnly()
    {
        var facets = LibraryBrowse.Facets(Library());
        Assert.DoesNotContain(facets.Genres, g => g.Key == "Music");
        Assert.Equal(2, facets.Genres.First(g => g.Key == "Action").Value);
        Assert.Equal(1994, facets.MinYear);
        Assert.Equal(2016, facets.MaxYear);
        Assert.Equal("Action", facets.Genres[0].Key);
    }
}
