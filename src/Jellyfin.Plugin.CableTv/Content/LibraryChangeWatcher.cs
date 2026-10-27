using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// Rebuilds the channels some minutes after the library stops changing, so new episodes and movies join their channels
/// (and removed ones leave) without waiting for the nightly rebuild. A scan changes thousands of items one by one, so
/// changes are collected and answered with one rebuild once things go quiet.
/// </summary>
public sealed class LibraryChangeWatcher : IHostedService, IDisposable
{
    /// <summary>How long changes must stop before the channels are rebuilt.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    private static readonly BaseItemKind[] Relevant =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Season,
        BaseItemKind.Episode,
        BaseItemKind.MusicVideo,
        BaseItemKind.Video,
        BaseItemKind.Audio,
        BaseItemKind.MusicAlbum,
        BaseItemKind.BoxSet,
        BaseItemKind.Playlist,
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly GuideRefresher _refresher;
    private readonly ChangeSignal _signal;
    private readonly ILogger<LibraryChangeWatcher> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ChangeDebounce _debounce = new(Quiet, MinGap, MaxWait);

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeWatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="refresher">Guide refresher.</param>
    /// <param name="signal">Changes noticed elsewhere (a file that went missing).</param>
    /// <param name="logger">Logger.</param>
    public LibraryChangeWatcher(ILibraryManager libraryManager, GuideRefresher refresher, ChangeSignal signal, ILogger<LibraryChangeWatcher> logger)
    {
        _libraryManager = libraryManager;
        _refresher = refresher;
        _signal = signal;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _ = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    /// <summary>
    /// Whether a change to this kind of item can change a channel.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True when it can.</returns>
    internal static bool IsRelevant(BaseItem item)
        => !item.IsVirtualItem && Array.IndexOf(Relevant, item.GetBaseItemKind()) >= 0;

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is { } item && IsRelevant(item))
        {
            _debounce.Record(DateTime.UtcNow);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Poll);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = DateTime.UtcNow;
                if (_signal.Take())
                {
                    _debounce.Record(now);
                }

                if (Plugin.Instance?.Configuration.AutoRebuildOnLibraryChange == false)
                {
                    // Off: forget the changes, so turning it on doesn't rebuild for old ones.
                    _debounce.Rebuilt(now);
                    continue;
                }

                if (_debounce.TakeDue(now))
                {
                    _logger.LogInformation("The library changed; rebuilding Cable TV channels");
                    _refresher.RequestRebuild();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watching the library for changes stopped");
        }
    }
}
