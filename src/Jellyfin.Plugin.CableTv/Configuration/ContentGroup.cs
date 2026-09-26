using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// A named, reusable set of sources, for example "Saturday morning cartoons", that channels include with a
/// <see cref="ContentSourceType.Group"/> source.
/// </summary>
public class ContentGroup
{
    /// <summary>Gets or sets the group's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the group's sources.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public ContentSource[] Sources { get; set; } = [];
}
