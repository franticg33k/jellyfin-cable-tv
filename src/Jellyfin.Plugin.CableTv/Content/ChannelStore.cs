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
    public IChannelTimeline Preview(ChannelDefinition definition)
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

    private static TimeZoneInfo ResolveZone(string? id, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("Unknown time zone {Zone}; using the server's", id);
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// Slots and seasons air whatever their own sources hold, whatever item kinds the channel itself admits.
    /// </summary>
    private static ChannelDefinition AnyKind(ChannelDefinition definition)
    {
        definition.ItemTypes = ["Episode", "Movie", "MusicVideo", "Video"];
        return definition;
    }

    private ScheduleRules BuildRules(ChannelDefinition definition, PluginConfiguration config, IReadOnlyList<PoolItem> pool)
    {
        var slots = new List<TimeSlotRule>();
        foreach (var slot in definition.TimeSlots)
        {
            if (!TimeWindow.TryParseTime(slot.Start, out var start) || !TimeWindow.TryParseTime(slot.End, out var end))
            {
                _logger.LogWarning("Channel {Channel}: time slot {Slot} has an invalid time; skipped", definition.Id, slot.Name);
                continue;
            }

            var name = string.IsNullOrWhiteSpace(slot.Name) ? FormattableString.Invariant($"{slot.Start}-{slot.End}") : slot.Name;
            var slotPool = _resolver.Resolve(AnyKind(definition.WithSources(slot.Sources)));
            slots.Add(new TimeSlotRule(name, new TimeWindow(start, end), ScheduleRules.ParseDays(slot.Days), slotPool, slot.Sorting));
        }

        var seasons = new List<SeasonalRule>();
        foreach (var season in definition.Seasons)
        {
            if (!SeasonalRule.TryParseMonthDay(season.From, out var from) || !SeasonalRule.TryParseMonthDay(season.To, out var to))
            {
                _logger.LogWarning("Channel {Channel}: season {Season} has an invalid date; skipped", definition.Id, season.Name);
                continue;
            }

            var seasonal = _resolver.Resolve(AnyKind(definition.WithSources(season.Sources)));
            var mainIds = pool.Select(p => p.ItemId).ToHashSet();
            var seasonPool = season.Mode == SeasonMode.Replace
                ? seasonal
                : pool.Concat(seasonal.Where(s => !mainIds.Contains(s.ItemId))).ToList();
            var name = string.IsNullOrWhiteSpace(season.Name) ? season.From + " to " + season.To : season.Name;
            seasons.Add(new SeasonalRule(name, from, to, seasonPool));
        }

        // Restricted hours. An item that some unrestricted source also brings in may air any time.
        var restrictions = new List<RestrictionRule>();
        var restricted = definition.Sources.Where(s => !string.IsNullOrWhiteSpace(s.AirHours)).ToList();
        if (restricted.Count > 0)
        {
            var free = _resolver.Resolve(definition.WithSources(definition.Sources.Where(s => string.IsNullOrWhiteSpace(s.AirHours)).ToArray()))
                .Select(p => p.ItemId)
                .ToHashSet();
            foreach (var group in restricted.GroupBy(s => s.AirHours!.Trim(), StringComparer.Ordinal))
            {
                if (!TimeWindow.TryParse(group.Key, out var window))
                {
                    _logger.LogWarning("Channel {Channel}: invalid AirHours {Hours}; ignored", definition.Id, group.Key);
                    continue;
                }

                var ids = _resolver.Resolve(definition.WithSources(group.ToArray()))
                    .Select(p => p.ItemId)
                    .Where(id => !free.Contains(id))
                    .ToHashSet();
                if (ids.Count > 0)
                {
                    restrictions.Add(new RestrictionRule(ids, window));
                }
            }
        }

        PremiereRule? premiere = null;
        if (definition.Premieres is { Enabled: true } p && TimeWindow.TryParseTime(p.Time, out var at))
        {
            premiere = new PremiereRule(at, ScheduleRules.ParseDays(p.Days), Math.Clamp(p.WithinDays, 1, 60));
        }

        return new ScheduleRules
        {
            Zone = ResolveZone(config.TimeZone, _logger),
            Slots = slots,
            Seasons = seasons,
            Restrictions = restrictions,
            Premiere = premiere,
        };
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

        var anchor = definition.AnchorUtc ?? config.ScheduleAnchorUtc;
        var rules = BuildRules(definition, config, pool);
        IChannelTimeline timeline = rules.HasRules
            ? new RuledTimeline(definition.Id, anchor, pool, options, rules)
            : new ChannelTimeline(definition.Id, anchor, pool, options);
        var stream = StreamProfile.For(
            pool.Concat(rules.Slots.SelectMany(s => s.Pool)).Concat(rules.Seasons.SelectMany(s => s.Pool)),
            config.FallbackMode,
            config.TranscodeHeight,
            config.NormalizeLoudness);
        return new ChannelSnapshot(definition, timeline, stream);
    }
}
