using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Packs;
using Jellyfin.Plugin.CableTv.Scheduling;
using Jellyfin.Plugin.CableTv.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
    private readonly StreamManager _streams;
    private readonly PackService _packs;
    private readonly ChannelSuggester _suggester;
    private readonly Weather.WeatherService _weather;

    private static readonly System.Text.Json.JsonSerializerOptions WeatherJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Initializes a new instance of the <see cref="CableTvController"/> class.
    /// </summary>
    /// <param name="store">Channel store.</param>
    /// <param name="refresher">Guide refresher.</param>
    /// <param name="streams">Stream manager.</param>
    /// <param name="packs">Import and export.</param>
    /// <param name="suggester">Channel suggestions.</param>
    /// <param name="weather">Weather forecasts.</param>
    public CableTvController(ChannelStore store, GuideRefresher refresher, StreamManager streams, PackService packs, ChannelSuggester suggester, Weather.WeatherService weather)
    {
        _weather = weather;
        _store = store;
        _refresher = refresher;
        _streams = streams;
        _packs = packs;
        _suggester = suggester;
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
                LogoUrl(c),
                c.Timeline.Version,
                c.Timeline.PoolSize,
                string.IsNullOrWhiteSpace(c.Definition.Category) ? null : c.Definition.Category.Trim(),
                c.Definition.Kind == ChannelKind.Standard ? null : c.Definition.Kind.ToString().ToLowerInvariant()))
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

        var result = SelectChannels(channelIds)
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
    /// Returns programmes for a time window, breaks folded in: what a guide needs, a fraction of the size of the
    /// full schedule.
    /// </summary>
    /// <param name="channelIds">Comma-separated channel ids; all channels when omitted.</param>
    /// <param name="from">Window start (UTC); now when omitted.</param>
    /// <param name="to">Window end (UTC); six hours after <paramref name="from"/> when omitted. At most three days after it.</param>
    /// <returns>The guide.</returns>
    [HttpGet("Guide")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<GuideResponse> GetGuide(
        [FromQuery] string? channelIds,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        var start = from?.ToUniversalTime() ?? DateTime.UtcNow;
        var end = to?.ToUniversalTime() ?? start + DefaultWindow;
        if (end <= start)
        {
            return BadRequest("'to' must be after 'from'.");
        }

        if (end - start > MaxWindow)
        {
            end = start + MaxWindow;
        }

        var result = SelectChannels(channelIds)
            .Select(c => new ChannelGuideDto(
                c.Definition.Id,
                c.Timeline.Version,
                GuideMerge.Merge(c.Timeline.GetBlocks(start, end), c.Definition.Name).Select(GuideProgramDto.From).ToList()))
            .ToList();
        return new GuideResponse(DateTime.UtcNow, start, end, result);
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
            .Select(c => new ChannelBrandingDto(c.Definition.Id, LogoUrl(c)))
            .ToList();
        var name = Plugin.Instance?.Configuration.ServiceName;
        return new PresentationResponse(string.IsNullOrWhiteSpace(name) ? "Cable TV" : name.Trim(), channels);
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

    /// <summary>
    /// Previews a channel from unsaved settings: resolves its pool and returns the coming slots. Administrators only.
    /// </summary>
    /// <param name="channel">Channel settings as edited, not yet saved.</param>
    /// <param name="hours">Hours to preview from now (1–48).</param>
    /// <returns>The preview.</returns>
    [HttpPost("Preview")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PreviewResponse> Preview([FromBody, Required] ChannelDefinition channel, [FromQuery] int hours = 6)
    {
        if (string.IsNullOrWhiteSpace(channel.Id))
        {
            return BadRequest("The channel needs an Id.");
        }

        var timeline = _store.Preview(channel);
        var now = DateTime.UtcNow;
        var slots = timeline.GetSlots(now, now.AddHours(Math.Clamp(hours, 1, 48))).Select(SlotDto.From).ToList();
        return new PreviewResponse(now, timeline.PoolSize, TimeSpan.FromTicks(timeline.CycleTicks).TotalHours, timeline.Version, slots);
    }

    /// <summary>
    /// A channel's logo: its own image, the TV network's logo from Jellyfin, or a generated one. Anonymous so image
    /// tags and Live TV clients can load it.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <returns>The image, or a redirect to it.</returns>
    [HttpGet("Logo/{channelId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetLogo([FromRoute] string channelId)
    {
        var logo = _store.Get(channelId)?.Logo;
        if (logo?.RemoteUrl is { } url)
        {
            return Redirect(url);
        }

        if (logo?.LocalPath is not { } path || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        // Logo URLs carry a version that changes with the image, so a versioned request can be cached for good.
        Response.Headers.CacheControl = Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "public, max-age=86400";
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            _ => "image/png",
        };
        return PhysicalFile(path, type);
    }

    /// <summary>
    /// The forecast for a weather channel, for clients that draw the weather themselves.
    /// </summary>
    /// <param name="channelId">Weather channel id.</param>
    /// <returns>The forecast (camelCase JSON).</returns>
    [HttpGet("Weather/{channelId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetWeather([FromRoute] string channelId)
    {
        var channel = _store.Get(channelId);
        if (channel?.Definition.Kind != ChannelKind.Weather)
        {
            return NotFound();
        }

        var report = await _weather.GetAsync(channel.Definition, HttpContext.RequestAborted).ConfigureAwait(false);
        if (report is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "No forecast yet; check the channel's location.");
        }

        return Content(System.Text.Json.JsonSerializer.Serialize(report, WeatherJson), "application/json");
    }

    /// <summary>
    /// Suggests channels the library could fill: TV networks, genres, decades, kids, holidays and collections.
    /// Administrators only.
    /// </summary>
    /// <param name="minTitles">Fewest shows or movies a suggested channel draws from.</param>
    /// <returns>The suggestions.</returns>
    [HttpGet("Suggestions")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ChannelSuggestion>> GetSuggestions([FromQuery] int minTitles = 3)
        => Ok(_suggester.Suggest(Plugin.Instance?.Configuration ?? new PluginConfiguration(), minTitles));

    /// <summary>
    /// Imports a lineup CSV, an episodes CSV or a JSON channel pack. Without <c>apply</c> it only reports what would
    /// change and which titles the library has. Administrators only.
    /// </summary>
    /// <param name="request">The file and options.</param>
    /// <returns>The report.</returns>
    [HttpPost("Import")]
    [Authorize(Policy = "RequiresElevation")]
    [RequestSizeLimit(32 * 1024 * 1024)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ImportPlan> Import([FromBody, Required] ImportRequest request)
    {
        if (Plugin.Instance is not { } plugin)
        {
            return BadRequest("The plugin isn't loaded.");
        }

        ImportPlan plan;
        try
        {
            plan = _packs.Import(request, plugin.Configuration);
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }

        if (request.Apply)
        {
            var config = plugin.Configuration;
            config.Channels = plan.Channels;
            if (plan.CommercialSources is not null)
            {
                config.CommercialSources = plan.CommercialSources;
            }

            // Raises ConfigurationChanged, which rebuilds the channels in the background.
            plugin.UpdateConfiguration(config);
            plan.Applied = true;
        }

        return plan;
    }

    /// <summary>
    /// Exports channels as a JSON channel pack or a lineup CSV. Administrators only.
    /// </summary>
    /// <param name="format">json (default) or csv.</param>
    /// <param name="channelIds">Comma-separated channel ids; all channels when omitted.</param>
    /// <returns>The file.</returns>
    [HttpGet("Export")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Export([FromQuery] string? format, [FromQuery] string? channelIds)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var ids = (channelIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var stamp = DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return File(Encoding.UTF8.GetBytes(_packs.ExportCsv(config, ids)), "text/csv", $"cabletv-lineup-{stamp}.csv");
        }

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_packs.ExportPack(config, ids), PackService.Json);
        return File(json, "application/json", $"cabletv-channels-{stamp}.json");
    }

    /// <summary>
    /// The web TV mode: a full-screen cable TV page for browsers. It signs in with the browser's Jellyfin session.
    /// </summary>
    /// <returns>The page.</returns>
    [HttpGet("Web")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult GetWebTv()
    {
        // Revalidate every time, so a plugin update reaches browsers straight away.
        Response.Headers.CacheControl = "no-cache";
        return Resource("tv.html", "text/html; charset=utf-8");
    }

    /// <summary>
    /// hls.js for the web TV mode (Apache-2.0).
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("hls.js")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult GetHlsJs()
    {
        Response.Headers.CacheControl = "public, max-age=604800";
        return Resource("hls.light.min.js", "text/javascript; charset=utf-8");
    }

    /// <summary>
    /// The channel's continuous MPEG-TS stream. Read by the server's own ffmpeg when a Live TV client tunes in; it
    /// authenticates with the plugin's stream key rather than a user token.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <param name="key">Stream key.</param>
    /// <returns>A task that completes when the reader disconnects.</returns>
    [HttpGet("Stream/{channelId}")]
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task GetStream([FromRoute] string channelId, [FromQuery] string? key)
    {
        var expected = Plugin.Instance?.Configuration.StreamKey;
        if (string.IsNullOrEmpty(expected)
            || key is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(expected)))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (_store.Get(channelId) is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var aborted = HttpContext.RequestAborted;
        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-cache, no-store";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        using var subscription = _streams.Subscribe(channelId);
        try
        {
            await foreach (var chunk in subscription.Reader.ReadAllAsync(aborted).ConfigureAwait(false))
            {
                await Response.Body.WriteAsync(chunk, aborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
        }
    }

    private FileStreamResult Resource(string name, string contentType)
    {
        var stream = typeof(CableTvController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.CableTv.Web." + name)
                     ?? throw new InvalidOperationException("Missing resource " + name);
        return File(stream, contentType);
    }

    private IEnumerable<ChannelSnapshot> SelectChannels(string? channelIds)
    {
        if (string.IsNullOrWhiteSpace(channelIds))
        {
            return _store.Channels;
        }

        return channelIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(_store.Get)
            .OfType<ChannelSnapshot>();
    }

    private string? LogoUrl(ChannelSnapshot channel)
    {
        if (channel.Logo is not { } logo)
        {
            return null;
        }

        // The version changes when the logo does, so clients don't keep showing a cached old one.
        var version = StableHash.ToHex(StableHash.Add(StableHash.Start(), logo.LocalPath ?? logo.RemoteUrl ?? string.Empty), 8);
        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}/CableTv/Logo/{Uri.EscapeDataString(channel.Definition.Id)}?v={version}";
    }
}
