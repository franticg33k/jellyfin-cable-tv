using System;
using System.Collections.Generic;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Packs;

/// <summary>
/// A shareable set of channels: the JSON export format.
/// </summary>
/// <remarks>
/// Channels are stored exactly as configured. Sources that point at items by id (libraries, collections, playlists,
/// items) only mean something on the server they came from, so <see cref="References"/> records each id's name, and an
/// import on another server finds the item with the same name there.
/// </remarks>
public sealed class ChannelPack
{
    /// <summary>The value of <see cref="Format"/>.</summary>
    public const string FormatName = "cabletv-pack";

    /// <summary>Gets or sets the format marker.</summary>
    public string Format { get; set; } = FormatName;

    /// <summary>Gets or sets the format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the pack's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets a description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets when the pack was exported.</summary>
    public DateTime? Exported { get; set; }

    /// <summary>Gets or sets the channels.</summary>
    public ChannelDefinition[] Channels { get; set; } = [];

    /// <summary>Gets or sets the global commercial sources, if exported.</summary>
    public ContentSource[]? CommercialSources { get; set; }

    /// <summary>Gets or sets what each item id in the sources refers to, keyed by id.</summary>
    public Dictionary<string, PackReference>? References { get; set; }
}

/// <summary>
/// What an item id in a pack refers to, so it can be found again on another server.
/// </summary>
/// <param name="Name">Item name.</param>
/// <param name="Kind">Library, Collection, Playlist, Series, Movie, Episode, MusicVideo or Video.</param>
/// <param name="Year">Production year, if known.</param>
public sealed record PackReference(string Name, string Kind, int? Year = null);
