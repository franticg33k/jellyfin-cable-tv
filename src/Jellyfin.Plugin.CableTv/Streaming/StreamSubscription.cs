using System;
using System.Threading.Channels;

namespace Jellyfin.Plugin.CableTv.Streaming;

/// <summary>
/// One viewer of a channel stream.
/// </summary>
public sealed class StreamSubscription : IDisposable
{
    private readonly StreamManager _manager;
    private readonly string _channelId;
    private readonly ChannelBroadcaster _broadcaster;
    private bool _disposed;

    internal StreamSubscription(StreamManager manager, string channelId, ChannelBroadcaster broadcaster, ChannelReader<byte[]> reader)
    {
        _manager = manager;
        _channelId = channelId;
        _broadcaster = broadcaster;
        Reader = reader;
    }

    /// <summary>
    /// Gets the MPEG-TS chunks, each a whole number of packets.
    /// </summary>
    public ChannelReader<byte[]> Reader { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.Release(_channelId, _broadcaster, Reader);
    }
}
