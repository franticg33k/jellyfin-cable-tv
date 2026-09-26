using System;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using Jellyfin.Plugin.CableTv.Streaming;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class StreamingTests
{
    private static readonly DateTime Start = new(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc);

    private static PoolItem Item(string codec, int width, int height, double minutes = 20, bool audio = true)
        => new(Guid.NewGuid(), "m", TimeSpan.FromMinutes(minutes).Ticks, "Show")
        {
            Path = "/media/show.mkv",
            VideoCodec = codec,
            Width = width,
            Height = height,
            HasAudio = audio,
        };

    private static ScheduledSlot Slot(PoolItem? item)
        => new("s-1", item is null ? SlotKind.Filler : SlotKind.Program, Start, Start.AddMinutes(20), item, 0, TimeSpan.FromMinutes(20).Ticks, "g-1");

    private static string Arg(System.Collections.Generic.IReadOnlyList<string> args, string flag)
        => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Profile_PicksCodecAndSizeWithMostAirtime()
    {
        var pool = new[] { Item("hevc", 1920, 1080, 60), Item("h264", 1280, 720, 20), Item("h264", 1280, 720, 30) };

        var profile = StreamProfile.For(pool, FallbackStreamMode.Auto, 720, true);

        Assert.Equal(("hevc", 1920, 1080), (profile.VideoCodec, profile.Width, profile.Height));
    }

    [Fact]
    public void Profile_FallsBackToH264ForCodecsItCannotEncode()
    {
        var profile = StreamProfile.For([Item("mpeg2video", 720, 480)], FallbackStreamMode.Auto, 720, true);

        Assert.Equal("h264", profile.VideoCodec);
    }

    [Fact]
    public void Profile_TranscodeMode_UsesH264AtConfiguredHeight()
    {
        var profile = StreamProfile.For([Item("hevc", 1920, 1080)], FallbackStreamMode.Transcode, 1080, true);

        Assert.Equal(("h264", 1920, 1080), (profile.VideoCodec, profile.Width, profile.Height));
    }

    [Fact]
    public void Auto_CopiesMatchingItems_AndTranscodesOthers()
    {
        var profile = new StreamProfile(FallbackStreamMode.Auto, "h264", 1280, 720, true);

        var copied = FfmpegArguments.Build(Slot(Item("h264", 1280, 720)), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(42), profile);
        var encoded = FfmpegArguments.Build(Slot(Item("hevc", 1920, 1080)), TimeSpan.Zero, TimeSpan.FromMinutes(20), TimeSpan.Zero, profile);

        Assert.Equal("copy", Arg(copied, "-c:v"));
        Assert.Equal("300", Arg(copied, "-ss"));
        Assert.Equal("900", Arg(copied, "-t"));
        Assert.True(copied.ToList().IndexOf("-t") < copied.ToList().IndexOf("-i"), "-t must limit the input, not the output");
        // 30 s base + 42 s stream position - 300 s into the item: the seek point lands on the stream position.
        Assert.Equal("-228", Arg(copied, "-output_ts_offset"));
        Assert.Contains("-copyts", copied);
        Assert.Equal("30", Arg(encoded, "-output_ts_offset"));
        Assert.Equal("libx264", Arg(encoded, "-c:v"));
        Assert.Contains("scale=1280:720", Arg(encoded, "-vf"), StringComparison.Ordinal);
        Assert.DoesNotContain("-ss", encoded);
    }

    [Fact]
    public void AudioIsAlwaysReencodedAndLevelled()
    {
        var profile = new StreamProfile(FallbackStreamMode.Copy, "h264", 1280, 720, true);

        var args = FfmpegArguments.Build(Slot(Item("h264", 1280, 720)), TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.Zero, profile);

        Assert.Equal("aac", Arg(args, "-c:a"));
        Assert.Equal("48000", Arg(args, "-ar"));
        Assert.Equal("2", Arg(args, "-ac"));
        Assert.StartsWith("loudnorm", Arg(args, "-af"), StringComparison.Ordinal);
        Assert.Equal("mpegts", Arg(args, "-f"));
    }

    [Fact]
    public void SilentItems_GetGeneratedAudio()
    {
        var profile = new StreamProfile(FallbackStreamMode.Copy, "h264", 1280, 720, false);

        var args = FfmpegArguments.Build(Slot(Item("h264", 1280, 720, audio: false)), TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.Zero, profile);

        Assert.Contains("anullsrc=r=48000:cl=stereo", args);
        Assert.DoesNotContain("-itsoffset", args);
        Assert.Contains("1:a:0", args);
        Assert.DoesNotContain("-af", args);
    }

    [Fact]
    public void Filler_IsBlackAtProfileSize()
    {
        var profile = new StreamProfile(FallbackStreamMode.Copy, "hevc", 1920, 1080, true);

        var args = FfmpegArguments.Build(Slot(null), TimeSpan.Zero, TimeSpan.FromSeconds(90), TimeSpan.Zero, profile);

        Assert.Contains("color=c=black:s=1920x1080:r=25", args);
        Assert.Equal("libx265", Arg(args, "-c:v"));
        Assert.DoesNotContain("-af", args);
    }

    [Fact]
    public void InitialBurst_IsOnlyAddedWhenAsked()
    {
        var profile = new StreamProfile(FallbackStreamMode.Copy, "h264", 1280, 720, true);

        var burst = FfmpegArguments.Build(Slot(Item("h264", 1280, 720)), TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.Zero, profile, 2);
        var paced = FfmpegArguments.Build(Slot(Item("h264", 1280, 720)), TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.Zero, profile);

        Assert.Equal("2", Arg(burst, "-readrate_initial_burst"));
        Assert.DoesNotContain("-readrate_initial_burst", paced);
        Assert.Equal("1", Arg(paced, "-readrate"));
    }

    [Fact]
    public void SilentItems_JoinedMidway_ShiftGeneratedAudioToTheSeekPoint()
    {
        var profile = new StreamProfile(FallbackStreamMode.Copy, "h264", 1280, 720, false);

        var args = FfmpegArguments.Build(Slot(Item("h264", 1280, 720, audio: false)), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(1), TimeSpan.Zero, profile);

        Assert.Equal("90", Arg(args, "-itsoffset"));
    }
}
