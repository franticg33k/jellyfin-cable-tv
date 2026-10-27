using System.Threading;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// A flag raised when something other than a library event says the channels are out of date, such as a scheduled file
/// that has gone missing. The library watcher turns it into a rebuild.
/// </summary>
public sealed class ChangeSignal
{
    private int _raised;

    /// <summary>Says the channels are out of date.</summary>
    public void Raise() => Volatile.Write(ref _raised, 1);

    /// <summary>
    /// Reads and clears the flag.
    /// </summary>
    /// <returns>True when it was raised.</returns>
    public bool Take() => Interlocked.Exchange(ref _raised, 0) == 1;
}
