using System.Globalization;

namespace Jellyfin.Plugin.CableTv.Content;

/// <summary>
/// Channel number helpers.
/// </summary>
internal static class ChannelNumber
{
    /// <summary>
    /// Sort key that orders "2" before "10" and "4.1" after "4".
    /// </summary>
    /// <param name="number">Channel number.</param>
    /// <returns>Numeric key; non-numeric numbers sort last.</returns>
    public static double SortKey(string? number)
        => double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.MaxValue;
}
