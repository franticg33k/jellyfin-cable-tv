using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.CableTv.Scheduling;

namespace Jellyfin.Plugin.CableTv.Api;

// Property names are pinned with JsonPropertyName so the contract stays camelCase whatever
// naming policy the server or the client's Accept header selects. See docs/api-contract.md.

/// <summary>Response of <c>GET /CableTv/Channels</c>.</summary>
/// <param name="ServerTime">Server clock when the response was built.</param>
/// <param name="Channels">Channels ordered by number.</param>
public sealed record ChannelListResponse(
    [property: JsonPropertyName("serverTime")] DateTime ServerTime,
    [property: JsonPropertyName("channels")] IReadOnlyList<ChannelDto> Channels);

/// <summary>A channel.</summary>
/// <param name="ChannelId">Stable channel id.</param>
/// <param name="Number">Channel number.</param>
/// <param name="Name">Channel name.</param>
/// <param name="LogoUrl">Logo URL, if any.</param>
/// <param name="ScheduleVersion">Changes whenever the channel's timeline changes.</param>
/// <param name="PoolSize">Number of schedulable items; zero means the channel shows nothing.</param>
/// <param name="Category">Guide group, e.g. "Kids"; omitted when not set.</param>
/// <param name="Kind">"stream" or "weather" for those channels; omitted for library channels.</param>
public sealed record ChannelDto(
    [property: JsonPropertyName("channelId")] string ChannelId,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("logoUrl")] string? LogoUrl,
    [property: JsonPropertyName("scheduleVersion")] string ScheduleVersion,
    [property: JsonPropertyName("poolSize")] int PoolSize,
    [property: JsonPropertyName("category")] string? Category = null,
    [property: JsonPropertyName("kind")] string? Kind = null);

/// <summary>Response of <c>GET /CableTv/Schedule</c>.</summary>
/// <param name="ServerTime">Server clock when the response was built.</param>
/// <param name="From">Window start actually served.</param>
/// <param name="To">Window end actually served.</param>
/// <param name="ScheduleVersion">Combined version of every channel in the response.</param>
/// <param name="Channels">Per-channel slots.</param>
public sealed record ScheduleResponse(
    [property: JsonPropertyName("serverTime")] DateTime ServerTime,
    [property: JsonPropertyName("from")] DateTime From,
    [property: JsonPropertyName("to")] DateTime To,
    [property: JsonPropertyName("scheduleVersion")] string ScheduleVersion,
    [property: JsonPropertyName("channels")] IReadOnlyList<ChannelScheduleDto> Channels);

/// <summary>One channel's slots in a schedule window.</summary>
/// <param name="ChannelId">Channel id.</param>
/// <param name="Number">Channel number.</param>
/// <param name="ScheduleVersion">Channel timeline version.</param>
/// <param name="Slots">Slots overlapping the window, in order.</param>
public sealed record ChannelScheduleDto(
    [property: JsonPropertyName("channelId")] string ChannelId,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("scheduleVersion")] string ScheduleVersion,
    [property: JsonPropertyName("slots")] IReadOnlyList<SlotDto> Slots);

/// <summary>Response of <c>GET /CableTv/Now</c>.</summary>
/// <param name="ServerTime">Server clock the offset was computed at.</param>
/// <param name="ChannelId">Channel id.</param>
/// <param name="ScheduleVersion">Channel timeline version.</param>
/// <param name="Current">Slot airing at <paramref name="ServerTime"/>.</param>
/// <param name="OffsetMs">Position inside the item to start playback at (in-point plus time since slot start).</param>
/// <param name="Next">The slots after the current one, for preloading.</param>
public sealed record NowResponse(
    [property: JsonPropertyName("serverTime")] DateTime ServerTime,
    [property: JsonPropertyName("channelId")] string ChannelId,
    [property: JsonPropertyName("scheduleVersion")] string ScheduleVersion,
    [property: JsonPropertyName("current")] SlotDto Current,
    [property: JsonPropertyName("offsetMs")] long OffsetMs,
    [property: JsonPropertyName("next")] IReadOnlyList<SlotDto> Next);

/// <summary>Response of <c>GET /CableTv/Presentation</c>.</summary>
/// <param name="ServiceName">Name to brand the TV mode with.</param>
/// <param name="Channels">Per-channel branding.</param>
public sealed record PresentationResponse(
    [property: JsonPropertyName("serviceName")] string ServiceName,
    [property: JsonPropertyName("channels")] IReadOnlyList<ChannelBrandingDto> Channels);

/// <summary>Response of <c>POST /CableTv/Preview</c>.</summary>
/// <param name="ServerTime">Server clock when the preview was built.</param>
/// <param name="PoolSize">Blocks per cycle (weighted copies included); zero means the sources matched nothing.</param>
/// <param name="CycleHours">Length of one pass over the pool.</param>
/// <param name="ScheduleVersion">Version the channel would have once saved.</param>
/// <param name="Slots">The coming slots.</param>
public sealed record PreviewResponse(
    [property: JsonPropertyName("serverTime")] DateTime ServerTime,
    [property: JsonPropertyName("poolSize")] int PoolSize,
    [property: JsonPropertyName("cycleHours")] double CycleHours,
    [property: JsonPropertyName("scheduleVersion")] string ScheduleVersion,
    [property: JsonPropertyName("slots")] IReadOnlyList<SlotDto> Slots);

/// <summary>Per-channel branding.</summary>
/// <param name="ChannelId">Channel id.</param>
/// <param name="LogoUrl">Logo URL, if any.</param>
public sealed record ChannelBrandingDto(
    [property: JsonPropertyName("channelId")] string ChannelId,
    [property: JsonPropertyName("logoUrl")] string? LogoUrl);

/// <summary>Response of <c>GET /CableTv/Guide</c>.</summary>
/// <param name="ServerTime">Server clock when the response was built.</param>
/// <param name="From">Window start actually served.</param>
/// <param name="To">Window end actually served.</param>
/// <param name="Channels">Per-channel programmes.</param>
public sealed record GuideResponse(
    [property: JsonPropertyName("serverTime")] DateTime ServerTime,
    [property: JsonPropertyName("from")] DateTime From,
    [property: JsonPropertyName("to")] DateTime To,
    [property: JsonPropertyName("channels")] IReadOnlyList<ChannelGuideDto> Channels);

/// <summary>One channel's programmes in a guide window.</summary>
/// <param name="ChannelId">Channel id.</param>
/// <param name="ScheduleVersion">Channel timeline version.</param>
/// <param name="Programs">Programmes overlapping the window, breaks folded in.</param>
public sealed record ChannelGuideDto(
    [property: JsonPropertyName("channelId")] string ChannelId,
    [property: JsonPropertyName("scheduleVersion")] string ScheduleVersion,
    [property: JsonPropertyName("programs")] IReadOnlyList<GuideProgramDto> Programs);

/// <summary>A guide entry: one programme with its breaks, or an off-air stretch.</summary>
/// <param name="GuideGroup">Same value as the programme's slots carry.</param>
/// <param name="Start">Start (UTC).</param>
/// <param name="End">End (UTC), breaks included.</param>
/// <param name="ItemId">Programme item; omitted when off air.</param>
/// <param name="Title">Title; omitted when off air.</param>
/// <param name="Episode">"S02E05", for episodes.</param>
/// <param name="EpisodeTitle">Episode title.</param>
/// <param name="Premiere">True for a premiere.</param>
/// <param name="Lineup">Time slot or seasonal lineup name.</param>
/// <param name="Year">Production year.</param>
/// <param name="Rating">Official rating.</param>
/// <param name="Movie">True for a movie.</param>
/// <param name="Kind">"stream" or "generated" for non-library programmes; omitted otherwise.</param>
/// <param name="SeriesId">Series of an episode.</param>
/// <param name="Artist">Artist, for music.</param>
public sealed record GuideProgramDto(
    [property: JsonPropertyName("guideGroup")] string GuideGroup,
    [property: JsonPropertyName("start")] DateTime Start,
    [property: JsonPropertyName("end")] DateTime End,
    [property: JsonPropertyName("itemId")] string? ItemId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("episode")] string? Episode,
    [property: JsonPropertyName("episodeTitle")] string? EpisodeTitle,
    [property: JsonPropertyName("premiere")] bool? Premiere,
    [property: JsonPropertyName("lineup")] string? Lineup,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("rating")] string? Rating,
    [property: JsonPropertyName("movie")] bool? Movie,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("seriesId")] string? SeriesId = null,
    [property: JsonPropertyName("artist")] string? Artist = null)
{
    /// <summary>Maps an engine block to the wire format.</summary>
    /// <param name="block">Engine block.</param>
    /// <returns>The DTO.</returns>
    public static GuideProgramDto From(ScheduledBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var item = block.Item;
        var kind = block.Slots.Count > 0 ? block.Slots[0].Kind : SlotKind.Program;
        var special = kind is SlotKind.Stream or SlotKind.Generated;
        // A merged run of songs or trailers (GuideMerge) has no single item.
        var merged = item is { ItemId: var id } && id == Guid.Empty && !special;
        return new GuideProgramDto(
            block.Slots.Count > 0 ? block.Slots[0].GuideGroup : "g-" + block.BlockId,
            block.StartUtc,
            block.EndUtc,
            ItemIdOrNull(item),
            item?.Title,
            item?.EpisodeLabel,
            item?.EpisodeTitle,
            block.IsPremiere ? true : null,
            block.Lineup,
            item?.ProductionYear,
            string.IsNullOrWhiteSpace(item?.OfficialRating) ? null : item.OfficialRating,
            item?.IsMovie == true ? true : null,
            special ? kind.ToString().ToLowerInvariant() : merged ? (item!.IsAudio ? "music" : "trailers") : null,
            item?.SeriesId?.ToString("N", CultureInfo.InvariantCulture),
            item?.Artist);
    }

    /// <summary>The item id, or null for a synthetic item (a stream or a weather block).</summary>
    /// <param name="item">Item.</param>
    /// <returns>The id.</returns>
    internal static string? ItemIdOrNull(PoolItem? item)
        => item is null || item.ItemId == Guid.Empty ? null : item.ItemId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>A slot on a channel's timeline.</summary>
/// <param name="SlotId">Deterministic slot id.</param>
/// <param name="Kind">program, commercial, bumper, filler, stream or generated.</param>
/// <param name="Start">Wall-clock start (UTC).</param>
/// <param name="End">Wall-clock end (UTC).</param>
/// <param name="ItemId">Jellyfin item id to direct-play; null for filler.</param>
/// <param name="MediaSourceId">Media source id to direct-play; null for filler.</param>
/// <param name="InPointMs">Position in the item where the slot starts.</param>
/// <param name="OutPointMs">Position in the item where the slot ends.</param>
/// <param name="Title">Title of the item; null for filler.</param>
/// <param name="Episode">"S02E05" label, when the item is an episode.</param>
/// <param name="EpisodeTitle">Episode title.</param>
/// <param name="GuideGroup">Groups a programme with its breaks.</param>
/// <param name="Premiere">True for a premiere of a newly added item; omitted otherwise.</param>
/// <param name="Lineup">Name of the time slot or seasonal lineup; omitted for the main lineup.</param>
/// <param name="Year">Production year, when known.</param>
/// <param name="Url">For a stream slot: the URL to play.</param>
/// <param name="Audio">True for music: play the audio and show the visualiser.</param>
/// <param name="Artist">Artist, for music.</param>
/// <param name="Album">Album, for music.</param>
/// <param name="Trailer">True for a trailer.</param>
/// <param name="OwnerId">Movie or series a trailer belongs to.</param>
/// <param name="SeriesId">Series of an episode.</param>
/// <param name="Rating">Official rating, for example "TV-PG", when known.</param>
/// <param name="Movie">True for a movie; omitted otherwise.</param>
public sealed record SlotDto(
    [property: JsonPropertyName("slotId")] string SlotId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("start")] DateTime Start,
    [property: JsonPropertyName("end")] DateTime End,
    [property: JsonPropertyName("itemId")] string? ItemId,
    [property: JsonPropertyName("mediaSourceId")] string? MediaSourceId,
    [property: JsonPropertyName("inPointMs")] long InPointMs,
    [property: JsonPropertyName("outPointMs")] long OutPointMs,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("episode")] string? Episode,
    [property: JsonPropertyName("episodeTitle")] string? EpisodeTitle,
    [property: JsonPropertyName("guideGroup")] string GuideGroup,
    [property: JsonPropertyName("premiere")] bool? Premiere = null,
    [property: JsonPropertyName("lineup")] string? Lineup = null,
    [property: JsonPropertyName("year")] int? Year = null,
    [property: JsonPropertyName("rating")] string? Rating = null,
    [property: JsonPropertyName("movie")] bool? Movie = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("audio")] bool? Audio = null,
    [property: JsonPropertyName("artist")] string? Artist = null,
    [property: JsonPropertyName("album")] string? Album = null,
    [property: JsonPropertyName("trailer")] bool? Trailer = null,
    [property: JsonPropertyName("ownerId")] string? OwnerId = null,
    [property: JsonPropertyName("seriesId")] string? SeriesId = null)
{
    // The media source is the item itself unless it has several versions; then it's needed to pick the right one.
    private static string? MediaSourceOrNull(PoolItem? item)
        => item is null || Guid.TryParse(item.MediaSourceId, out var source) && source == item.ItemId ? null : item.MediaSourceId;

    /// <summary>Maps an engine slot to the wire format.</summary>
    /// <param name="slot">Engine slot.</param>
    /// <returns>The DTO.</returns>
    public static SlotDto From(ScheduledSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return new SlotDto(
            slot.SlotId,
            slot.Kind.ToString().ToLowerInvariant(),
            slot.StartUtc,
            slot.EndUtc,
            GuideProgramDto.ItemIdOrNull(slot.Item),
            slot.Kind is SlotKind.Stream or SlotKind.Generated ? null : MediaSourceOrNull(slot.Item),
            slot.InPointTicks / TimeSpan.TicksPerMillisecond,
            slot.OutPointTicks / TimeSpan.TicksPerMillisecond,
            slot.Item?.Title,
            slot.Item?.EpisodeLabel,
            slot.Item?.EpisodeTitle,
            slot.GuideGroup,
            slot.IsPremiere ? true : null,
            slot.Lineup,
            slot.Item?.ProductionYear,
            string.IsNullOrWhiteSpace(slot.Item?.OfficialRating) ? null : slot.Item.OfficialRating,
            slot.Item?.IsMovie == true ? true : null,
            slot.Kind == SlotKind.Stream ? slot.Item?.Path : null,
            slot.Item?.IsAudio == true ? true : null,
            slot.Item?.Artist,
            slot.Item?.Album,
            slot.Item?.IsTrailer == true ? true : null,
            slot.Item?.OwnerId?.ToString("N", CultureInfo.InvariantCulture),
            slot.Item?.SeriesId?.ToString("N", CultureInfo.InvariantCulture));
    }
}
