using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Content;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Web;

/// <summary>
/// Adds the web TV page to Jellyfin's side menu. Jellyfin's web app shows the <c>menuLinks</c> listed in its
/// <c>/web/config.json</c>; this adds one, named after the service, to that file as it is served, so no file on disk
/// changes and a Jellyfin update keeps working.
/// </summary>
public sealed class WebMenuStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.UseMiddleware<WebMenuMiddleware>();
            next(app);
        };
}

/// <summary>
/// Rewrites <c>/web/config.json</c> responses to add the web TV menu link (see <see cref="WebMenuStartupFilter"/>).
/// </summary>
public sealed class WebMenuMiddleware
{
    private const string ConfigPath = "/web/config.json";

    private readonly RequestDelegate _next;
    private readonly ChannelStore _store;
    private readonly ILogger<WebMenuMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebMenuMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="store">Channel store.</param>
    /// <param name="logger">Logger.</param>
    public WebMenuMiddleware(RequestDelegate next, ChannelStore store, ILogger<WebMenuMiddleware> logger)
    {
        _next = next;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">HTTP context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path.Value ?? string.Empty;
        var config = Plugin.Instance?.Configuration;
        if (!HttpMethods.IsGet(context.Request.Method)
            || !path.EndsWith(ConfigPath, StringComparison.OrdinalIgnoreCase)
            || config is null
            || !config.ShowInWebMenu
            || _store.Channels.Count == 0)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // The whole, uncompressed file is needed to change it.
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");
        context.Request.Headers.Remove("Accept-Encoding");
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        var body = buffer.ToArray();
        if (context.Response.StatusCode == StatusCodes.Status200OK)
        {
            var prefix = context.Request.PathBase.Value + path[..^ConfigPath.Length];
            var name = string.IsNullOrWhiteSpace(config.ServiceName) ? "Cable TV" : config.ServiceName.Trim();
            var changed = AddLink(body, name, prefix + "/CableTv/Web");
            if (changed is not null)
            {
                body = changed;
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.ContentLength = body.Length;
            }
        }

        await original.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a menu link to a web client config file.
    /// </summary>
    /// <param name="json">The file.</param>
    /// <param name="name">Link text.</param>
    /// <param name="url">Link target.</param>
    /// <returns>The changed file, or null when it isn't a JSON object or already has the link.</returns>
    internal static byte[]? AddLink(byte[] json, string name, string url)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is null)
        {
            return null;
        }

        if (root["menuLinks"] is not JsonArray links)
        {
            links = [];
            root["menuLinks"] = links;
        }

        foreach (var link in links)
        {
            if (link?["url"] is JsonValue value
                && value.TryGetValue<string>(out var existing)
                && string.Equals(existing, url, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        links.Add(new JsonObject { ["name"] = name, ["icon"] = "live_tv", ["url"] = url });
        return JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
    }
}
