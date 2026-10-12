using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Library;
using Jellyfin.Plugin.CableTv.Packs;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class RuleTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);

    private static IndexedTitle Movie(string name, int? year, params string[] genres)
        => new(Guid.NewGuid(), TitleKind.Movie, name, year) { Genres = genres };

    private static IndexedTitle Show(string name, int? year, params string[] genres)
        => new(Guid.NewGuid(), TitleKind.Series, name, year) { Genres = genres };

    private static bool Matches(ContentRule rule, IndexedTitle title) => new RuleMatcher(rule, Now).Matches(title);

    [Fact]
    public void GenresYearsAndExclusions_AllApply()
    {
        var rule = new ContentRule { Genres = ["Comedy"], ExcludeGenres = ["Animation"], YearFrom = 1980, YearTo = 1999 };

        Assert.True(Matches(rule, Movie("Ghostbusters", 1984, "Comedy", "Fantasy")));
        Assert.True(Matches(rule, Movie("Romantic thing", 1990, "Romantic Comedy")));
        Assert.False(Matches(rule, Movie("Superbad", 2007, "Comedy")));
        Assert.False(Matches(rule, Movie("Aladdin", 1992, "Animation", "Comedy")));
        Assert.False(Matches(rule, Movie("Heat", 1995, "Crime")));
        Assert.True(Matches(rule, Movie("Undated", null, "Comedy")));
    }

    [Fact]
    public void Ratings_AllowUnratedOnlyWhenAsked()
    {
        var rule = new ContentRule { Ratings = ["G", "TV-Y"] };
        var unrated = Movie("Home video", 1990);

        Assert.True(Matches(rule, Movie("Kid film", 1990) with { OfficialRating = "G" }));
        Assert.False(Matches(rule, Movie("Grown-up film", 1990) with { OfficialRating = "R" }));
        Assert.False(Matches(rule, unrated));
        rule.AllowUnrated = true;
        Assert.True(Matches(rule, unrated));
    }

    [Fact]
    public void TitleWordsAlone_AreTheWholeLineup()
    {
        var rule = new ContentRule { Genres = ["Animation"], TitleWords = ["The Simpsons", "Futurama"] };

        Assert.True(Matches(rule, Show("The Simpsons (1989)", 1989, "Comedy")));
        Assert.False(Matches(rule, Show("Arthur", 1996, "Animation")));
        Assert.False(Matches(rule, Show("Futuramas", 1999, "Animation")));
    }

    [Fact]
    public void Keywords_AddToGenres_AndHonourTheirGate()
    {
        var rule = new ContentRule { Genres = ["Romance"], Keywords = ["high school"], KeywordGenres = ["Comedy", "Drama"] };

        Assert.True(Matches(rule, Movie("Love story", 2001, "Romance")));
        Assert.True(Matches(rule, Movie("Clueless", 1995, "Comedy") with { Tags = ["High School"] }));
        Assert.False(Matches(rule, Movie("Slasher", 1996, "Thriller") with { Tags = ["high school"] }));
    }

    [Fact]
    public void KeywordGatedGenres_NeedAKeyword()
    {
        var rule = new ContentRule { Genres = ["Musical", "Music"], Keywords = ["musical"], KeywordGatedGenres = ["Music"] };

        Assert.True(Matches(rule, Movie("Grease", 1978, "Musical")));
        Assert.False(Matches(rule, Movie("Rock doc", 2001, "Music")));
        Assert.True(Matches(rule, Movie("Rock opera", 2001, "Music") with { Tags = ["musical"] }));
    }

    [Fact]
    public void StudiosNeedGenres_CompaniesStandAlone()
    {
        var studioAndGenre = new ContentRule { Studios = ["Disney"], Genres = ["Animation"] };
        Assert.True(Matches(studioAndGenre, Movie("Aladdin", 1992, "Animation") with { Studios = ["Walt Disney Pictures"] }));
        Assert.False(Matches(studioAndGenre, Movie("Flubber", 1997, "Comedy") with { Studios = ["Walt Disney Pictures"] }));

        var companies = new ContentRule { Genres = ["Drama"], Companies = ["HBO"] };
        Assert.True(Matches(companies, Show("Veep", 2012, "Comedy") with { Studios = ["HBO"] }));
    }

    [Fact]
    public void LockedGenres_StayOnTheirOwnChannels()
    {
        var comedy = new ContentRule { Genres = ["Comedy"], LockGenres = true };
        Assert.False(Matches(comedy, Movie("Scary Movie", 2000, "Comedy", "Horror")));
        Assert.False(Matches(comedy, Movie("Toon", 2000, "Comedy", "Animation")));

        var kids = new ContentRule { Genres = ["Comedy"], LockGenres = true, Kids = true };
        Assert.True(Matches(kids, Movie("Toon", 2000, "Comedy", "Animation")));
        Assert.False(Matches(kids, Movie("Edgy", 2000, "Comedy") with { OfficialRating = "R" }));

        var reality = new ContentRule { Genres = ["Reality"], LockGenres = true };
        Assert.False(Matches(reality, Show("Drama", 2000, "Drama")));
    }

    [Fact]
    public void Members_QualifyOnTheirOwn_UnlessExcluded()
    {
        var rule = new ContentRule { Genres = ["Western"], Members = ["movie:346"], ExcludeGenres = ["Animation"], YearTo = 1999 };
        var samurai = Movie("Seven Samurai", 1954, "Action", "Drama") with { TmdbId = "346" };

        Assert.True(Matches(rule, samurai));
        Assert.False(Matches(rule, samurai with { Year = 2005 }));
        Assert.False(Matches(rule, samurai with { Genres = ["Animation"] }));
        Assert.False(Matches(rule, Show("Seven Samurai", 1954) with { TmdbId = "346" }));

        rule.MembersOnly = true;
        Assert.False(Matches(rule, Movie("Unforgiven", 1992, "Western")));
        Assert.True(Matches(rule, samurai));
    }

    [Fact]
    public void AlwaysTitles_SkipTheOtherConditions()
    {
        var rule = new ContentRule { Genres = ["Anime"], MembersOnly = true, AlwaysTitles = ["Pokémon"] };
        Assert.True(Matches(rule, Show("Pokémon (1997)", 1997, "Kids")));
    }

    [Fact]
    public void Gates_RejectMissingData()
    {
        Assert.False(Matches(new ContentRule { MinRating = 7 }, Movie("Unrated", 2000)));
        Assert.True(Matches(new ContentRule { MinRating = 7 }, Movie("Good", 2000) with { CommunityRating = 7.5f }));
        Assert.False(Matches(new ContentRule { AddedWithinDays = 14 }, Movie("Old", 2000) with { DateAdded = Now.AddDays(-30) }));
        Assert.True(Matches(new ContentRule { AddedWithinDays = 14 }, Movie("New", 2000) with { DateAdded = Now.AddDays(-3) }));
        Assert.False(Matches(new ContentRule { ReleasedWithinMonths = 6 }, Movie("Old", 2026) with { PremiereDate = Now.AddMonths(-9) }));
        Assert.False(Matches(new ContentRule { MinMinutes = 60 }, Movie("Short", 2000) with { Minutes = 22 }));
    }

    [Fact]
    public void EpisodeWords_PickEpisodesByName()
    {
        var matcher = new RuleMatcher(new ContentRule { EpisodeWords = ["Christmas", "Holiday"] }, Now);
        Assert.True(matcher.FiltersEpisodes);
        Assert.True(matcher.MatchesEpisode("The One with the Holiday Armadillo"));
        Assert.False(matcher.MatchesEpisode("The One with the Thumb"));
        Assert.False(new RuleMatcher(new ContentRule(), Now).FiltersEpisodes);
    }

    [Fact]
    public void MusicVideos_MatchOnArtistAndTitle()
    {
        var rule = new ContentRule { TitleWords = ["queen"] };
        var video = new IndexedTitle(Guid.NewGuid(), TitleKind.MusicVideo, "Bohemian Rhapsody", 1975) { Artists = ["Queen"] };
        Assert.True(Matches(rule, video));
    }

    [Fact]
    public void RuleSources_SurviveXmlAndJson()
    {
        var channel = new ChannelDefinition
        {
            Id = "ch-x",
            Name = "X",
            Sources = [new ContentSource { Type = ContentSourceType.Rule, Rule = new ContentRule { Genres = ["Comedy"], YearFrom = 1980, MinRating = 6.5, Members = ["tv:1"] } }],
        };

        // Jellyfin saves plugin configuration with XmlSerializer.
        var serializer = new XmlSerializer(typeof(ChannelDefinition));
        using var writer = new StringWriter();
        serializer.Serialize(writer, channel);
        using var reader = new StringReader(writer.ToString());
        var rule = ((ChannelDefinition)serializer.Deserialize(reader)!).Sources[0].Rule!;
        Assert.Equal(["Comedy"], rule.Genres);
        Assert.Equal(1980, rule.YearFrom);
        Assert.Null(rule.YearTo);
        Assert.Equal(6.5, rule.MinRating);
        Assert.Equal(["tv:1"], rule.Members);

        var json = PackService.Clone(channel);
        Assert.Equal(ContentSourceType.Rule, json.Sources[0].Type);
        Assert.Equal(1980, json.Sources[0].Rule!.YearFrom);
    }

    [Fact]
    public void BundledPresets_Load()
    {
        var catalog = PresetCatalog.Default;
        var channels = catalog.Bundles.SelectMany(b => b.Channels).ToList();

        Assert.Contains("MIT", catalog.License, StringComparison.Ordinal);
        Assert.True(channels.Count > 100);
        Assert.All(channels, c =>
        {
            Assert.NotEmpty(c.Sources);
            Assert.All(c.Sources, s => Assert.NotNull(s.Rule));
            Assert.DoesNotContain("Nostalgex", c.Name, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(channels.Count, channels.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Copies are independent of the catalog.
        var sitcoms = channels.First(c => c.Name == "Sitcoms");
        sitcoms.CopySources()[0].Rule!.Genres = ["Changed"];
        Assert.NotEqual(["Changed"], sitcoms.Sources[0].Rule!.Genres);
    }

    [Fact]
    public void BundledPresets_PickTheRightTitles()
    {
        var channels = PresetCatalog.Default.Bundles.SelectMany(b => b.Channels).ToDictionary(c => c.Name);
        bool Airs(string channel, IndexedTitle title) => channels[channel].Sources.Any(s => new RuleMatcher(s.Rule!, Now).Matches(title));

        var friends = Show("Friends", 1994, "Comedy") with { OfficialRating = "TV-14" };
        Assert.True(Airs("90s Sitcoms", friends));
        Assert.False(Airs("90s Sitcoms", Show("The Office", 2005, "Comedy")));
        Assert.True(Airs("Westerns", Movie("Unforgiven", 1992, "Western", "Drama")));
        Assert.False(Airs("Rom Coms", Movie("Elf", 2003, "Comedy", "Romance", "Family")));

        // Holiday films stay on the holiday channels.
        var christmas = Movie("A Christmas Story", 1983, "Comedy", "Family");
        Assert.False(Airs("Retro LOL", christmas));
    }
}
