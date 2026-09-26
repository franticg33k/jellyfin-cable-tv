using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Scheduling;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.LiveTv;

/// <summary>
/// Publishes the plugin's channels and guide through Jellyfin Live TV, so every client with Live TV support sees them.
/// </summary>
/// <remarks>
/// Tuning in opens the channel's continuous stream (<see cref="Streaming.StreamManager"/>), which joins the airing
/// programme at the right point and runs on through breaks and following programmes. Each programme block, breaks
/// included, is one guide entry. Clients that want instant, transcode-free tune-in use the plugin API instead.
/// </remarks>
public class CableTvLiveTvService : ILiveTvService
{
    private readonly ChannelStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<CableTvLiveTvService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CableTvLiveTvService"/> class.
    /// </summary>
    /// <param name="store">Channel store.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="appHost">Server application host.</param>
    /// <param name="logger">Logger.</param>
    public CableTvLiveTvService(ChannelStore store, ILibraryManager libraryManager, IServerApplicationHost appHost, ILogger<CableTvLiveTvService> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _appHost = appHost;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>Jellyfin keys channels and programmes by this name, so it is fixed rather than configurable.</remarks>
    public string Name => "Cable TV";

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/franticg33k/jellyfin-cable-tv";

    /// <inheritdoc />
    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var channels = _store.Channels.Select(c => new ChannelInfo
        {
            Id = c.Definition.Id,
            Number = c.Definition.Number,
            Name = c.Definition.Name,
            ChannelType = ChannelType.TV,
            ChannelGroup = string.IsNullOrWhiteSpace(c.Definition.Category) ? null : c.Definition.Category.Trim(),
            ImagePath = c.Logo?.LocalPath,
            ImageUrl = c.Logo?.RemoteUrl,
            HasImage = c.Logo is not null,
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
            .GetBlocks(startDateUtc.ToUniversalTime(), endDateUtc.ToUniversalTime())
            .Select(b => ToProgram(channelId, b))
            .ToList();

        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        var channel = _store.Get(channelId) ?? throw new ResourceNotFoundException($"Unknown Cable TV channel {channelId}.");
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.FallbackMode == FallbackStreamMode.Off)
        {
            var (slot, source) = GetAiring(channel);
            _logger.LogInformation("Tuning {Channel} to {Title} from its start (continuous stream is off)", channelId, slot.Item?.Title);
            return Task.FromResult(ToFileSource(channelId + "_" + slot.SlotId, source));
        }

        _logger.LogInformation("Tuning {Channel} to its continuous stream", channelId);
        return Task.FromResult(ToStreamSource(channelId + "_live", channel, config.StreamKey));
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var channel = _store.Get(channelId) ?? throw new ResourceNotFoundException($"Unknown Cable TV channel {channelId}.");
        var config = Plugin.Instance?.Configuration;
        var source = config is null || config.FallbackMode == FallbackStreamMode.Off
            ? ToFileSource(channelId, GetAiring(channel).Source)
            : ToStreamSource(channelId, channel, config.StreamKey);
        return Task.FromResult(new List<MediaSourceInfo> { source });
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

    private static ProgramInfo ToProgram(string channelId, ScheduledBlock block)
    {
        var item = block.Item;
        if (item is null)
        {
            return new ProgramInfo
            {
                Id = channelId + "_" + block.BlockId,
                ChannelId = channelId,
                Name = "Off air",
                StartDate = block.StartUtc,
                EndDate = block.EndUtc,
            };
        }

        return new ProgramInfo
        {
            Id = channelId + "_" + block.BlockId,
            ChannelId = channelId,
            Name = item.Title,
            EpisodeTitle = item.EpisodeTitle,
            SeasonNumber = item.SeasonNumber,
            EpisodeNumber = item.EpisodeNumber,
            Overview = item.Overview,
            Genres = item.Genres.ToList(),
            OfficialRating = item.OfficialRating,
            ProductionYear = item.ProductionYear,
            StartDate = block.StartUtc,
            EndDate = block.EndUtc,
            IsMovie = item.IsMovie,
            IsSeries = item.SeriesId.HasValue,
            IsRepeat = !block.IsPremiere,
            IsPremiere = block.IsPremiere,
            SeriesId = item.SeriesId?.ToString("N", CultureInfo.InvariantCulture),
            ShowId = item.SeriesId?.ToString("N", CultureInfo.InvariantCulture),
            ImagePath = item.ImagePath,
            HasImage = !string.IsNullOrEmpty(item.ImagePath),
        };
    }

    private static MediaSourceInfo ToFileSource(string id, MediaSourceInfo source) => new()
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

    private MediaSourceInfo ToStreamSource(string id, ChannelSnapshot channel, string key)
    {
        var profile = channel.Stream;
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/CableTv/Stream/{1}?key={2}",
            _appHost.GetApiUrlForLocalAccess(IPAddress.Loopback, false).TrimEnd('/'),
            Uri.EscapeDataString(channel.Definition.Id),
            Uri.EscapeDataString(key));

        return new MediaSourceInfo
        {
            Id = id,
            Path = url,
            Protocol = MediaProtocol.Http,
            Container = "mpegts",
            IsRemote = false,
            IsInfiniteStream = true,
            SupportsProbing = false,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            RequiresOpening = true,
            RequiresClosing = true,

            // Without this Jellyfin's ffmpeg analyses 200 s of input before it starts, which on a live source means
            // waiting 200 s. The stream's format is declared below, so a short look is enough.
            AnalyzeDurationMs = 2000,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = profile.VideoCodec,
                    Width = profile.Width,
                    Height = profile.Height,
                    IsInterlaced = false,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    SampleRate = 48000,
                    BitRate = 192000,
                    IsDefault = true,
                },
            ],
        };
    }

    private (ScheduledSlot Slot, MediaSourceInfo Source) GetAiring(ChannelSnapshot channel)
    {
        var channelId = channel.Definition.Id;
        var slot = channel.Timeline.GetBlocks(DateTime.UtcNow, DateTime.UtcNow.AddTicks(1)).FirstOrDefault()?.Slots[0]
                   ?? throw new ResourceNotFoundException($"Cable TV channel {channelId} has no content.");

        if (slot.Item is null || _libraryManager.GetItemById(slot.Item.ItemId) is not IHasMediaSources item)
        {
            throw new ResourceNotFoundException($"The programme airing on {channelId} is no longer in the library.");
        }

        var source = item.GetMediaSources(false).FirstOrDefault(s => s.Id == slot.Item.MediaSourceId)
                     ?? item.GetMediaSources(false).FirstOrDefault()
                     ?? throw new ResourceNotFoundException($"Item {slot.Item.ItemId} has no media source.");

        return (slot, source);
    }
}
