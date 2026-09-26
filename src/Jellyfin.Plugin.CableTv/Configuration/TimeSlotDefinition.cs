using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// A time slot: during its hours the channel airs different content, for example cartoons on Saturday mornings.
/// </summary>
public class TimeSlotDefinition
{
    /// <summary>Gets or sets the slot's name, shown as its lineup.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the local start time, "HH:mm".</summary>
    public string Start { get; set; } = "20:00";

    /// <summary>Gets or sets the local end time, "HH:mm"; at or before the start, the slot runs past midnight.</summary>
    public string End { get; set; } = "23:00";

    /// <summary>Gets or sets the days the slot starts on ("Mon", "Sat", …); empty for every day.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public string[] Days { get; set; } = [];

    /// <summary>Gets or sets how the slot's pool is ordered.</summary>
    public ChannelSorting Sorting { get; set; } = ChannelSorting.Random;

    /// <summary>Gets or sets the slot's content sources.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public ContentSource[] Sources { get; set; } = [];
}
