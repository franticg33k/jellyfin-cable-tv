using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Logos;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class LogoLookTests
{
    [Theory]
    [InlineData("#1E88E5", "#1E88E5")]
    [InlineData("1e88e5", "#1E88E5")]
    [InlineData("#fa0", "#FFAA00")]
    [InlineData("  #000000 ", "#000000")]
    [InlineData("#12345", null)]
    [InlineData("blue", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizesColours(string? input, string? expected)
        => Assert.Equal(expected, LogoLook.NormalizeColor(input));

    [Fact]
    public void ChannelChoicesBeatDefaults()
    {
        var look = LogoLook.Create("HBO", "H", LogoStyle.Outline, "#112233", null, LogoStyle.Flat, "#445566", "#778899");
        Assert.Equal(LogoStyle.Outline, look.Style);
        Assert.Equal("H", look.Text);
        Assert.Equal("#112233", look.Background);
        Assert.Equal("#778899", look.TextColor);
    }

    [Fact]
    public void UnsetChoicesFallBackToDefaultsThenToThePlate()
    {
        var inherited = LogoLook.Create("HBO", " ", LogoStyle.Default, "", "", LogoStyle.TextOnly, "", "");
        Assert.Equal(LogoStyle.TextOnly, inherited.Style);
        Assert.Equal("HBO", inherited.Text);
        Assert.Null(inherited.Background);
        Assert.Null(inherited.TextColor);

        Assert.Equal(LogoStyle.Plate, LogoLook.Create("HBO", null, LogoStyle.Default, null, null, LogoStyle.Default, null, null).Style);
    }

    [Fact]
    public void KeyChangesWithTheLook()
    {
        var a = LogoLook.Create("HBO", null, LogoStyle.Default, null, null, LogoStyle.Default, null, null);
        var b = LogoLook.Create("HBO", null, LogoStyle.Default, "#FF0000", null, LogoStyle.Default, null, null);
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(a.Key, LogoLook.Create("HBO", null, LogoStyle.Plate, null, null, LogoStyle.Default, null, null).Key);
    }

    [Fact]
    public void RecognizesUploadedImages()
    {
        Assert.Equal(".png", LogoUpload.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal(".jpg", LogoUpload.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(".gif", LogoUpload.Detect("GIF89a"u8));
        Assert.Equal(".webp", LogoUpload.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(LogoUpload.Detect("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Null(LogoUpload.Detect("MZ\u0090\0"u8));
        Assert.Null(LogoUpload.Detect([]));
    }
}
