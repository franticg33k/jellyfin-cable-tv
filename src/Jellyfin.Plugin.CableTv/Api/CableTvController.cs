using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CableTv.Api;

/// <summary>
/// Read-only API for clients that assemble the channel on the device (direct play at an offset, preloading).
/// </summary>
[ApiController]
[Authorize]
[Route("CableTv")]
[Produces("application/json")]
public class CableTvController : ControllerBase
{
    /// <summary>Default schedule window when <c>to</c> is omitted.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(6);

    /// <summary>Largest schedule window one request may ask for.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(3);

    private const int MaxNext = 10;

    private static readonly TimeSpan NowLookahead = TimeSpan.FromDays(30);

    private readonly ChannelStore _store;
    private readonly GuideRefresher _refresher;

    /// <summary>
    /// Initializes a new instance of the <see cref="CableTvController"/> class.
    /// </summary>
    /// <param name="store">Channel store.</param>
    /// <param name="refresher">Guide refresher.</param>
    public CableTvController(ChannelStore store, GuideRefresher refresher)
    {
        _store = store;
        _refresher = refresher;
    }

    /// <summary>
    /// Lists the channels.
    /// </summary>
    /// <returns>The channels, ordered by number.</returns>
    [HttpGet("Channels")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ChannelListResponse> GetChannels()
    {
        var channels = _store.Channels
            .Select(c => new ChannelDto(
                c.Definition.Id,
                c.Definition.Number,
                c.Definition.Name,
                NullIfBlank(c.Definition.LogoUrl),
                c.Timeline.Version,
                c.Timeline.PoolSize))
            .ToList();

        return new ChannelListResponse(DateTime.UtcNow, channels);
    }

    /// <summary>
    /// Returns resolved slots for a time window.
    /// </summary>
    /// <param name="channelIds">Comma-separated channel ids; all channels when omitted.</param>
    /// <param name="from">Window start (UTC); now when omitted.</param>
    /// <param name="to">Window end (UTC); six hours after <paramref name="from"/> when omitted. At most three days after it.</param>
    /// <returns>The schedule.</returns>
    [HttpGet("Schedule")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ScheduleResponse> GetSchedule(
        [FromQuery] string? channelIds,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var now = DateTime.UtcNow;
        var start = from?.ToUniversalTime() ?? now;
        var end = to?.ToUniversalTime() ?? start + DefaultWindow;
        if (end <= start)
        {
            return BadRequest("'to' must be after 'from'.");
        }

        if (end - start > MaxWindow)
        {
            end = start + MaxWindow;
        }

        IEnumerable<ChannelSnapshot> channels = _store.Channels;
        if (!string.IsNullOrWhiteSpace(channelIds))
        {
            var wanted = channelIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            channels = channels.Where(c => wanted.Contains(c.Definition.Id));
        }

        var result = channels
            .Select(c => new ChannelScheduleDto(
                c.Definition.Id,
                c.Definition.Number,
                c.Timeline.Version,
                c.Timeline.GetSlots(start, end).Select(SlotDto.From).ToList()))
            .ToList();

        var combined = result.Aggregate(StableHash.Start(), (h, c) => StableHash.Add(StableHash.Add(h, c.ChannelId), c.ScheduleVersion));
        return new ScheduleResponse(now, start, end, StableHash.ToHex(combined, 12), result);
    }

    /// <summary>
    /// Returns what a channel is airing now, the offset to start at, and the next slots to preload.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <param name="next">How many following slots to include (0–10).</param>
    /// <returns>The airing slot.</returns>
    [HttpGet("Now")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<NowResponse> GetNow([FromQuery, Required] string channelId, [FromQuery] int next = 3)
    {
        var channel = _store.Get(channelId);
        if (channel is null)
        {
            return NotFound($"Unknown channel '{channelId}'.");
        }

        var now = DateTime.UtcNow;
        var count = Math.Clamp(next, 0, MaxNext);

        // Slots are enumerated lazily, so the long window costs only the slots actually taken.
        var slots = channel.Timeline.GetSlots(now, now + NowLookahead).Take(count + 1).ToList();
        if (slots.Count == 0)
        {
            return NotFound($"Channel '{channelId}' has no content.");
        }

        var current = slots[0];
        var offsetTicks = current.InPointTicks + (now - current.StartUtc).Ticks;
        return new NowResponse(
            now,
            channel.Definition.Id,
            channel.Timeline.Version,
            SlotDto.From(current),
            offsetTicks / TimeSpan.TicksPerMillisecond,
            slots.Skip(1).Select(SlotDto.From).ToList());
    }

    /// <summary>
    /// Returns server-set branding and defaults.
    /// </summary>
    /// <returns>The presentation settings.</returns>
    [HttpGet("Presentation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PresentationResponse> GetPresentation()
    {
        var channels = _store.Channels
            .Select(c => new ChannelBrandingDto(c.Definition.Id, NullIfBlank(c.Definition.LogoUrl)))
            .ToList();
        return new PresentationResponse("Cable TV", channels);
    }

    /// <summary>
    /// Rebuilds every channel from the library now and refreshes the Live TV guide. Administrators only.
    /// </summary>
    /// <returns>The rebuilt channels.</returns>
    [HttpPost("Rebuild")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ChannelListResponse> Rebuild()
    {
        _refresher.RebuildAndRefreshGuide();
        return GetChannels();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
