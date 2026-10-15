using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Library;
using Jellyfin.Plugin.CableTv.Scheduling;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Logos;

/// <summary>
/// Where a channel's logo comes from: a file on the server or a URL.
/// </summary>
/// <param name="LocalPath">Local image file, or null.</param>
/// <param name="RemoteUrl">Remote image URL, or null.</param>
public sealed record LogoRef(string? LocalPath, string? RemoteUrl);

/// <summary>
/// Picks each channel's logo. In order: the channel's own URL or file; "studio:Name"; the configured logo pack
/// ("{LogoBaseUrl}/{slug}.png"); the logo Jellyfin has for a studio or TV network with the channel's name (downloaded by
/// its Studio Images plugin); otherwise a generated logo. A channel's logo options (transparent background, size,
/// position) are applied to whichever image that gives, except a generated logo. No third-party artwork ships with the plugin.
/// </summary>
public class LogoService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LogoService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, string?> _studioImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _failedSources = new(StringComparer.Ordinal);
    private bool _renderFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogoService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="httpClientFactory">HTTP client factory, for logos at a URL that need processing.</param>
    /// <param name="logger">Logger.</param>
    public LogoService(ILibraryManager libraryManager, IHttpClientFactory httpClientFactory, ILogger<LogoService> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a channel's logo; draws and caches a generated one when nothing else is found.
    /// </summary>
    /// <param name="channel">Channel.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The logo, or null when none could be made.</returns>
    public LogoRef? Resolve(ChannelDefinition channel, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(config);
        var source = ResolveSource(channel, config, out var generated);
        return source is not null && !generated && WantsProcessing(channel) ? Process(source, channel) ?? source : source;
    }

    /// <summary>Forgets looked-up studio images, for example after a library scan.</summary>
    public void ClearCache()
    {
        _studioImages.Clear();
        _failedSources.Clear();
    }

    private static bool WantsProcessing(ChannelDefinition channel)
        => channel.LogoTransparent || channel.LogoScale is >= 10 and < 100 || channel.LogoAlign != LogoAlign.Center;

    private LogoRef? ResolveSource(ChannelDefinition channel, PluginConfiguration config, out bool generated)
    {
        generated = false;
        var logo = channel.LogoUrl?.Trim();

        if (!string.IsNullOrEmpty(logo) && Uri.TryCreate(logo, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return new LogoRef(null, logo);
        }

        // An image file on the server.
        if (!string.IsNullOrEmpty(logo) && Path.IsPathRooted(logo) && File.Exists(logo))
        {
            return new LogoRef(logo, null);
        }

        if (!string.IsNullOrEmpty(logo) && logo.StartsWith("studio:", StringComparison.OrdinalIgnoreCase)
            && StudioImage(logo["studio:".Length..].Trim()) is { } studioLogo)
        {
            return new LogoRef(studioLogo, null);
        }

        if (!string.IsNullOrWhiteSpace(config.LogoBaseUrl) && !string.IsNullOrWhiteSpace(channel.Name))
        {
            return new LogoRef(null, config.LogoBaseUrl.TrimEnd('/') + "/" + TitleNormalizer.Slug(channel.Name) + ".png");
        }

        if (!string.IsNullOrWhiteSpace(channel.Name) && StudioImage(channel.Name.Trim()) is { } byName)
        {
            return new LogoRef(byName, null);
        }

        generated = true;
        return Generated(channel.Name);
    }

    /// <summary>
    /// Applies the channel's logo options to its logo, caching the result as a PNG in the plugin's data folder. Null
    /// when it can't be done (the caller then uses the unchanged logo).
    /// </summary>
    private LogoRef? Process(LogoRef source, ChannelDefinition channel)
    {
        if (Plugin.Instance is not { } plugin || _renderFailed)
        {
            return null;
        }

        var scale = Math.Clamp(channel.LogoScale, 10, 100);
        var tolerance = Math.Clamp(channel.LogoTolerance, 1, 100);
        string identity;
        if (source.LocalPath is { } local)
        {
            var file = new FileInfo(local);
            identity = local + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        }
        else
        {
            identity = source.RemoteUrl ?? string.Empty;
        }

        var options = $"{channel.LogoTransparent}|{tolerance}|{scale}|{channel.LogoAlign}|{LogoRenderer.StyleVersion}";
        var hash = StableHash.ToHex(StableHash.Add(StableHash.Add(StableHash.Start(), identity), options), 10);
        var path = Path.Combine(plugin.DataFolderPath, "logos", "custom-" + TitleNormalizer.Slug(channel.Id) + "-" + hash + ".png");
        if (File.Exists(path))
        {
            return new LogoRef(path, null);
        }

        if (_failedSources.ContainsKey(identity))
        {
            return null;
        }

        try
        {
            var bytes = source.LocalPath is { } p ? File.ReadAllBytes(p) : Download(source.RemoteUrl!);
            var png = LogoProcessor.Process(bytes, channel.LogoTransparent, tolerance, scale, channel.LogoAlign);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
            return new LogoRef(path, null);
        }
        catch (Exception ex) when (ex is TypeLoadException or DllNotFoundException or TypeInitializationException)
        {
            _renderFailed = true;
            _logger.LogWarning(ex, "Couldn't process the logo for channel {Id}", channel.Id);
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        {
            _failedSources[identity] = true;
            _logger.LogWarning(ex, "Couldn't process the logo for channel {Id}; using it unchanged", channel.Id);
            return null;
        }
    }

    private byte[] Download(string url)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = _httpClientFactory.CreateClient();
        using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel.Token).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 5 * 1024 * 1024)
        {
            throw new InvalidDataException("The logo is larger than 5 MB.");
        }

        return response.Content.ReadAsByteArrayAsync(cancel.Token).GetAwaiter().GetResult();
    }

    private string? StudioImage(string name)
        => _studioImages.GetOrAdd(name, n =>
        {
            // Query rather than ILibraryManager.GetStudio, which would create a studio that doesn't exist.
            var studio = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Studio],
                Name = n,
                Limit = 1,
            }).FirstOrDefault();
            var path = studio is null
                ? null
                : new[] { ImageType.Thumb, ImageType.Primary, ImageType.Logo }
                    .Select(type => studio.GetImagePath(type, 0))
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
            return path;
        });

    private LogoRef? Generated(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || _renderFailed || Plugin.Instance is not { } plugin)
        {
            return null;
        }

        var directory = Path.Combine(plugin.DataFolderPath, "logos");
        var hash = StableHash.ToHex(StableHash.Add(StableHash.Add(StableHash.Start(), name), LogoRenderer.StyleVersion), 10);
        var path = Path.Combine(directory, TitleNormalizer.Slug(name) + "-" + hash + ".png");
        if (File.Exists(path))
        {
            return new LogoRef(path, null);
        }

        try
        {
            Directory.CreateDirectory(directory);
            var png = LogoRenderer.RenderPng(name);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
            return new LogoRef(path, null);
        }
        catch (Exception ex) when (ex is TypeLoadException or DllNotFoundException or FileNotFoundException or TypeInitializationException or IOException or UnauthorizedAccessException)
        {
            // Without a working SkiaSharp (or a writable data folder) channels simply have no generated logo.
            _renderFailed = ex is not IOException and not UnauthorizedAccessException;
            _logger.LogWarning(ex, "Couldn't draw a logo for channel {Name}", name);
            return null;
        }
    }
}
