using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Content;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.CableTv.Tasks;

/// <summary>
/// Re-resolves channel pools so new library items join their channels.
/// </summary>
public class RebuildChannelsTask : IScheduledTask
{
    private readonly GuideRefresher _refresher;

    /// <summary>
    /// Initializes a new instance of the <see cref="RebuildChannelsTask"/> class.
    /// </summary>
    /// <param name="refresher">Guide refresher.</param>
    public RebuildChannelsTask(GuideRefresher refresher)
    {
        _refresher = refresher;
    }

    /// <inheritdoc />
    public string Name => "Rebuild Cable TV channels";

    /// <inheritdoc />
    public string Key => "CableTvRebuildChannels";

    /// <inheritdoc />
    public string Description => "Re-reads each channel's content sources from the library, then refreshes the Live TV guide. Rebuilding a channel whose content changed reshuffles its schedule.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _refresher.RebuildAndRefreshGuide();
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
        };
    }
}
