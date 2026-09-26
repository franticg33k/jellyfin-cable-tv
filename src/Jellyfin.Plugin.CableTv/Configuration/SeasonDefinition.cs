using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// A seasonal or holiday lineup, for example Christmas specials from December 1 to 26.
/// </summary>
public class SeasonDefinition
{
    /// <summary>Gets or sets the lineup's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the first day, "MM-dd".</summary>
    public string From { get; set; } = "12-01";

    /// <summary>Gets or sets the last day, "MM-dd"; before <see cref="From"/>, the season wraps the new year.</summary>
    public string To { get; set; } = "12-26";

    /// <summary>Gets or sets whether the lineup replaces the channel's usual content or mixes into it.</summary>
    public SeasonMode Mode { get; set; } = SeasonMode.Mix;

    /// <summary>Gets or sets the lineup's content sources.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public ContentSource[] Sources { get; set; } = [];
}
