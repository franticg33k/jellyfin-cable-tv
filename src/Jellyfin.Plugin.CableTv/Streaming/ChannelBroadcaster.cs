using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Content;
using Jellyfin.Plugin.CableTv.Scheduling;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Streaming;

/// <summary>
/// Produces one channel's continuous MPEG-TS stream and fans it out to every viewer, so a channel costs one ffmpeg
/// however many people watch it.
/// </summary>
internal sealed class ChannelBroadcaster : IAsyncDisposable
{
    /// <summary>MPEG-TS packet size; chunks are cut on packet boundaries so late joiners get whole packets.</summary>
    private const int PacketSize = 188;

    /// <summary>How far the stream may fall behind the schedule before it skips ahead to rejoin it.</summary>
    private static readonly TimeSpan MaxLag = TimeSpan.FromSeconds(3);

    /// <summary>Slots shorter than this are skipped instead of starting an ffmpeg run for them.</summary>
    private static readonly TimeSpan MinRun = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How much recent output a new viewer receives at once. It holds at least one keyframe for typical content, so
    /// the viewer's player can start straight away instead of waiting for the next one.
    /// </summary>
    private static readonly TimeSpan Replay = TimeSpan.FromSeconds(12);

    /// <summary>Seconds of the first item sent faster than real time, so the first viewer's player fills its buffer.</summary>
    private const double InitialBurstSeconds = 10;

    /// <summary>
    /// Timestamp gap left between consecutive items. A run's audio can end a few milliseconds after its video; the gap
    /// keeps the next run's timestamps from overlapping it.
    /// </summary>
    private static readonly TimeSpan RunGap = TimeSpan.FromMilliseconds(100);

    private readonly string _channelId;
    private readonly ChannelStore _store;
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly List<Channel<byte[]>> _subscribers = [];
    private readonly Queue<(DateTime At, byte[] Chunk)> _recent = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _producer;

    public ChannelBroadcaster(string channelId, ChannelStore store, string ffmpegPath, ILogger logger)
    {
        _channelId = channelId;
        _store = store;
        _ffmpegPath = ffmpegPath;
        _logger = logger;
    }

    public int SubscriberCount
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }

    public bool IsStopped => _stop.IsCancellationRequested;

    /// <summary>
    /// Adds a viewer. The producer starts with the first one.
    /// </summary>
    public ChannelReader<byte[]> Subscribe()
    {
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096) { SingleReader = true, SingleWriter = true });
        lock (_lock)
        {
            foreach (var (_, chunk) in _recent)
            {
                queue.Writer.TryWrite(chunk);
            }

            _subscribers.Add(queue);
            _producer ??= Task.Run(() => ProduceAsync(_stop.Token));
        }

        return queue.Reader;
    }

    public void Unsubscribe(ChannelReader<byte[]> reader)
    {
        lock (_lock)
        {
            var index = _subscribers.FindIndex(s => s.Reader == reader);
            if (index >= 0)
            {
                _subscribers[index].Writer.TryComplete();
                _subscribers.RemoveAt(index);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_producer is not null)
        {
            try
            {
                await _producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_lock)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }

        _stop.Dispose();
    }

    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        var streamStart = DateTime.UtcNow;
        var position = streamStart;
        var first = true;
        var runs = 0;

        _logger.LogInformation("Starting stream for channel {Channel}", _channelId);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var channel = _store.Get(_channelId);
                if (channel is null)
                {
                    _logger.LogWarning("Channel {Channel} no longer exists; ending its stream", _channelId);
                    break;
                }

                var now = DateTime.UtcNow;
                if (now - position > MaxLag)
                {
                    position = now;
                }

                var slot = channel.Timeline.GetSlotAt(position);
                if (slot is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    position = DateTime.UtcNow;
                    continue;
                }

                var remaining = slot.EndUtc - position;
                if (remaining < MinRun)
                {
                    position = slot.EndUtc;
                    continue;
                }

                var args = FfmpegArguments.Build(
                    slot,
                    TimeSpan.FromTicks(slot.InPointTicks) + (position - slot.StartUtc),
                    remaining,
                    position - streamStart + (RunGap * runs++),
                    channel.Stream,
                    first ? InitialBurstSeconds : 0);
                first = false;

                if (!await RunAsync(args, cancellationToken).ConfigureAwait(false))
                {
                    // Cover the failed item with a short stretch of filler rather than spinning on it.
                    var filler = slot with { Item = null };
                    var gap = TimeSpan.FromSeconds(Math.Min(10, remaining.TotalSeconds));
                    var fillerArgs = FfmpegArguments.Build(filler, TimeSpan.Zero, gap, DateTime.UtcNow - streamStart + (RunGap * runs++), channel.Stream);
                    await RunAsync(fillerArgs, cancellationToken).ConfigureAwait(false);
                    position = DateTime.UtcNow;
                    continue;
                }

                position = slot.EndUtc;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stream for channel {Channel} failed", _channelId);
        }
        finally
        {
            _logger.LogInformation("Stopped stream for channel {Channel}", _channelId);
            await _stop.CancelAsync().ConfigureAwait(false);
            lock (_lock)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryComplete();
                }
            }
        }
    }

    /// <summary>
    /// Runs one ffmpeg process and fans its output out. Returns false when ffmpeg fails without producing output.
    /// </summary>
    private async Task<bool> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        _logger.LogDebug("ffmpeg {Args}", string.Join(' ', args));
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        long written = 0;
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var buffer = new byte[PacketSize * 512];
            var filled = 0;
            int read;
            while ((read = await stdout.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false)) > 0)
            {
                filled += read;
                var whole = filled - (filled % PacketSize);
                if (whole == 0)
                {
                    continue;
                }

                Broadcast(buffer.AsSpan(0, whole).ToArray());
                written += whole;
                Buffer.BlockCopy(buffer, whole, buffer, 0, filled - whole);
                filled -= whole;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        if (process.ExitCode != 0)
        {
            var error = await stderr.ConfigureAwait(false);
            _logger.LogWarning("ffmpeg exited with {Code} on channel {Channel}: {Error}", process.ExitCode, _channelId, error.Trim());
            return written > 0;
        }

        return true;
    }

    private void Broadcast(byte[] chunk)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _recent.Enqueue((now, chunk));
            while (_recent.Count > 0 && now - _recent.Peek().At > Replay)
            {
                _recent.Dequeue();
            }

            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].Writer.TryWrite(chunk))
                {
                    // A viewer this far behind has stalled; drop it rather than buffer without bound.
                    _logger.LogWarning("Dropping a stalled viewer of channel {Channel}", _channelId);
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }
}
