using System;
using System.Linq;
using Jellyfin.Plugin.CableTv.Api;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Library;
using Jellyfin.Plugin.CableTv.Scheduling;
using Jellyfin.Plugin.CableTv.Streaming;
using Jellyfin.Plugin.CableTv.Weather;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class ExtrasTests
{
    private static readonly DateTime Anchor = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void StreamChannelRepeatsHourBlocksWithTheUrl()
    {
        var item = new PoolItem(Guid.Empty, string.Empty, 0, "News 24") { Path = "https://example.com/live.m3u8" };
        var timeline = new RepeatingTimeline("ch-news", Anchor, SlotKind.Stream, item, TimeSpan.FromHours(1));
        var from = new DateTime(2026, 9, 26, 10, 20, 0, DateTimeKind.Utc);

        var blocks = timeline.GetBlocks(from, from.AddHours(3)).ToList();

        Assert.Equal(4, blocks.Count);
        Assert.Equal(new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc), blocks[0].StartUtc);
        var slot = SlotDto.From(blocks[0].Slots[0]);
        Assert.Equal("stream", slot.Kind);
        Assert.Equal("https://example.com/live.m3u8", slot.Url);
        Assert.Null(slot.ItemId);
        var guide = GuideProgramDto.From(blocks[0]);
        Assert.Equal("stream", guide.Kind);
        Assert.Null(guide.ItemId);
        Assert.Equal("News 24", guide.Title);
        Assert.Equal(timeline.Version, new RepeatingTimeline("ch-news", Anchor, SlotKind.Stream, item, TimeSpan.FromHours(1)).Version);
    }

    [Fact]
    public void MusicSlotsCarryArtistAndAlbum()
    {
        var track = new PoolItem(Guid.NewGuid(), "x", TimeSpan.FromMinutes(4).Ticks, "Song") { IsAudio = true, Artist = "Band", Album = "Record" };
        var slot = SlotDto.From(new ScheduledSlot("s", SlotKind.Program, Anchor, Anchor.AddMinutes(4), track, 0, track.DurationTicks, "g"));
        Assert.True(slot.Audio);
        Assert.Equal("Band", slot.Artist);
        Assert.Equal("Record", slot.Album);
    }

    [Fact]
    public void TrailerSlotsNameTheirOwner()
    {
        var owner = Guid.NewGuid();
        var trailer = new PoolItem(Guid.NewGuid(), "x", TimeSpan.FromMinutes(2).Ticks, "Starship Harbor") { IsTrailer = true, OwnerId = owner, EpisodeTitle = "Trailer" };
        var slot = SlotDto.From(new ScheduledSlot("s", SlotKind.Program, Anchor, Anchor.AddMinutes(2), trailer, 0, trailer.DurationTicks, "g"));
        Assert.True(slot.Trailer);
        Assert.Equal(owner.ToString("N"), slot.OwnerId);
    }

    [Theory]
    [InlineData(0, "Sunny")]
    [InlineData(2, "Partly cloudy")]
    [InlineData(95, "Thunderstorms")]
    [InlineData(1234, "Unknown")]
    public void DescribesWeatherCodes(int code, string text) => Assert.Equal(text, WeatherCodes.Describe(code).Long);

    [Theory]
    [InlineData(0, "N")]
    [InlineData(45, "NE")]
    [InlineData(200, "SSW")]
    [InlineData(359, "N")]
    [InlineData(-90, "W")]
    public void ConvertsWindDirection(double degrees, string point) => Assert.Equal(point, WeatherCodes.Compass(degrees));

    [Fact]
    public void DrawsAForecastCard()
    {
        var report = new WeatherReport(
            "Chicago",
            false,
            DateTime.UtcNow,
            new WeatherNow(72, 70, 45, 8, "NW", 30.01, 2, "Partly cloudy"),
            Enumerable.Range(0, 7).Select(i => new WeatherDay(DateTime.Today.AddDays(i).ToString("yyyy-MM-dd"), 75 + i, 60 - i, i % 3, "Sunny", 10 * i)).ToList(),
            [new WeatherHour("2026-09-26T10:00", 70, 1, 0)],
            "06:45",
            "18:50");
        var png = WeatherCardRenderer.RenderPng(report, "Weather Now");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        Assert.True(png.Length > 10_000);
    }

    [Fact]
    public void MusicAirsOverABackgroundOnTheLiveStream()
    {
        var track = new PoolItem(Guid.NewGuid(), "x", TimeSpan.FromMinutes(4).Ticks, "Song") { IsAudio = true, Path = "/music/song.flac" };
        var slot = new ScheduledSlot("s", SlotKind.Program, Anchor, Anchor.AddMinutes(4), track, 0, track.DurationTicks, "g");
        var profile = new StreamProfile(FallbackStreamMode.Auto, "h264", 1280, 720, false);

        var args = FfmpegArguments.Build(slot, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3.5), TimeSpan.Zero, profile).ToList();

        Assert.Contains("/music/song.flac", args);
        Assert.Contains(args, a => a.StartsWith("color=", StringComparison.Ordinal));
        Assert.Equal("1:v:0", args[args.IndexOf("-map") + 1]);
        Assert.Contains("libx264", args);
    }

    [Fact]
    public void StillCardArguments()
    {
        var args = FfmpegArguments.BuildStill("/data/weather/card.png", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), new StreamProfile(FallbackStreamMode.Auto, "h264", 1280, 720, false)).ToList();
        Assert.Contains("/data/weather/card.png", args);
        Assert.Contains("stillimage", args);
        Assert.Equal("300", args[args.IndexOf("-t") + 1]);
    }

    [Fact]
    public void CommercialWithoutAConvertedCopyAirsAsIs()
    {
        var ad = new PoolItem(Guid.NewGuid(), "a", TimeSpan.FromSeconds(30).Ticks, "Ad") { VideoCodec = "mpeg4", Width = 640, Height = 480, Path = "/ads/a.avi" };
        var profile = new StreamProfile(FallbackStreamMode.Auto, "h264", 1280, 720, false);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cabletv-test-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            Assert.Same(ad, CommercialCache.ForStream(ad, profile, dir));

            // Once converted, the copy airs in the channel's format, so the stream can copy it.
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"{ad.ItemId:N}-{ad.DurationTicks}-h264-1280x720.ts"), [0]);
            var aired = CommercialCache.ForStream(ad, profile, dir);
            Assert.EndsWith(".ts", aired.Path, StringComparison.Ordinal);
            Assert.True(profile.CanCopy(aired));
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void IndexFindsAlbumsByArtist()
    {
        var index = new LibraryIndex([new IndexedTitle(Guid.NewGuid(), TitleKind.Album, "Greatest Hits", 1981) { Artists = ["Queen"], Genres = ["Rock"] }]);
        Assert.Single(index.WithArtist(["queen"]));
        Assert.Single(index.WithGenre(["Rock"]));
    }
}

public class GuideMergeTests
{
    private static readonly DateTime T = new(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc);

    private static ScheduledBlock Song(int startMin, int minutes, string artist)
    {
        var item = new PoolItem(Guid.NewGuid(), "x", TimeSpan.FromMinutes(minutes).Ticks, "Song " + startMin) { IsAudio = true, Artist = artist };
        var start = T.AddMinutes(startMin);
        return new ScheduledBlock("b" + startMin, item, start, start.AddMinutes(minutes), [new ScheduledSlot("s" + startMin, SlotKind.Program, start, start.AddMinutes(minutes), item, 0, item.DurationTicks, "g" + startMin)]);
    }

    [Fact]
    public void FoldsSongsIntoHalfHourEntries()
    {
        var blocks = new[] { Song(0, 4, "Queen"), Song(4, 5, "ABBA"), Song(9, 4, "Queen"), Song(28, 4, "Blondie"), Song(32, 3, "Cher") };
        var merged = GuideMerge.Merge(blocks, "Rock Radio").ToList();

        Assert.Equal(2, merged.Count);
        Assert.Equal(T, merged[0].StartUtc);
        Assert.Equal(T.AddMinutes(32), merged[0].EndUtc);
        var dto = GuideProgramDto.From(merged[0]);
        Assert.Equal("Rock Radio", dto.Title);
        Assert.Equal("Queen, ABBA, Blondie", dto.EpisodeTitle);
        Assert.Equal("music", dto.Kind);
        Assert.Null(dto.ItemId);
        Assert.Equal(4, merged[0].Slots.Count);
    }

    [Fact]
    public void LeavesProgrammesAlone()
    {
        var show = new PoolItem(Guid.NewGuid(), "x", TimeSpan.FromMinutes(30).Ticks, "Show");
        var block = new ScheduledBlock("b", show, T, T.AddMinutes(30), [new ScheduledSlot("s", SlotKind.Program, T, T.AddMinutes(30), show, 0, show.DurationTicks, "g")]);
        Assert.Same(block, Assert.Single(GuideMerge.Merge([block], "Ch")));
    }
}
