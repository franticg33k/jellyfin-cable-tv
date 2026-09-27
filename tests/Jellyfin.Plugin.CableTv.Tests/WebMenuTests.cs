using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.CableTv.Web;
using Xunit;

namespace Jellyfin.Plugin.CableTv.Tests;

public class WebMenuTests
{
    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void AddsTheLink_KeepingEverythingElse()
    {
        var changed = WebMenuMiddleware.AddLink(Json("""{"multiserver":false,"menuLinks":[{"name":"Docs","url":"https://x"}],"plugins":["a"]}"""), "Home Cable", "/jf/CableTv/Web");

        var root = JsonNode.Parse(changed!)!.AsObject();
        Assert.False(root["multiserver"]!.GetValue<bool>());
        Assert.Single(root["plugins"]!.AsArray());
        var links = root["menuLinks"]!.AsArray();
        Assert.Equal(2, links.Count);
        Assert.Equal("Home Cable", links[1]!["name"]!.GetValue<string>());
        Assert.Equal("live_tv", links[1]!["icon"]!.GetValue<string>());
        Assert.Equal("/jf/CableTv/Web", links[1]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void CreatesMenuLinks_WhenMissing_AndDoesNotAddTwice()
    {
        var once = WebMenuMiddleware.AddLink(Json("{}"), "Cable TV", "/CableTv/Web");
        Assert.Single(JsonNode.Parse(once!)!["menuLinks"]!.AsArray());
        Assert.Null(WebMenuMiddleware.AddLink(once!, "Cable TV", "/CableTv/Web"));
    }

    [Fact]
    public void IgnoresOddLinks()
    {
        var changed = WebMenuMiddleware.AddLink(Json("""{"menuLinks":[{"name":"x","url":5},null]}"""), "Cable TV", "/CableTv/Web");
        Assert.Equal(3, JsonNode.Parse(changed!)!["menuLinks"]!.AsArray().Count);
    }

    [Fact]
    public void LeavesNonObjectsAlone()
    {
        Assert.Null(WebMenuMiddleware.AddLink(Json("not json"), "Cable TV", "/CableTv/Web"));
        Assert.Null(WebMenuMiddleware.AddLink(Json("[1,2]"), "Cable TV", "/CableTv/Web"));
    }
}
