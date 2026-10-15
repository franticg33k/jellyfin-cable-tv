using System;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using Jellyfin.Plugin.CableTv.Streaming;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class AudioTrackTests
{
    private static readonly AudioTrack[] DualAudio = [new(0, "eng", true), new(1, "jpn", false), new(2, "eng", false)];

    [Fact]
    public void WithALanguage_PicksTheFirstTrackInIt()
    {
        Assert.Equal(1, AudioTrack.Pick(DualAudio, "jpn"));
        Assert.Equal(1, AudioTrack.Pick(DualAudio, "ja"));
        Assert.Equal(0, AudioTrack.Pick(DualAudio, "en"));
    }

    [Fact]
    public void WithoutALanguage_PicksTheFlaggedDefault_ElseTheFirst()
    {
        Assert.Equal(0, AudioTrack.Pick(DualAudio, null));
        Assert.Equal(1, AudioTrack.Pick([new AudioTrack(0, "eng", false), new AudioTrack(1, "jpn", true)], string.Empty));
        Assert.Equal(0, AudioTrack.Pick([new AudioTrack(0, "eng", false), new AudioTrack(1, "jpn", false)], " "));
    }

    [Fact]
    public void ALanguageTheFileLacksFallsBackToItsDefault()
    {
        Assert.Equal(0, AudioTrack.Pick(DualAudio, "fra"));
        Assert.Equal(0, AudioTrack.Pick([], "jpn"));
    }

    [Fact]
    public void BibliographicCodesMatch()
    {
        Assert.Equal(1, AudioTrack.Pick([new AudioTrack(0, "eng", true), new AudioTrack(1, "ger", false)], "de"));
    }

    private static PoolItem Item(params AudioTrack[] audio) => new(Guid.NewGuid(), "m", TimeSpan.FromMinutes(20).Ticks, "Anime")
    {
        Path = "/media/anime/e1.mkv",
        VideoCodec = "h264",
        Width = 1280,
        Height = 720,
        HasAudio = true,
        AudioTracks = audio,
    };

    private static string[] Build(PoolItem item, StreamProfile profile)
        => FfmpegArguments.Build(
            new ScheduledSlot("s", SlotKind.Program, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(20), item, 0, item.DurationTicks, "g"),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(18),
            TimeSpan.Zero,
            profile).ToArray();

    private static string AudioMap(string[] args)
    {
        var maps = args.Select((a, i) => (a, i)).Where(x => x.a == "-map").Select(x => args[x.i + 1]).ToArray();
        return maps.Single(m => m.Contains(":a:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStreamCarriesTheWantedLanguage()
    {
        var profile = new StreamProfile(FallbackStreamMode.Auto, "h264", 1280, 720, false) { AudioLanguage = "jpn" };

        Assert.Equal("0:a:1", AudioMap(Build(Item(DualAudio), profile)));
    }

    [Fact]
    public void WithNoPreferenceTheStreamCarriesTheFilesDefaultTrack()
    {
        var profile = new StreamProfile(FallbackStreamMode.Auto, "h264", 1280, 720, false);

        Assert.Equal("0:a:0", AudioMap(Build(Item(DualAudio), profile)));
        Assert.Equal("0:a:1", AudioMap(Build(Item(new AudioTrack(0, "eng", false), new AudioTrack(1, "jpn", true)), profile)));
        Assert.Equal("0:a:0", AudioMap(Build(Item(), profile)));
    }
}
