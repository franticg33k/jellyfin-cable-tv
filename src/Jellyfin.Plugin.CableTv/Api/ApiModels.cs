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
public sealed record ChannelDto(
    [property: JsonPropertyName("channelId")] string ChannelId,
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("logoUrl")] string? LogoUrl,
    [property: JsonPropertyName("scheduleVersion")] string ScheduleVersion,
    [property: JsonPropertyName("poolSize")] int PoolSize);

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
    [property: JsonPropertyName("lineup")] string? Lineup = null)
{
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
            slot.Item?.ItemId.ToString("N", CultureInfo.InvariantCulture),
            slot.Item?.MediaSourceId,
            slot.InPointTicks / TimeSpan.TicksPerMillisecond,
            slot.OutPointTicks / TimeSpan.TicksPerMillisecond,
            slot.Item?.Title,
            slot.Item?.EpisodeLabel,
            slot.Item?.EpisodeTitle,
            slot.GuideGroup,
            slot.IsPremiere ? true : null,
            slot.Lineup);
    }
}
