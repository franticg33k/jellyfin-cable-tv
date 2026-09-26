using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CableTv.Streaming;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.CableTv.Tasks;

/// <summary>
/// Converts commercials to each channel's stream format ahead of time.
/// </summary>
public class PrepareCommercialsTask : IScheduledTask
{
    private readonly CommercialCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="PrepareCommercialsTask"/> class.
    /// </summary>
    /// <param name="cache">Commercial cache.</param>
    public PrepareCommercialsTask(CommercialCache cache)
    {
        _cache = cache;
    }

    /// <inheritdoc />
    public string Name => "Prepare Cable TV commercials";

    /// <inheritdoc />
    public string Key => "CableTvPrepareCommercials";

    /// <inheritdoc />
    public string Description => "Converts commercials to each channel's Live TV stream format ahead of time, so the stream copies them instead of transcoding every airing.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => _cache.PrepareAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4.5).Ticks,
        };
    }
}
