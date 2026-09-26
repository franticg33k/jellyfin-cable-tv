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
    /// Gets or sets the number of consecutive episodes per series for <see cref="ChannelSorting.Block"/>.
    /// </summary>
    public int BlockSize { get; set; } = 3;

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

    /// <summary>
    /// Gets or sets a value indicating whether commercial breaks are planned.
    /// </summary>
    public bool CommercialsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the grid in minutes each programme block is padded to (for example 30). 0 disables fill-to-grid.
    /// </summary>
    public int GridMinutes { get; set; }

    /// <summary>
    /// Gets or sets where programmes are split for a break.
    /// </summary>
    public MidBreakMode MidBreak { get; set; } = MidBreakMode.None;

    /// <summary>
    /// Gets or sets the length of each break in seconds when fill-to-grid is off.
    /// </summary>
    public int BreakSeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets the commercial sources for this channel; the global commercial sources are used when empty.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public ContentSource[] CommercialSources { get; set; } = [];

    /// <summary>
    /// Gets or sets time slots, where the channel airs different content; earlier slots win where they overlap.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public TimeSlotDefinition[] TimeSlots { get; set; } = [];

    /// <summary>
    /// Gets or sets seasonal lineups; earlier ones win where they overlap.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public SeasonDefinition[] Seasons { get; set; } = [];

    /// <summary>
    /// Gets or sets the premiere settings.
    /// </summary>
    public PremiereDefinition Premieres { get; set; } = new();

    /// <summary>
    /// Returns a copy of this channel with other content sources, for resolving a slot's or season's pool.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <returns>The copy.</returns>
    public ChannelDefinition WithSources(ContentSource[] sources)
    {
        var copy = (ChannelDefinition)MemberwiseClone();
        copy.Sources = sources;
        return copy;
    }
}
