using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Packs;

/// <summary>
/// Ready-made channel lineups that ship with the plugin, grouped in bundles. Each preset channel is one or more
/// <see cref="ContentSourceType.Rule"/> sources, so it fills itself from whatever the library holds.
/// </summary>
public sealed class PresetCatalog
{
    private const string ResourceName = "Jellyfin.Plugin.CableTv.Packs.Presets.nostalgex.json";

    private static readonly Lazy<PresetCatalog> Bundled = new(() => Load(typeof(PresetCatalog).Assembly.GetManifestResourceStream(ResourceName)));

    /// <summary>Gets the catalog's name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets where the lineups come from.</summary>
    public string? Source { get; init; }

    /// <summary>Gets the license the lineups are under.</summary>
    public string? License { get; init; }

    /// <summary>Gets the bundles.</summary>
    public IReadOnlyList<PresetBundle> Bundles { get; init; } = [];

    /// <summary>Gets the lineups that ship with the plugin.</summary>
    public static PresetCatalog Default => Bundled.Value;

    /// <summary>
    /// Reads a catalog.
    /// </summary>
    /// <param name="stream">JSON stream; null for an empty catalog.</param>
    /// <returns>The catalog.</returns>
    public static PresetCatalog Load(Stream? stream)
    {
        if (stream is null)
        {
            return new PresetCatalog();
        }

        using (stream)
        {
            return JsonSerializer.Deserialize<PresetCatalog>(stream, PackService.Json) ?? new PresetCatalog();
        }
    }
}

/// <summary>
/// A themed group of preset channels.
/// </summary>
public sealed class PresetBundle
{
    /// <summary>Gets the bundle id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the bundle name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets a description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the channels.</summary>
    public IReadOnlyList<PresetChannel> Channels { get; init; } = [];
}

/// <summary>
/// A preset channel.
/// </summary>
public sealed class PresetChannel
{
    /// <summary>Gets the suggested channel number.</summary>
    public string Number { get; init; } = string.Empty;

    /// <summary>Gets the name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets how many shows or movies the library needs for the channel to be worth suggesting.</summary>
    public int MinItems { get; init; } = 3;

    /// <summary>Gets what airs: Episode, Movie, MusicVideo.</summary>
    public IReadOnlyList<string> ItemTypes { get; init; } = ["Episode", "Movie"];

    /// <summary>Gets the content sources.</summary>
    public IReadOnlyList<ContentSource> Sources { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the channel airs only movies.
    /// </summary>
    public bool MoviesOnly => ItemTypes.Count == 1 && string.Equals(ItemTypes[0], "Movie", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether the channel airs only music videos.
    /// </summary>
    public bool MusicVideosOnly => ItemTypes.Count == 1 && string.Equals(ItemTypes[0], "MusicVideo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies the sources, so a channel built from them can be edited without touching the catalog.
    /// </summary>
    /// <returns>The copies.</returns>
    public ContentSource[] CopySources() => PackService.Clone(Sources.ToArray());
}
