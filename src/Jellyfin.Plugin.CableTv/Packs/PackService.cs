using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Packs;

/// <summary>
/// Imports lineups (CSV), pinned episodes (CSV) and channel packs (JSON), and exports channels in both formats.
/// </summary>
/// <remarks>
/// An import never touches the saved configuration: it returns the channel list it would produce plus a report of what
/// each channel's titles matched, and the caller saves that list only when asked to. Matching runs against the shared
/// library index, so previewing a 5,000-row lineup costs dictionary lookups, not database queries.
/// </remarks>
public class PackService
{
    /// <summary>Largest import accepted, in characters.</summary>
    public const int MaxImportLength = 8 * 1024 * 1024;

    private const int MaxMissingListed = 100;

    /// <summary>JSON settings for packs: indented, enums as names, case-insensitive on read.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new LenientGuidConverter() },
    };

    private readonly ContentPoolResolver _resolver;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PackService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PackService"/> class.
    /// </summary>
    /// <param name="resolver">Pool resolver.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public PackService(ContentPoolResolver resolver, ILibraryManager libraryManager, ILogger<PackService> logger)
    {
        _resolver = resolver;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Works out what an import would do.
    /// </summary>
    /// <param name="request">Import request.</param>
    /// <param name="config">Current configuration (not modified).</param>
    /// <returns>The resulting channel list and a report.</returns>
    /// <exception cref="FormatException">The file can't be read.</exception>
    public ImportPlan Import(ImportRequest request, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        var content = request.Content ?? string.Empty;
        if (content.Length > MaxImportLength)
        {
            throw new FormatException("The file is too large (8 MB at most).");
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var context = _resolver.FreshContext();
        var channels = Clone(config.Channels).ToList();
        var trimmed = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
        ImportPlan plan;
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            plan = ImportPack(trimmed, request.Mode, channels, config, context);
        }
        else if (LineupCsv.IsEpisodes(content))
        {
            plan = ImportEpisodes(LineupCsv.ParseEpisodes(content), request, channels, context);
        }
        else
        {
            plan = ImportLineup(LineupCsv.ParseLineup(content), request, channels, config, context);
        }

        _logger.LogInformation(
            "Cable TV import ({Kind}, {Mode}): {Channels} channels, {Matched} matched, {Missing} missing, in {Elapsed} ms",
            plan.Kind,
            request.Mode,
            plan.Report.Count,
            plan.Matched,
            plan.Missing,
            started.ElapsedMilliseconds);
        return plan;
    }

    /// <summary>
    /// Exports channels as a JSON pack.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <param name="channelIds">Channels to export; all when empty.</param>
    /// <returns>The pack.</returns>
    public ChannelPack ExportPack(PluginConfiguration config, IReadOnlyCollection<string> channelIds)
    {
        ArgumentNullException.ThrowIfNull(config);
        var channels = Clone(Select(config.Channels, channelIds).ToArray());
        var all = channelIds.Count == 0;
        var commercials = all && config.CommercialSources.Length > 0 ? Clone(config.CommercialSources) : null;
        var references = new Dictionary<string, PackReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in AllSources(channels, commercials).SelectMany(s => s.Ids).Distinct())
        {
            if (_libraryManager.GetItemById(id) is { } item)
            {
                references[id.ToString("N", CultureInfo.InvariantCulture)] = new PackReference(item.Name ?? string.Empty, KindOf(item), item.ProductionYear);
            }
        }

        return new ChannelPack
        {
            Name = all ? "Cable TV channels" : string.Join(", ", channels.Select(c => c.Name)),
            Exported = DateTime.UtcNow,
            Channels = channels,
            CommercialSources = commercials,
            References = references.Count > 0 ? references : null,
        };
    }

    /// <summary>
    /// Exports channels as a lineup CSV: every show or movie each channel airs.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <param name="channelIds">Channels to export; all when empty.</param>
    /// <returns>CSV text.</returns>
    public string ExportCsv(PluginConfiguration config, IReadOnlyCollection<string> channelIds)
    {
        ArgumentNullException.ThrowIfNull(config);
        var context = _resolver.FreshContext();
        var rows = new List<LineupRow>();
        foreach (var channel in Select(config.Channels, channelIds))
        {
            rows.AddRange(_resolver.TitlesOf(channel, context).Select(t => new LineupRow(channel.Number, channel.Name, t.Title, t.Year)));
        }

        return LineupCsv.WriteLineup(rows);
    }

    /// <summary>
    /// Deep-copies configuration objects.
    /// </summary>
    /// <typeparam name="T">Type.</typeparam>
    /// <param name="value">Value.</param>
    /// <returns>The copy.</returns>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Json), Json)!;

    private static IEnumerable<ChannelDefinition> Select(IEnumerable<ChannelDefinition> channels, IReadOnlyCollection<string> ids)
        => ids.Count == 0 ? channels : channels.Where(c => ids.Contains(c.Id, StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<ContentSource> AllSources(IEnumerable<ChannelDefinition> channels, IEnumerable<ContentSource>? extra)
        => channels.SelectMany(c => c.Sources
                .Concat(c.CommercialSources)
                .Concat(c.TimeSlots.SelectMany(t => t.Sources))
                .Concat(c.Seasons.SelectMany(s => s.Sources)))
            .Concat(extra ?? []);

    private static string KindOf(BaseItem item) => item switch
    {
        CollectionFolder => "Library",
        BoxSet => "Collection",
        Playlist => "Playlist",
        Series => "Series",
        Movie => "Movie",
        Episode => "Episode",
        _ => item.GetBaseItemKind().ToString(),
    };

    private static ChannelDefinition? FindChannel(List<ChannelDefinition> channels, string name, string number, bool byNumber)
    {
        var id = ChannelDefaults.IdFor(name);
        return channels.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? channels.FirstOrDefault(c => string.Equals(c.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? (byNumber && number.Length > 0 ? channels.FirstOrDefault(c => string.Equals(c.Number.Trim(), number.Trim(), StringComparison.OrdinalIgnoreCase)) : null);
    }

    private static string TitleValue(LineupRow row)
        => row.Year is int year && TitleNormalizer.SplitYear(row.Title).Year is null
            ? FormattableString.Invariant($"{row.Title} ({year})")
            : row.Title;

    private static List<string> Limit(List<string> missing) => missing.Count > MaxMissingListed ? missing.GetRange(0, MaxMissingListed) : missing;

    private static SeasonDefinition UpsertSeason(ChannelDefinition channel, ImportRequest request)
    {
        var name = string.IsNullOrWhiteSpace(request.SeasonName) ? "Season" : request.SeasonName.Trim();
        var season = channel.Seasons.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (season is null)
        {
            season = new SeasonDefinition { Name = name };
            channel.Seasons = [.. channel.Seasons, season];
        }

        season.From = string.IsNullOrWhiteSpace(request.From) ? season.From : request.From.Trim();
        season.To = string.IsNullOrWhiteSpace(request.To) ? season.To : request.To.Trim();
        season.Mode = request.SeasonMode;
        return season;
    }

    private ImportPlan ImportLineup(List<LineupRow> rows, ImportRequest request, List<ChannelDefinition> channels, PluginConfiguration config, ResolveContext context)
    {
        if (rows.Count == 0)
        {
            throw new FormatException("The CSV has no rows with a channel and a title.");
        }

        var plan = new ImportPlan("lineup");
        var numbers = new ChannelDefaults.NumberAllocator(channels);
        var touched = new HashSet<ChannelDefinition>();
        var groups = rows.GroupBy(r => r.Number.Length > 0 ? r.Number + "\u0001" + r.Channel : r.Channel, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var first = group.First();
            var titles = group.Select(TitleValue).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var (found, missing) = _resolver.MatchTitles(titles, context);
            var titleSource = new ContentSource { Type = ContentSourceType.Titles, Values = titles.ToArray() };
            var existing = FindChannel(channels, first.Channel, first.Number, request.Mode == ImportMode.Season);
            string action;

            if (request.Mode == ImportMode.Season)
            {
                if (existing is null)
                {
                    plan.Warnings.Add($"No channel named {first.Channel} for the season lineup; skipped.");
                    plan.Report.Add(new ImportChannelReport(string.Empty, first.Number, first.Channel, "skipped", titles.Count, found.Count, Limit(missing)));
                    continue;
                }

                UpsertSeason(existing, request).Sources = [titleSource];
                action = "season";
            }
            else if (existing is not null)
            {
                // Keep the channel's schedule settings and other sources; its titles are replaced.
                existing.Sources = [.. existing.Sources.Where(s => s.Type != ContentSourceType.Titles), titleSource];
                action = "update";
            }
            else
            {
                var number = numbers.Take(first.Number, 1);
                if (first.Number.Length > 0 && number != first.Number.Trim())
                {
                    plan.Warnings.Add($"Channel number {first.Number} is taken; {first.Channel} gets {number}.");
                }

                existing = ChannelDefaults.Create(first.Channel, number, config.CommercialSources.Length > 0, titleSource);
                channels.Add(existing);
                action = "add";
            }

            touched.Add(existing);
            plan.Report.Add(new ImportChannelReport(existing.Id, existing.Number, existing.Name, action, titles.Count, found.Count, Limit(missing)));
            plan.Matched += found.Count;
            plan.Missing += missing.Count;
        }

        if (request.Mode == ImportMode.Replace)
        {
            foreach (var removed in channels.Where(c => !touched.Contains(c)).ToList())
            {
                channels.Remove(removed);
                plan.Report.Add(new ImportChannelReport(removed.Id, removed.Number, removed.Name, "remove", 0, 0, []));
            }
        }

        plan.Channels = channels.ToArray();
        return plan;
    }

    private ImportPlan ImportEpisodes(List<EpisodeRow> rows, ImportRequest request, List<ChannelDefinition> channels, ResolveContext context)
    {
        if (rows.Count == 0)
        {
            throw new FormatException("The CSV has no rows with a show and an episode title.");
        }

        var plan = new ImportPlan("episodes");
        var target = string.IsNullOrWhiteSpace(request.TargetChannelId)
            ? null
            : channels.FirstOrDefault(c => string.Equals(c.Id, request.TargetChannelId, StringComparison.OrdinalIgnoreCase));

        // Which channels carry each show, by their title sources.
        var carriers = new Dictionary<string, List<ChannelDefinition>>(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            foreach (var value in channel.Sources.Where(s => s.Type == ContentSourceType.Titles).SelectMany(s => s.Values))
            {
                var key = TitleNormalizer.Key(TitleNormalizer.SplitYear(value).Title);
                if (!carriers.TryGetValue(key, out var list))
                {
                    list = [];
                    carriers[key] = list;
                }

                if (!list.Contains(channel))
                {
                    list.Add(channel);
                }
            }
        }

        var perChannel = new Dictionary<ChannelDefinition, List<string>>();
        foreach (var row in rows)
        {
            var value = row.Show + " :: " + row.Episode;
            var key = TitleNormalizer.Key(TitleNormalizer.SplitYear(row.Show).Title);
            IEnumerable<ChannelDefinition> destinations = target is not null
                ? [target]
                : carriers.GetValueOrDefault(key) ?? [];
            var any = false;
            foreach (var channel in destinations)
            {
                any = true;
                if (!perChannel.TryGetValue(channel, out var list))
                {
                    list = [];
                    perChannel[channel] = list;
                }

                list.Add(value);
            }

            if (!any)
            {
                plan.Warnings.Add($"No channel carries {row.Show}; choose a target channel to add \"{row.Episode}\".");
            }
        }

        foreach (var (channel, values) in perChannel)
        {
            var season = UpsertSeason(channel, request);
            var source = season.Sources.FirstOrDefault(s => s.Type == ContentSourceType.Episodes);
            if (source is null)
            {
                // Pinned specials air more often than the rest of the lineup under Random sorting.
                source = new ContentSource { Type = ContentSourceType.Episodes, Weight = 3 };
                season.Sources = [.. season.Sources, source];
            }

            source.Values = source.Values.Concat(values).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var (found, missing) = _resolver.MatchEpisodes(values, context);
            plan.Report.Add(new ImportChannelReport(channel.Id, channel.Number, channel.Name, "season", values.Count, found.Count, Limit(missing)));
            plan.Matched += found.Count;
            plan.Missing += missing.Count;
        }

        plan.Channels = channels.ToArray();
        return plan;
    }

    private ImportPlan ImportPack(string json, ImportMode mode, List<ChannelDefinition> channels, PluginConfiguration config, ResolveContext context)
    {
        ChannelPack pack;
        try
        {
            pack = json.StartsWith('[')
                ? new ChannelPack { Channels = JsonSerializer.Deserialize<ChannelDefinition[]>(json, Json) ?? [] }
                : JsonSerializer.Deserialize<ChannelPack>(json, Json) ?? new ChannelPack();
        }
        catch (JsonException ex)
        {
            throw new FormatException("The JSON isn't a Cable TV channel pack: " + ex.Message, ex);
        }

        if (!string.Equals(pack.Format, ChannelPack.FormatName, StringComparison.OrdinalIgnoreCase) || pack.Channels.Length == 0)
        {
            throw new FormatException("The JSON isn't a Cable TV channel pack, or it has no channels.");
        }

        var plan = new ImportPlan("pack");
        RemapIds(pack, context, plan.Warnings);

        var existed = channels.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mode == ImportMode.Replace)
        {
            foreach (var removed in channels.Where(c => !pack.Channels.Any(p => string.Equals(p.Id, c.Id, StringComparison.OrdinalIgnoreCase))))
            {
                plan.Report.Add(new ImportChannelReport(removed.Id, removed.Number, removed.Name, "remove", 0, 0, []));
            }

            channels.Clear();
        }

        var numbers = new ChannelDefaults.NumberAllocator(channels);
        foreach (var channel in pack.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Name))
            {
                plan.Warnings.Add("Skipped a channel without a name.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(channel.Id))
            {
                channel.Id = ChannelDefaults.IdFor(channel.Name);
            }

            var index = channels.FindIndex(c => string.Equals(c.Id, channel.Id, StringComparison.OrdinalIgnoreCase));
            string action;
            if (index >= 0)
            {
                channels[index] = channel;
                action = "update";
            }
            else
            {
                var number = numbers.Take(channel.Number, 1);
                if (!string.IsNullOrWhiteSpace(channel.Number) && number != channel.Number.Trim())
                {
                    plan.Warnings.Add($"Channel number {channel.Number} is taken; {channel.Name} gets {number}.");
                }

                channel.Number = number;
                channels.Add(channel);
                action = existed.Contains(channel.Id) ? "update" : "add";
            }

            var titles = channel.Sources.Where(s => s.Type == ContentSourceType.Titles).SelectMany(s => s.Values).ToList();
            var (found, missing) = _resolver.MatchTitles(titles, context);
            plan.Report.Add(new ImportChannelReport(channel.Id, channel.Number, channel.Name, action, titles.Count, found.Count, Limit(missing)));
            plan.Matched += found.Count;
            plan.Missing += missing.Count;
        }

        if (pack.CommercialSources is { Length: > 0 } && (mode == ImportMode.Replace || config.CommercialSources.Length == 0))
        {
            plan.CommercialSources = pack.CommercialSources;
        }

        plan.Channels = channels.ToArray();
        return plan;
    }

    /// <summary>
    /// Points ids from another server at the item with the same name here; drops ids that can't be found.
    /// </summary>
    private void RemapIds(ChannelPack pack, ResolveContext context, List<string> warnings)
    {
        var references = pack.References ?? [];
        var map = new Dictionary<Guid, Guid?>();
        Dictionary<string, Guid>? libraries = null;

        foreach (var source in AllSources(pack.Channels, pack.CommercialSources))
        {
            if (source.Ids.Length == 0)
            {
                continue;
            }

            var ids = new List<Guid>(source.Ids.Length);
            foreach (var id in source.Ids)
            {
                if (!map.TryGetValue(id, out var local))
                {
                    local = _libraryManager.GetItemById(id) is not null ? id : null;
                    if (local is null && references.TryGetValue(id.ToString("N", CultureInfo.InvariantCulture), out var reference))
                    {
                        local = Find(reference, context, ref libraries);
                        if (local is null)
                        {
                            warnings.Add($"{reference.Kind} \"{reference.Name}\" isn't on this server; left out.");
                        }
                    }
                    else if (local is null)
                    {
                        warnings.Add($"Item {id:N} isn't on this server; left out.");
                    }

                    map[id] = local;
                }

                if (local is Guid found)
                {
                    ids.Add(found);
                }
            }

            source.Ids = ids.Distinct().ToArray();
        }
    }

    private Guid? Find(PackReference reference, ResolveContext context, ref Dictionary<string, Guid>? libraries)
    {
        switch (reference.Kind)
        {
            case "Library":
                libraries ??= _libraryManager.GetVirtualFolders()
                    .Where(f => Guid.TryParse(f.ItemId, out _))
                    .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => Guid.Parse(g.First().ItemId), StringComparer.OrdinalIgnoreCase);
                return libraries.TryGetValue(reference.Name, out var library) ? library : null;
            case "Collection":
            case "Playlist":
                return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [reference.Kind == "Collection" ? BaseItemKind.BoxSet : BaseItemKind.Playlist],
                    Name = reference.Name,
                    Recursive = true,
                    Limit = 1,
                }).FirstOrDefault()?.Id;
            default:
                var kind = reference.Kind switch
                {
                    "Series" => TitleKind.Series,
                    "Movie" => TitleKind.Movie,
                    "MusicVideo" => TitleKind.MusicVideo,
                    _ => (TitleKind?)null,
                };
                var matches = context.Index.Match(reference.Name, reference.Year);
                return (matches.FirstOrDefault(m => kind is null || m.Kind == kind) ?? matches.FirstOrDefault())?.Id;
        }
    }

    /// <summary>
    /// Reads ids with or without dashes (Jellyfin shows them without), writes them without.
    /// </summary>
    private sealed class LenientGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Guid.TryParse(reader.GetString(), out var id) ? id : throw new JsonException("Not an item id: " + reader.GetString());

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString("N", CultureInfo.InvariantCulture));
    }
}
