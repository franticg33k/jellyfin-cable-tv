using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Content;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Streaming;

/// <summary>
/// Owns the per-channel broadcasters: starts one with its first viewer and stops it a little after its last.
/// </summary>
public sealed class StreamManager : IAsyncDisposable
{
    /// <summary>
    /// How long a channel keeps streaming with nobody watching, so a quick re-tune or Jellyfin reopening the stream
    /// reuses it instead of starting over.
    /// </summary>
    private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, ChannelBroadcaster> _broadcasters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChannelStore _store;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<StreamManager> _logger;
    private readonly Lock _lock = new();
    private readonly Weather.WeatherService _weather;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamManager"/> class.
    /// </summary>
    /// <param name="store">Channel store.</param>
    /// <param name="mediaEncoder">Media encoder, for the server's ffmpeg path.</param>
    /// <param name="weather">Weather service, for weather channels' forecast cards.</param>
    /// <param name="logger">Logger.</param>
    public StreamManager(ChannelStore store, IMediaEncoder mediaEncoder, Weather.WeatherService weather, ILogger<StreamManager> logger)
    {
        _store = store;
        _weather = weather;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Starts watching a channel.
    /// </summary>
    /// <param name="channelId">Channel id.</param>
    /// <returns>A subscription; dispose it when the viewer leaves.</returns>
    public StreamSubscription Subscribe(string channelId)
    {
        lock (_lock)
        {
            if (!_broadcasters.TryGetValue(channelId, out var broadcaster) || broadcaster.IsStopped)
            {
                broadcaster = new ChannelBroadcaster(channelId, _store, _mediaEncoder.EncoderPath, (c, ct) => _weather.CardAsync(c.Definition, ct), _logger);
                _broadcasters[channelId] = broadcaster;
            }

            return new StreamSubscription(this, channelId, broadcaster, broadcaster.Subscribe());
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var broadcaster in _broadcasters.Values)
        {
            await broadcaster.DisposeAsync().ConfigureAwait(false);
        }

        _broadcasters.Clear();
    }

    internal void Release(string channelId, ChannelBroadcaster broadcaster, ChannelReader<byte[]> reader)
    {
        broadcaster.Unsubscribe(reader);
        _ = StopIfIdleAsync(channelId, broadcaster);
    }

    private async Task StopIfIdleAsync(string channelId, ChannelBroadcaster broadcaster)
    {
        await Task.Delay(IdleGrace).ConfigureAwait(false);
        lock (_lock)
        {
            if (broadcaster.SubscriberCount > 0
                || !_broadcasters.TryGetValue(channelId, out var current)
                || !ReferenceEquals(current, broadcaster))
            {
                return;
            }

            _broadcasters.TryRemove(channelId, out _);
        }

        await broadcaster.DisposeAsync().ConfigureAwait(false);
    }
}
