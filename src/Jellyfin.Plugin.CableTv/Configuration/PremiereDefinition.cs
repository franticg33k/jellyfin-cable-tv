using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// Premieres: newly added items air at a fixed time, marked as new in the guide.
/// </summary>
public class PremiereDefinition
{
    /// <summary>Gets or sets a value indicating whether premieres are scheduled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the local time, "HH:mm".</summary>
    public string Time { get; set; } = "20:00";

    /// <summary>Gets or sets the days premieres run on; empty for every day.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public string[] Days { get; set; } = [];

    /// <summary>Gets or sets how many days after being added an item still counts as new.</summary>
    public int WithinDays { get; set; } = 7;
}
