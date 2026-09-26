using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Scheduling;
using Jellyfin.Plugin.CableTv.Streaming;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// Holds the current channel snapshots.
/// </summary>
/// <remarks>
/// Pools are resolved only on a rebuild (configuration save, the scheduled task, or first use), never per request.
/// That keeps the schedule stable between rebuilds even as the library changes underneath it.
/// </remarks>
public class ChannelStore
{
    private readonly ContentPoolResolver _resolver;
    private readonly ILogger<ChannelStore> _logger;
    private readonly Lock _rebuildLock = new();
    private IReadOnlyList<ChannelSnapshot>? _channels;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelStore"/> class.
    /// </summary>
    /// <param name="resolver">Pool resolver.</param>
    /// <param name="logger">Logger.</param>
    public ChannelStore(ContentPoolResolver resolver, ILogger<ChannelStore> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Gets the enabled channels ordered by number, building them on first use.
    /// </summary>
    public IReadOnlyList<ChannelSnapshot> Channels => Volatile.Read(ref _channels) ?? Rebuild();

    /// <summary>
    /// Gets a channel by id.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <returns>The channel, or null.</returns>
    public ChannelSnapshot? Get(string channelId)
        => Channels.FirstOrDefault(c => string.Equals(c.Definition.Id, channelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds a channel's timeline from the library without publishing it, for previews of unsaved settings.
    /// </summary>
    /// <param name="definition">Channel definition.</param>
    /// <returns>The timeline.</returns>
    public ChannelTimeline Preview(ChannelDefinition definition)
        => Build(definition, Plugin.Instance?.Configuration ?? new PluginConfiguration()).Timeline;

    /// <summary>
    /// Re-resolves every enabled channel's pool from the current configuration.
    /// </summary>
    /// <returns>The new snapshots.</returns>
    public IReadOnlyList<ChannelSnapshot> Rebuild()
    {
        lock (_rebuildLock)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var channels = new List<ChannelSnapshot>();

            foreach (var definition in config.Channels.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Id)))
            {
                try
                {
                    var snapshot = Build(definition, config);
                    var timeline = snapshot.Timeline;
                    channels.Add(snapshot);
                    _logger.LogInformation(
                        "Channel {Number} {Name}: {Count} items, version {Version}",
                        definition.Number,
                        definition.Name,
                        timeline.PoolSize,
                        timeline.Version);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to build channel {Id}", definition.Id);
                }
            }

            var ordered = channels
                .OrderBy(c => ChannelNumber.SortKey(c.Definition.Number))
                .ThenBy(c => c.Definition.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Volatile.Write(ref _channels, ordered);
            return ordered;
        }
    }

    private ChannelSnapshot Build(ChannelDefinition definition, PluginConfiguration config)
    {
        var pool = _resolver.Resolve(definition);

        IReadOnlyList<PoolItem> commercials = [];
        if (definition.CommercialsEnabled)
        {
            var sources = definition.CommercialSources.Length > 0 ? definition.CommercialSources : config.CommercialSources;
            commercials = _resolver.ResolveCommercials(sources);
        }

        var options = new TimelineOptions
        {
            Sorting = definition.Sorting,
            BlockSize = definition.BlockSize,
            Commercials = commercials,
            Grid = definition.CommercialsEnabled ? TimeSpan.FromMinutes(Math.Clamp(definition.GridMinutes, 0, 240)) : TimeSpan.Zero,
            MidBreak = definition.CommercialsEnabled ? definition.MidBreak : MidBreakMode.None,
            BreakLength = definition.CommercialsEnabled ? TimeSpan.FromSeconds(Math.Clamp(definition.BreakSeconds, 0, 1800)) : TimeSpan.Zero,
        };

        var timeline = new ChannelTimeline(definition.Id, definition.AnchorUtc ?? config.ScheduleAnchorUtc, pool, options);
        var stream = StreamProfile.For(pool, config.FallbackMode, config.TranscodeHeight, config.NormalizeLoudness);
        return new ChannelSnapshot(definition, timeline, stream);
    }
}
