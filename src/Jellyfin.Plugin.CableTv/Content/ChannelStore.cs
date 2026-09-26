using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Logos;
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
    private readonly LogoService _logos;
    private readonly ILogger<ChannelStore> _logger;
    private readonly Lock _rebuildLock = new();
    private IReadOnlyList<ChannelSnapshot>? _channels;
    private Dictionary<string, ChannelSnapshot> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelStore"/> class.
    /// </summary>
    /// <param name="resolver">Pool resolver.</param>
    /// <param name="logos">Logo service.</param>
    /// <param name="logger">Logger.</param>
    public ChannelStore(ContentPoolResolver resolver, LogoService logos, ILogger<ChannelStore> logger)
    {
        _resolver = resolver;
        _logos = logos;
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
    {
        _ = Channels;
        return Volatile.Read(ref _byId).GetValueOrDefault(channelId);
    }

    /// <summary>
    /// Builds a channel's timeline from the library without publishing it, for previews of unsaved settings.
    /// </summary>
    /// <param name="definition">Channel definition.</param>
    /// <returns>The timeline.</returns>
    public IChannelTimeline Preview(ChannelDefinition definition)
        => Build(definition, Plugin.Instance?.Configuration ?? new PluginConfiguration(), _resolver.SharedContext()).Timeline;

    /// <summary>
    /// Re-resolves every enabled channel's pool from the current configuration.
    /// </summary>
    /// <returns>The new snapshots.</returns>
    public IReadOnlyList<ChannelSnapshot> Rebuild()
    {
        lock (_rebuildLock)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var channels = new List<ChannelSnapshot>();
            var context = _resolver.CreateContext();
            _logos.ClearCache();

            foreach (var definition in config.Channels.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Id)))
            {
                try
                {
                    var snapshot = Build(definition, config, context) with { Logo = ResolveLogo(definition, config) };
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
            Volatile.Write(ref _byId, ordered.GroupBy(c => c.Definition.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));
            Volatile.Write(ref _channels, ordered);
            _resolver.Trim(context);
            _resolver.ShareContext(context);
            _logger.LogInformation("Built {Count} Cable TV channels in {Elapsed} ms", ordered.Count, started.ElapsedMilliseconds);
            return ordered;
        }
    }

    private LogoRef? ResolveLogo(ChannelDefinition definition, PluginConfiguration config)
    {
        try
        {
            return _logos.Resolve(definition, config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't find a logo for channel {Id}", definition.Id);
            return null;
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

    private ScheduleRules BuildRules(ChannelDefinition definition, PluginConfiguration config, IReadOnlyList<PoolItem> pool, ResolveContext context)
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
            var slotPool = _resolver.Resolve(AnyKind(definition.WithSources(slot.Sources)), context);
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

            var seasonal = _resolver.Resolve(AnyKind(definition.WithSources(season.Sources)), context);
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
            var free = _resolver.Resolve(definition.WithSources(definition.Sources.Where(s => string.IsNullOrWhiteSpace(s.AirHours)).ToArray()), context)
                .Select(p => p.ItemId)
                .ToHashSet();
            foreach (var group in restricted.GroupBy(s => s.AirHours!.Trim(), StringComparer.Ordinal))
            {
                if (!TimeWindow.TryParse(group.Key, out var window))
                {
                    _logger.LogWarning("Channel {Channel}: invalid AirHours {Hours}; ignored", definition.Id, group.Key);
                    continue;
                }

                var ids = _resolver.Resolve(definition.WithSources(group.ToArray()), context)
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

    private static ChannelSnapshot BuildSpecial(ChannelDefinition definition, PluginConfiguration config)
    {
        var anchor = definition.AnchorUtc ?? config.ScheduleAnchorUtc;
        var profile = StreamProfile.For([], FallbackStreamMode.Transcode, config.TranscodeHeight, config.NormalizeLoudness);
        IChannelTimeline timeline = definition.Kind switch
        {
            // A stream is one long programme; hour blocks keep guides tidy.
            ChannelKind.Stream => new RepeatingTimeline(
                definition.Id,
                anchor,
                SlotKind.Stream,
                new PoolItem(Guid.Empty, string.Empty, 0, definition.Name) { Path = definition.StreamUrl?.Trim(), EpisodeTitle = "Live" },
                TimeSpan.FromHours(1)),
            _ => new RepeatingTimeline(
                definition.Id,
                anchor,
                SlotKind.Generated,
                new PoolItem(Guid.Empty, string.Empty, 0, "Local Forecast")
                {
                    EpisodeTitle = string.IsNullOrWhiteSpace(definition.WeatherLocation) ? null : definition.WeatherLocation.Trim(),
                },
                TimeSpan.FromMinutes(30)),
        };
        return new ChannelSnapshot(definition, timeline, profile);
    }

    private ChannelSnapshot Build(ChannelDefinition definition, PluginConfiguration config, ResolveContext context)
    {
        if (definition.Kind != ChannelKind.Standard)
        {
            return BuildSpecial(definition, config);
        }

        var pool = _resolver.Resolve(definition, context);

        IReadOnlyList<PoolItem> commercials = [];
        if (definition.CommercialsEnabled)
        {
            var sources = definition.CommercialSources.Length > 0 ? definition.CommercialSources : config.CommercialSources;
            commercials = _resolver.ResolveCommercials(sources, context);
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
        var rules = BuildRules(definition, config, pool, context);
        IChannelTimeline timeline = rules.HasRules
            ? new RuledTimeline(definition.Id, anchor, pool, options, rules)
            : new ChannelTimeline(definition.Id, anchor, pool, options);
        var stream = StreamProfile.For(
            pool.Concat(rules.Slots.SelectMany(s => s.Pool)).Concat(rules.Seasons.SelectMany(s => s.Pool)),
            config.FallbackMode,
            config.TranscodeHeight,
            config.NormalizeLoudness);
        return new ChannelSnapshot(definition, timeline, stream) { Commercials = commercials };
    }
}
