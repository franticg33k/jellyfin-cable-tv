using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Scheduling;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.LiveTv;

/// <summary>
/// Publishes the plugin's channels and guide through Jellyfin Live TV, so every client with Live TV support sees them.
/// </summary>
/// <remarks>
/// Phase 1 stream: tuning in plays the file airing now, from its start, and ends with it. The continuous
/// copy-video / re-encode-audio stream that joins mid-show replaces this in phase 3. Clients that want instant,
/// transcode-free tune-in use the plugin API directly instead.
/// </remarks>
public class CableTvLiveTvService : ILiveTvService
{
    private readonly ChannelStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CableTvLiveTvService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CableTvLiveTvService"/> class.
    /// </summary>
    /// <param name="store">Channel store.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public CableTvLiveTvService(ChannelStore store, ILibraryManager libraryManager, ILogger<CableTvLiveTvService> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>Jellyfin keys channels and programmes by this name, so it is fixed rather than configurable.</remarks>
    public string Name => "Cable TV";

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/arun-iv/jellyfin-cable-tv";

    /// <inheritdoc />
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var channels = _store.Channels.Select(c => new ChannelInfo
        {
            Id = c.Definition.Id,
            Number = c.Definition.Number,
            Name = c.Definition.Name,
            ChannelType = ChannelType.TV,
            ImageUrl = string.IsNullOrWhiteSpace(c.Definition.LogoUrl) ? null : c.Definition.LogoUrl,
            HasImage = !string.IsNullOrWhiteSpace(c.Definition.LogoUrl),
        });

        return Task.FromResult(channels);
    }

    /// <inheritdoc />
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        var channel = _store.Get(channelId);
        if (channel is null)
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        var programs = channel.Timeline
            .GetSlots(startDateUtc.ToUniversalTime(), endDateUtc.ToUniversalTime())
            .Where(s => s.Kind == SlotKind.Program)
            .Select(s => ToProgram(channelId, s))
            .ToList();

        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        var (slot, source) = GetAiring(channelId);
        _logger.LogInformation(
            "Tuning {Channel} to {Title} (slot {Slot}, {Offset:F0}s in)",
            channelId,
            slot.Item.Title,
            slot.SlotId,
            (DateTime.UtcNow - slot.StartUtc).TotalSeconds);

        return Task.FromResult(ToLiveSource(channelId + "_" + slot.SlotId, source));
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var (_, source) = GetAiring(channelId);
        return Task.FromResult(new List<MediaSourceInfo> { ToLiveSource(channelId, source) });
    }

    /// <inheritdoc />
    public Task CloseLiveStream(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ResetTuner(string id, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<TimerInfo>());

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo? program = null)
        => Task.FromResult(new SeriesTimerInfo());

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken) => throw RecordingNotSupported();

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => throw RecordingNotSupported();

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken) => throw RecordingNotSupported();

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken) => throw RecordingNotSupported();

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken) => Task.CompletedTask;

    private static NotSupportedException RecordingNotSupported()
        => new("Cable TV channels are built from your library; there is nothing to record.");

    private static ProgramInfo ToProgram(string channelId, ScheduledSlot slot)
    {
        var item = slot.Item;
        return new ProgramInfo
        {
            Id = channelId + "_" + slot.SlotId,
            ChannelId = channelId,
            Name = item.Title,
            EpisodeTitle = item.EpisodeTitle,
            SeasonNumber = item.SeasonNumber,
            EpisodeNumber = item.EpisodeNumber,
            Overview = item.Overview,
            Genres = item.Genres.ToList(),
            OfficialRating = item.OfficialRating,
            ProductionYear = item.ProductionYear,
            StartDate = slot.StartUtc,
            EndDate = slot.EndUtc,
            IsMovie = item.IsMovie,
            IsSeries = item.SeriesId.HasValue,
            IsRepeat = true,
            SeriesId = item.SeriesId?.ToString("N", CultureInfo.InvariantCulture),
            ShowId = item.SeriesId?.ToString("N", CultureInfo.InvariantCulture),
            ImagePath = item.ImagePath,
            HasImage = !string.IsNullOrEmpty(item.ImagePath),
        };
    }

    private static MediaSourceInfo ToLiveSource(string id, MediaSourceInfo source) => new()
    {
        Id = id,
        Path = source.Path,
        Protocol = source.Protocol,
        Container = source.Container,
        Bitrate = source.Bitrate,
        RunTimeTicks = source.RunTimeTicks,
        MediaStreams = source.MediaStreams,
        IsRemote = source.IsRemote,
        IsInfiniteStream = true,
        SupportsProbing = false,
        SupportsDirectPlay = false,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        RequiresOpening = true,
        RequiresClosing = true,
    };

    private (ScheduledSlot Slot, MediaSourceInfo Source) GetAiring(string channelId)
    {
        var channel = _store.Get(channelId) ?? throw new ResourceNotFoundException($"Unknown Cable TV channel {channelId}.");
        var slot = channel.Timeline.GetSlotAt(DateTime.UtcNow)
                   ?? throw new ResourceNotFoundException($"Cable TV channel {channelId} has no content.");

        if (_libraryManager.GetItemById(slot.Item.ItemId) is not IHasMediaSources item)
        {
            throw new ResourceNotFoundException($"Item {slot.Item.ItemId} airing on {channelId} is no longer in the library.");
        }

        var source = item.GetMediaSources(false).FirstOrDefault(s => s.Id == slot.Item.MediaSourceId)
                     ?? item.GetMediaSources(false).FirstOrDefault()
                     ?? throw new ResourceNotFoundException($"Item {slot.Item.ItemId} has no media source.");

        return (slot, source);
    }
}
