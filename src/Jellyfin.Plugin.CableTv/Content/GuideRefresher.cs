using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// Rebuilds the channels and asks Jellyfin to refresh its Live TV guide.
/// </summary>
public class GuideRefresher
{
    /// <summary>
    /// Key of Jellyfin's built-in "Refresh Guide" task. It refreshes channels before programmes, which avoids an empty guide.
    /// </summary>
    private const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly Lock _gate = new();
    private readonly ChannelStore _store;
    private bool _running;
    private bool _pending;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<GuideRefresher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuideRefresher"/> class.
    /// </summary>
    /// <param name="store">Channel store.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="logger">Logger.</param>
    public GuideRefresher(ChannelStore store, ITaskManager taskManager, ILogger<GuideRefresher> logger)
    {
        _store = store;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>
    /// Rebuilds in the background and returns at once. Requests that arrive while a rebuild runs are folded into one
    /// more rebuild afterwards, so saving settings several times in a row costs at most two rebuilds and never blocks.
    /// </summary>
    public void RequestRebuild()
    {
        lock (_gate)
        {
            if (_running)
            {
                _pending = true;
                return;
            }

            _running = true;
        }

        _ = Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    RebuildAndRefreshGuide();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cable TV rebuild failed");
                }

                lock (_gate)
                {
                    if (!_pending)
                    {
                        _running = false;
                        return;
                    }

                    _pending = false;
                }
            }
        });
    }

    /// <summary>
    /// Rebuilds all channels, then queues a guide refresh.
    /// </summary>
    public void RebuildAndRefreshGuide()
    {
        _store.Rebuild();

        var task = _taskManager.ScheduledTasks.FirstOrDefault(
            t => string.Equals(t.ScheduledTask.Key, RefreshGuideTaskKey, StringComparison.Ordinal));
        if (task is null)
        {
            _logger.LogWarning("Jellyfin's Refresh Guide task was not found; the Live TV guide will update on its own schedule");
            return;
        }

        _taskManager.QueueScheduledTask(task.ScheduledTask, new TaskOptions());
    }
}
