using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Logos;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class LogoImageTests
{
    private static byte[] Image(int w, int h, (byte R, byte G, byte B) background, System.Action<byte[], int> paint)
    {
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            rgba[i * 4] = background.R;
            rgba[(i * 4) + 1] = background.G;
            rgba[(i * 4) + 2] = background.B;
            rgba[(i * 4) + 3] = 255;
        }

        paint(rgba, w);
        return rgba;
    }

    private static void Fill(byte[] rgba, int w, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var i = ((y * w) + x) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }
        }
    }

    private static byte Alpha(byte[] rgba, int w, int x, int y) => rgba[(((y * w) + x) * 4) + 3];

    [Fact]
    public void RemovesAWhiteBackground_ButKeepsTheLogo()
    {
        var rgba = Image(20, 20, (255, 255, 255), (px, w) => Fill(px, w, 5, 5, 15, 15, 200, 0, 0));

        Assert.True(LogoImage.RemoveBackground(rgba, 20, 20, 24));

        Assert.Equal(0, Alpha(rgba, 20, 0, 0));
        Assert.Equal(0, Alpha(rgba, 20, 19, 19));
        Assert.Equal(255, Alpha(rgba, 20, 10, 10));
    }

    [Fact]
    public void KeepsBackgroundColouredPixelsInsideTheLogo()
    {
        // A white hole in a red ring isn't connected to the edge, so it isn't background.
        var rgba = Image(20, 20, (255, 255, 255), (px, w) =>
        {
            Fill(px, w, 4, 4, 16, 16, 200, 0, 0);
            Fill(px, w, 8, 8, 12, 12, 255, 255, 255);
        });

        LogoImage.RemoveBackground(rgba, 20, 20, 24);

        Assert.Equal(0, Alpha(rgba, 20, 1, 1));
        Assert.Equal(255, Alpha(rgba, 20, 10, 10));
        Assert.Equal(255, Alpha(rgba, 20, 5, 5));
    }

    [Fact]
    public void RemovesAnyPlainBackgroundColour()
    {
        var rgba = Image(12, 12, (10, 10, 10), (px, w) => Fill(px, w, 3, 3, 9, 9, 250, 250, 250));

        Assert.True(LogoImage.RemoveBackground(rgba, 12, 12, 24));
        Assert.Equal(0, Alpha(rgba, 12, 0, 5));
        Assert.Equal(255, Alpha(rgba, 12, 6, 6));
    }

    [Fact]
    public void LeavesAnImageWithoutAPlainBackgroundAlone()
    {
        var rgba = Image(10, 10, (255, 255, 255), (px, w) => Fill(px, w, 0, 0, 5, 5, 0, 0, 255));
        var copy = (byte[])rgba.Clone();

        Assert.False(LogoImage.RemoveBackground(rgba, 10, 10, 24));
        Assert.Equal(copy, rgba);
    }

    [Fact]
    public void LeavesAnAlreadyTransparentImageAlone()
    {
        var rgba = new byte[8 * 8 * 4];

        Assert.False(LogoImage.RemoveBackground(rgba, 8, 8, 24));
    }

    [Fact]
    public void TolerancePicksUpOffWhiteBackgrounds()
    {
        var rgba = Image(10, 10, (255, 255, 255), (px, w) => Fill(px, w, 4, 4, 6, 6, 0, 0, 0));
        Fill(rgba, 10, 0, 0, 10, 2, 235, 235, 235);
        Fill(rgba, 10, 0, 0, 2, 10, 235, 235, 235);

        LogoImage.RemoveBackground(rgba, 10, 10, 20);

        Assert.Equal(0, Alpha(rgba, 10, 1, 1));
        Assert.Equal(0, Alpha(rgba, 10, 9, 9));
    }

    [Fact]
    public void VisibleBoundsTrimTheEmptyMargin()
    {
        var rgba = new byte[10 * 10 * 4];
        Fill(rgba, 10, 2, 3, 6, 8, 1, 2, 3);

        Assert.Equal((2, 3, 4, 5), LogoImage.VisibleBounds(rgba, 10, 10));
        Assert.Null(LogoImage.VisibleBounds(new byte[16], 2, 2));
    }

    [Theory]
    [InlineData(100, LogoAlign.Center, 0, 20, 640, 320)]
    [InlineData(50, LogoAlign.Center, 160, 100, 320, 160)]
    [InlineData(50, LogoAlign.TopLeft, 0, 0, 320, 160)]
    [InlineData(50, LogoAlign.BottomRight, 320, 200, 320, 160)]
    [InlineData(50, LogoAlign.Top, 160, 0, 320, 160)]
    [InlineData(50, LogoAlign.Left, 0, 100, 320, 160)]
    public void PlacesALogoByScaleAndAlignment(int scale, LogoAlign align, float x, float y, float width, float height)
    {
        // A 2:1 logo on a 640x360 canvas.
        var place = LogoImage.Place(200, 100, 640, 360, scale, align);

        Assert.Equal(x, place.X, 0.5f);
        Assert.Equal(y, place.Y, 0.5f);
        Assert.Equal(width, place.Width, 0.5f);
        Assert.Equal(height, place.Height, 0.5f);
    }

    [Fact]
    public void ScaleIsKeptInRange()
    {
        Assert.Equal(64f, LogoImage.Place(100, 100, 640, 640, 0, LogoAlign.Center).Width, 0.5f);
        Assert.Equal(640f, LogoImage.Place(100, 100, 640, 640, 500, LogoAlign.Center).Width, 0.5f);
    }
}
