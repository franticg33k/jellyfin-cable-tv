using System;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using Jellyfin.Plugin.CableTv.Streaming;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class SubtitleTests
{
    private static readonly StreamProfile Burning = new(FallbackStreamMode.Auto, "h264", 1280, 720, false) { BurnSubtitles = true, SubtitleLanguage = "eng" };

    private static SubtitleTrack Text(string? language, bool forced = false, bool isDefault = false, int index = 0)
        => new(index, "subrip", language, null, forced, isDefault);

    private static PoolItem Item(params SubtitleTrack[] subtitles) => new(Guid.NewGuid(), "m", TimeSpan.FromMinutes(20).Ticks, "Show")
    {
        Path = "/media/Tom's Show/e1.mkv",
        VideoCodec = "h264",
        Width = 1280,
        Height = 720,
        Subtitles = subtitles,
    };

    private static string[] Build(PoolItem item, StreamProfile profile)
        => FfmpegArguments.Build(
            new ScheduledSlot("s", SlotKind.Program, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(20), item, 0, item.DurationTicks, "g"),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(18),
            TimeSpan.Zero,
            profile).ToArray();

    [Fact]
    public void Pick_PrefersFullTrackInTheWantedLanguage()
    {
        var tracks = new[] { Text("jpn", index: 0), Text("eng", forced: true, index: 1), Text("eng", index: 2) };
        Assert.Equal(2, SubtitleTrack.Pick(tracks, "eng")!.EmbeddedIndex);
        Assert.Equal(2, SubtitleTrack.Pick(tracks, "en")!.EmbeddedIndex);
        Assert.Null(SubtitleTrack.Pick(tracks, "fra"));
    }

    [Fact]
    public void Pick_MatchesBibliographicLanguageCodes()
    {
        Assert.NotNull(SubtitleTrack.Pick([Text("ger")], "de"));
        Assert.NotNull(SubtitleTrack.Pick([Text("fre")], "fra"));
    }

    [Fact]
    public void Pick_WithoutLanguage_UsesTheDefaultTrack_AndSkipsForcedOnly()
    {
        Assert.Equal(1, SubtitleTrack.Pick([Text("jpn", index: 0), Text("eng", isDefault: true, index: 1)], "")!.EmbeddedIndex);
        Assert.Null(SubtitleTrack.Pick([Text("eng", forced: true)], ""));
    }

    [Fact]
    public void Pick_IgnoresFormatsFfmpegCannotBurnIn()
    {
        Assert.Null(SubtitleTrack.Pick([new SubtitleTrack(-1, "sup", "eng", "/media/e1.sup", false, false)], "eng"));
    }

    [Fact]
    public void TextSubtitles_AreBurnedInBeforeScaling_AndForceATranscode()
    {
        var args = Build(Item(Text("eng")), Burning);

        var vf = args[Array.IndexOf(args, "-vf") + 1];
        Assert.StartsWith("subtitles=filename='/media/Tom'\\\\\\''s Show/e1.mkv':si=0,scale=1280:720", vf, StringComparison.Ordinal);
        Assert.Equal("libx264", args[Array.IndexOf(args, "-c:v") + 1]);
    }

    [Fact]
    public void ExternalSubtitles_UseTheSubtitleFile()
    {
        var args = Build(Item(new SubtitleTrack(-1, "subrip", "eng", "/media/e1.en.srt", false, false)), Burning);
        Assert.StartsWith("subtitles=filename='/media/e1.en.srt',", args[Array.IndexOf(args, "-vf") + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void ImageSubtitles_AreOverlaid()
    {
        var args = Build(Item(new SubtitleTrack(1, "hdmv_pgs_subtitle", "eng", null, false, false)), Burning);

        Assert.Contains("[0:v:0][0:s:1]overlay", args[Array.IndexOf(args, "-filter_complex") + 1], StringComparison.Ordinal);
        Assert.Equal("[v]", args[Array.IndexOf(args, "-map") + 1]);
        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void WithBurnInOff_OrNoMatchingTrack_VideoIsStillCopied()
    {
        var off = Build(Item(Text("eng")), Burning with { BurnSubtitles = false });
        Assert.Equal("copy", off[Array.IndexOf(off, "-c:v") + 1]);
        var noMatch = Build(Item(Text("jpn")), Burning);
        Assert.Equal("copy", noMatch[Array.IndexOf(noMatch, "-c:v") + 1]);
    }
}
