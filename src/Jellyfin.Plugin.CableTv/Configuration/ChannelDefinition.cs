using System;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// A configured channel.
/// </summary>
public class ChannelDefinition
{
    /// <summary>
    /// Gets or sets the stable channel id. It seeds the schedule, so changing it reshuffles the channel.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel number shown in guides, for example "0412".
    /// </summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional logo URL.
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the channel is published.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how the pool is ordered.
    /// </summary>
    public ChannelSorting Sorting { get; set; } = ChannelSorting.Random;

    /// <summary>
    /// Gets or sets the item kinds admitted to the pool: Episode, Movie, MusicVideo, Video.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public string[] ItemTypes { get; set; } = ["Episode", "Movie"];

    /// <summary>
    /// Gets or sets a value indicating whether season-0 specials are admitted.
    /// </summary>
    public bool AllowSpecials { get; set; }

    /// <summary>
    /// Gets or sets the content sources; the pool is their union.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public ContentSource[] Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets an optional per-channel schedule anchor; the global anchor is used when unset.
    /// </summary>
    public DateTime? AnchorUtc { get; set; }
}
