using System;
using System.Linq;
using Jellyfin.Plugin.CableTv.Library;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class LibraryIndexTests
{
    private static IndexedTitle Series(string name, int? year = null, string[]? studios = null, string[]? genres = null, string? rating = null)
        => new(Guid.NewGuid(), TitleKind.Series, name, year) { Studios = studios ?? [], Genres = genres ?? [], OfficialRating = rating };

    private static IndexedTitle Movie(string name, int? year = null, string[]? genres = null)
        => new(Guid.NewGuid(), TitleKind.Movie, name, year) { Genres = genres ?? [] };

    [Theory]
    [InlineData("The Office", "office")]
    [InlineData("Office, The", "office the")]
    [InlineData("Law & Order", "law and order")]
    [InlineData("Pokémon", "pokemon")]
    [InlineData("Bob's Burgers", "bobs burgers")]
    [InlineData("Charmed (1998)", "charmed")]
    [InlineData("  Spider-Man:  The Animated Series ", "spider man the animated series")]
    [InlineData("Disney+", "disney plus")]
    public void KeyNormalizesTitles(string title, string key) => Assert.Equal(key, TitleNormalizer.Key(title));

    [Theory]
    [InlineData("Cartoon Network", "cartoon_network")]
    [InlineData("HBO+", "hbo_plus")]
    [InlineData("A&E", "a_and_e")]
    [InlineData("Nick Jr.", "nick_jr")]
    [InlineData("Adult Swim's", "adult_swims")]
    public void SlugMakesFileNames(string name, string slug) => Assert.Equal(slug, TitleNormalizer.Slug(name));

    [Fact]
    public void SplitYearReadsTrailingYear()
    {
        Assert.Equal(("Doctor Who", 2005), TitleNormalizer.SplitYear("Doctor Who (2005)"));
        Assert.Equal(("1984", (int?)null), TitleNormalizer.SplitYear("1984"));
    }

    [Fact]
    public void MatchFindsTitlesDespiteFormatting()
    {
        var office = Series("The Office (US)", 2005);
        var index = new LibraryIndex([office, Series("Spiderman", 1994)]);

        Assert.Single(index.Match("Office (US)"));
        Assert.Single(index.Match("Spider-Man"));
        Assert.Empty(index.Match("Frasier"));
    }

    [Fact]
    public void YearPicksTheRightVersion()
    {
        var original = Series("Charmed", 1998);
        var reboot = Series("Charmed", 2018);
        var index = new LibraryIndex([original, reboot]);

        Assert.Equal(original.Id, Assert.Single(index.Match("Charmed", 1998)).Id);
        Assert.Equal(reboot.Id, Assert.Single(index.Match("Charmed (2017)")).Id);
        Assert.Empty(index.Match("Charmed", 1960));
        Assert.Equal(2, index.Match("Charmed").Count);
    }

    [Fact]
    public void YearInTheNameStandsInForMetadata()
    {
        var index = new LibraryIndex([Series("Doctor Who (2005)")]);
        Assert.Single(index.Match("Doctor Who", 2005));
        Assert.Empty(index.Match("Doctor Who", 1963));
    }

    [Fact]
    public void AttributeLookupsIgnoreCase()
    {
        var index = new LibraryIndex([
            Series("Seinfeld", 1989, studios: ["NBC"], genres: ["Comedy"], rating: "TV-PG"),
            Series("Friends", 1994, studios: ["NBC"], genres: ["Comedy"]),
            Movie("Alien", 1979, genres: ["Horror", "Science Fiction"]),
        ]);

        Assert.Equal(2, index.WithStudio(["nbc"]).Count());
        Assert.Equal(2, index.WithGenre(["comedy", "Comedy"]).Count());
        Assert.Single(index.WithRating(["tv-pg"]));
        Assert.Single(index.InYears(["1970"], decades: true));
        Assert.Equal(2, index.InYears(["1985-1995"]).Count());
        Assert.Empty(index.InYears(["nonsense"]));
    }

    [Fact]
    public void KeywordsMatchWholeWords()
    {
        var index = new LibraryIndex([Movie("A Christmas Story"), Movie("Christmastime Blues"), Series("Holiday Baking Championship")]);
        Assert.Single(index.WithKeyword(["christmas"]));
        Assert.Equal(2, index.WithKeyword(["christmas", "holiday"]).Count());
    }

    [Theory]
    [InlineData("1994", false, 1994, 1994)]
    [InlineData("1985 - 1994", false, 1985, 1994)]
    [InlineData("1994", true, 1990, 1999)]
    public void ParseRangeReadsYearsAndDecades(string text, bool decade, int from, int to)
        => Assert.Equal((from, to), LibraryIndex.ParseRange(text, decade));
}
