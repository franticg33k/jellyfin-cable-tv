using System;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the fixed instant every schedule is computed from.
    /// </summary>
    public DateTime ScheduleAnchorUtc { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Gets or sets the configured channels.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public ChannelDefinition[] Channels { get; set; } = [];

    /// <summary>
    /// Gets or sets the commercial sources used by channels that don't set their own.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML-serialized configuration.")]
    public ContentSource[] CommercialSources { get; set; } = [];

    /// <summary>
    /// Gets or sets how the continuous Live TV stream treats video.
    /// </summary>
    public FallbackStreamMode FallbackMode { get; set; } = FallbackStreamMode.Auto;

    /// <summary>
    /// Gets or sets the frame height used when the stream transcodes (for example 720 or 1080).
    /// </summary>
    public int TranscodeHeight { get; set; } = 720;

    /// <summary>
    /// Gets or sets a value indicating whether the stream levels loudness with ffmpeg's loudnorm filter.
    /// </summary>
    public bool NormalizeLoudness { get; set; } = true;

    /// <summary>
    /// Gets or sets the secret the server's own ffmpeg uses to read channel streams. Generated on first start.
    /// </summary>
    public string StreamKey { get; set; } = string.Empty;
}
