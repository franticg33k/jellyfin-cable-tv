using System;
using System.Linq;
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

    private readonly ChannelStore _store;
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
