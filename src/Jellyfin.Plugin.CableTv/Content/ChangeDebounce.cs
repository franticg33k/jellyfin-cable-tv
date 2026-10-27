using System;
using System.Threading;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// Decides when a burst of library changes (a scan adds hundreds of items one by one) is over and the channels should be
/// rebuilt once. A rebuild is due when changes have stopped for <c>quiet</c>, but never closer than <c>minGap</c> to the
/// previous rebuild, and no later than <c>maxWait</c> after the first change even if they never stop.
/// </summary>
public sealed class ChangeDebounce
{
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _minGap;
    private readonly TimeSpan _maxWait;
    private readonly Lock _gate = new();
    private DateTime? _firstChange;
    private DateTime _lastChange;
    private DateTime _lastRebuild = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeDebounce"/> class.
    /// </summary>
    /// <param name="quiet">How long changes must have stopped.</param>
    /// <param name="minGap">The shortest time between two rebuilds.</param>
    /// <param name="maxWait">The longest a change waits for a rebuild.</param>
    public ChangeDebounce(TimeSpan quiet, TimeSpan minGap, TimeSpan maxWait)
    {
        _quiet = quiet;
        _minGap = minGap;
        _maxWait = maxWait;
    }

    /// <summary>Gets a value indicating whether a change is waiting for a rebuild.</summary>
    public bool Pending
    {
        get
        {
            lock (_gate)
            {
                return _firstChange is not null;
            }
        }
    }

    /// <summary>
    /// Notes that the library changed.
    /// </summary>
    /// <param name="now">The time (UTC).</param>
    public void Record(DateTime now)
    {
        lock (_gate)
        {
            _firstChange ??= now;
            _lastChange = now;
        }
    }

    /// <summary>
    /// Tells whether a rebuild is due now; when it is, the waiting changes count as handled.
    /// </summary>
    /// <param name="now">The time (UTC).</param>
    /// <returns>True when the caller should rebuild.</returns>
    public bool TakeDue(DateTime now)
    {
        lock (_gate)
        {
            if (_firstChange is not { } first || now - _lastRebuild < _minGap)
            {
                return false;
            }

            if (now - _lastChange < _quiet && now - first < _maxWait)
            {
                return false;
            }

            _firstChange = null;
            _lastRebuild = now;
            return true;
        }
    }

    /// <summary>
    /// Notes that a rebuild happened for another reason, so changes noted before it no longer wait.
    /// </summary>
    /// <param name="now">The time (UTC).</param>
    public void Rebuilt(DateTime now)
    {
        lock (_gate)
        {
            _firstChange = null;
            _lastRebuild = now;
        }
    }
}
