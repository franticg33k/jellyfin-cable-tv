using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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
/// Picks each channel's logo. In order: the channel's own URL; "studio:Name"; the configured logo pack
/// ("{LogoBaseUrl}/{slug}.png"); the logo Jellyfin has for a studio or TV network with the channel's name (downloaded by
/// its Studio Images plugin); otherwise a generated logo. No third-party artwork ships with the plugin.
/// </summary>
public class LogoService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LogoService> _logger;
    private readonly ConcurrentDictionary<string, string?> _studioImages = new(StringComparer.OrdinalIgnoreCase);
    private bool _renderFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogoService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public LogoService(ILibraryManager libraryManager, ILogger<LogoService> logger)
    {
        _libraryManager = libraryManager;
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
        var logo = channel.LogoUrl?.Trim();

        if (!string.IsNullOrEmpty(logo) && Uri.TryCreate(logo, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return new LogoRef(null, logo);
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

        return Generated(channel.Name);
    }

    /// <summary>Forgets looked-up studio images, for example after a library scan.</summary>
    public void ClearCache() => _studioImages.Clear();

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
