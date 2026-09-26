using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Packs;

/// <summary>
/// What an import does with the channels already configured.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImportMode>))]
public enum ImportMode
{
    /// <summary>Add new channels and update matching ones (same id or name); keep the rest.</summary>
    Merge = 0,

    /// <summary>The imported channels become the whole lineup.</summary>
    Replace = 1,

    /// <summary>A CSV lineup becomes a seasonal lineup on the matching existing channels.</summary>
    Season = 2,
}

/// <summary>
/// Body of <c>POST /CableTv/Import</c>.
/// </summary>
public sealed class ImportRequest
{
    /// <summary>Gets or sets the file's text: a lineup CSV, an episodes CSV, or a JSON channel pack.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Gets or sets the import mode.</summary>
    [JsonPropertyName("mode")]
    public ImportMode Mode { get; set; }

    /// <summary>Gets or sets the season name, for season lineups and episodes.</summary>
    [JsonPropertyName("seasonName")]
    public string? SeasonName { get; set; }

    /// <summary>Gets or sets the season start, "MM-DD".</summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }

    /// <summary>Gets or sets the season end, "MM-DD" (inclusive).</summary>
    [JsonPropertyName("to")]
    public string? To { get; set; }

    /// <summary>Gets or sets whether the season lineup replaces or joins the regular one.</summary>
    [JsonPropertyName("seasonMode")]
    [JsonConverter(typeof(JsonStringEnumConverter<SeasonMode>))]
    public SeasonMode SeasonMode { get; set; } = SeasonMode.Replace;

    /// <summary>Gets or sets the channel pinned episodes go to; by default every channel carrying the show.</summary>
    [JsonPropertyName("targetChannelId")]
    public string? TargetChannelId { get; set; }

    /// <summary>Gets or sets a value indicating whether to save the result; otherwise only the report is returned.</summary>
    [JsonPropertyName("apply")]
    public bool Apply { get; set; }
}

/// <summary>
/// What an import does to one channel.
/// </summary>
/// <param name="ChannelId">Channel id.</param>
/// <param name="Number">Channel number.</param>
/// <param name="Name">Channel name.</param>
/// <param name="Action">add, update, season, remove or skipped.</param>
/// <param name="Titles">Titles (or pinned episodes) imported for the channel.</param>
/// <param name="Matched">How many of them the library has.</param>
/// <param name="Missing">The ones it doesn't (at most 100 listed); they won't air.</param>
public sealed record ImportChannelReport(
    [property: JsonPropertyName("channelId")] string ChannelId,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("titles")] int Titles,
    [property: JsonPropertyName("matched")] int Matched,
    [property: JsonPropertyName("missing")] IReadOnlyList<string> Missing);

/// <summary>
/// The outcome of an import: the channel list it produces and a report.
/// </summary>
/// <param name="kind">lineup, episodes or pack.</param>
public sealed class ImportPlan(string kind)
{
    /// <summary>Gets what was imported: lineup, episodes or pack.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; } = kind;

    /// <summary>Gets the per-channel report.</summary>
    [JsonPropertyName("channels")]
    public List<ImportChannelReport> Report { get; } = [];

    /// <summary>Gets problems worth showing.</summary>
    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; } = [];

    /// <summary>Gets or sets how many titles matched the library.</summary>
    [JsonPropertyName("matched")]
    public int Matched { get; set; }

    /// <summary>Gets or sets how many titles the library doesn't have.</summary>
    [JsonPropertyName("missing")]
    public int Missing { get; set; }

    /// <summary>Gets or sets a value indicating whether the result was saved.</summary>
    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    /// <summary>Gets or sets the full channel list after the import.</summary>
    [JsonIgnore]
    public ChannelDefinition[] Channels { get; set; } = [];

    /// <summary>Gets or sets new global commercial sources, when the pack brings them.</summary>
    [JsonIgnore]
    public ContentSource[]? CommercialSources { get; set; }
}
