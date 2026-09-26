using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Logos;
using Jellyfin.Plugin.CableTv.Packs;
using Jellyfin.Plugin.CableTv.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class PackTests
{
    [Fact]
    public void ParsesLineupWithHeaderQuotesAndBom()
    {
        var csv = "﻿Channel Number,Channel Name,Title,Release Year\r\n"
                  + "2,Cartoon Network,Dexter's Laboratory,1996\r\n"
                  + "2,Cartoon Network,\"Ed, Edd n Eddy\",1999\r\n"
                  + "\r\n"
                  + "5,\"Nick \"\"Classic\"\"\",Rugrats,unknown\n";

        var rows = LineupCsv.ParseLineup(csv);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new LineupRow("2", "Cartoon Network", "Dexter's Laboratory", 1996), rows[0]);
        Assert.Equal("Ed, Edd n Eddy", rows[1].Title);
        Assert.Equal(new LineupRow("5", "Nick \"Classic\"", "Rugrats", null), rows[2]);
    }

    [Fact]
    public void FindsColumnsByName()
    {
        var rows = LineupCsv.ParseLineup("Title,Year,Channel\nSeinfeld,1989,Sitcoms\n");
        Assert.Equal(new LineupRow(string.Empty, "Sitcoms", "Seinfeld", 1989), Assert.Single(rows));
    }

    [Fact]
    public void ReadsLineupWithoutHeader()
    {
        Assert.Equal(new LineupRow("7", "Toons", "Recess", 1997), Assert.Single(LineupCsv.ParseLineup("7,Toons,Recess,1997")));
        Assert.Equal(new LineupRow(string.Empty, "Toons", "Recess", 1997), Assert.Single(LineupCsv.ParseLineup("Toons,Recess,1997")));
    }

    [Fact]
    public void LineupRoundTrips()
    {
        LineupRow[] rows = [new("1", "Comedy, Etc", "\"Weird\" Al Show", 1997), new("2", "Movies", "Alien", null)];
        Assert.Equal(rows, LineupCsv.ParseLineup(LineupCsv.WriteLineup(rows)));
    }

    [Fact]
    public void DetectsAndParsesEpisodes()
    {
        var csv = "Show Title,Episode Title\nFriends,The One with the Holiday Armadillo\n\"Home Improvement\",'Twas the Night Before Chanukah\n";
        Assert.True(LineupCsv.IsEpisodes(csv));
        Assert.False(LineupCsv.IsEpisodes("Channel Number,Channel Name,Title,Release Year\n"));
        var rows = LineupCsv.ParseEpisodes(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new EpisodeRow("Friends", "The One with the Holiday Armadillo"), rows[0]);
    }

    [Fact]
    public void RejectsCsvWithoutTitleColumn()
        => Assert.Throws<FormatException>(() => LineupCsv.ParseLineup("Channel Number,Channel Name\n1,Foo\n"));

    [Fact]
    public void PackJsonRoundTrips()
    {
        var pack = new ChannelPack
        {
            Name = "Test",
            Channels =
            [
                new ChannelDefinition
                {
                    Id = "ch-toons",
                    Number = "2",
                    Name = "Toons",
                    Sorting = ChannelSorting.RoundRobin,
                    Sources = [new ContentSource { Type = ContentSourceType.Titles, Values = ["Recess (1997)"] }, new ContentSource { Type = ContentSourceType.Collection, Ids = [Guid.NewGuid()], Exclude = true }],
                    Seasons = [new SeasonDefinition { Name = "Christmas", Mode = SeasonMode.Replace }],
                },
            ],
            References = new() { ["abc"] = new PackReference("Pixar", "Collection") },
        };

        var json = JsonSerializer.Serialize(pack, PackService.Json);
        Assert.Contains("\"RoundRobin\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Titles\"", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<ChannelPack>(json.Replace("\"Channels\"", "\"channels\"", StringComparison.Ordinal), PackService.Json)!;
        var channel = Assert.Single(back.Channels);
        Assert.Equal("Recess (1997)", channel.Sources[0].Values[0]);
        Assert.True(channel.Sources[1].Exclude);
        Assert.Equal(SeasonMode.Replace, channel.Seasons[0].Mode);
        Assert.Equal("Pixar", back.References!["abc"].Name);
    }

    [Fact]
    public void CloneIsDeep()
    {
        var original = new[] { new ChannelDefinition { Id = "a", Sources = [new ContentSource { Values = ["x"] }] } };
        var copy = PackService.Clone(original);
        copy[0].Sources[0].Values = ["y"];
        Assert.Equal("x", original[0].Sources[0].Values[0]);
    }

    [Fact]
    public void NumberAllocatorSkipsTakenNumbers()
    {
        var numbers = new ChannelDefaults.NumberAllocator([new ChannelDefinition { Number = "0100" }, new ChannelDefinition { Number = "101" }]);
        Assert.True(numbers.IsTaken("100"));
        Assert.Equal("102", numbers.Take(null, 100));
        Assert.Equal("7", numbers.Take("7", 100));
        Assert.Equal("103", numbers.Take("7", 100));
    }

    [Fact]
    public void DefaultsTurnOnBreaksOnlyWithCommercials()
    {
        var plain = ChannelDefaults.Create("Cartoon Network", "2", commercials: false);
        Assert.Equal("ch-cartoon_network", plain.Id);
        Assert.False(plain.CommercialsEnabled);

        var withAds = ChannelDefaults.Create("Toons", "3", commercials: true);
        Assert.True(withAds.CommercialsEnabled);
        Assert.Equal(30, withAds.GridMinutes);
    }

    [Fact]
    public void RendersLogoPng()
    {
        var png = LogoRenderer.RenderPng("Cartoon Network");
        Assert.True(png.Length > 1000);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        Assert.NotEqual(png, LogoRenderer.RenderPng("A much longer channel name that wraps"));
    }
}
