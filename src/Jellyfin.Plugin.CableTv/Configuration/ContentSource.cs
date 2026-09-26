using System;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// One source feeding a channel's content pool.
/// </summary>
public class ContentSource
{
    /// <summary>
    /// Gets or sets the source type.
    /// </summary>
    public ContentSourceType Type { get; set; }

    /// <summary>
    /// Gets or sets the item ids (library, collection, playlist or item sources).
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public Guid[] Ids { get; set; } = [];

    /// <summary>
    /// Gets or sets the string values (genre names or decade start years).
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public string[] Values { get; set; } = [];

    /// <summary>
    /// Gets or sets how many times each item from this source airs per cycle (1–10). Only <see cref="ChannelSorting.Random"/> honours it.
    /// </summary>
    public int Weight { get; set; } = 1;

    /// <summary>
    /// Gets or sets restricted hours, "HH:mm-HH:mm" in local time (for example "21:00-05:00"): this source's items air only
    /// then. Empty for any time. An item also in an unrestricted source is not restricted.
    /// </summary>
    public string? AirHours { get; set; }
}
