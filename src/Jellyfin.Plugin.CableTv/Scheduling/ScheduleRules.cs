using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// A daily time window in local time. <see cref="End"/> at or before <see cref="Start"/> runs past midnight.
/// </summary>
/// <param name="Start">Local start time.</param>
/// <param name="End">Local end time.</param>
public sealed record TimeWindow(TimeOnly Start, TimeOnly End)
{
    /// <summary>Gets a value indicating whether the window runs past midnight.</summary>
    public bool CrossesMidnight => End <= Start;

    /// <summary>
    /// Parses "HH:mm-HH:mm".
    /// </summary>
    /// <param name="value">Text to parse.</param>
    /// <param name="window">The window.</param>
    /// <returns>True when it parsed.</returns>
    public static bool TryParse(string? value, out TimeWindow window)
    {
        window = new TimeWindow(TimeOnly.MinValue, TimeOnly.MinValue);
        var parts = value?.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 } || !TryParseTime(parts[0], out var start) || !TryParseTime(parts[1], out var end))
        {
            return false;
        }

        window = new TimeWindow(start, end);
        return true;
    }

    /// <summary>
    /// Parses "HH:mm"; "24:00" means midnight.
    /// </summary>
    /// <param name="value">Text to parse.</param>
    /// <param name="time">The time.</param>
    /// <returns>True when it parsed.</returns>
    public static bool TryParseTime(string? value, out TimeOnly time)
    {
        if (string.Equals(value?.Trim(), "24:00", StringComparison.Ordinal))
        {
            time = TimeOnly.MinValue;
            return true;
        }

        return TimeOnly.TryParseExact(value?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <inheritdoc />
    public override string ToString() => FormattableString.Invariant($"{Start:HH\\:mm}-{End:HH\\:mm}");
}

/// <summary>
/// A time slot: during its window, on its days, the channel airs its own pool.
/// </summary>
/// <param name="Name">Name shown for the lineup.</param>
/// <param name="Window">Daily window.</param>
/// <param name="Days">Days the slot starts on; empty for every day.</param>
/// <param name="Pool">Programme pool, canonical order.</param>
/// <param name="Sorting">Pool ordering.</param>
public sealed record TimeSlotRule(string Name, TimeWindow Window, IReadOnlyList<DayOfWeek> Days, IReadOnlyList<PoolItem> Pool, ChannelSorting Sorting)
{
    /// <summary>Whether the slot starts on <paramref name="day"/>.</summary>
    /// <param name="day">Local date.</param>
    /// <returns>True when it applies.</returns>
    public bool AppliesOn(DateOnly day) => Days.Count == 0 || Days.Contains(day.DayOfWeek);
}

/// <summary>
/// A seasonal lineup: between two calendar dates (inclusive, may wrap the new year) it replaces the channel's main pool.
/// </summary>
/// <param name="Name">Name shown for the lineup.</param>
/// <param name="From">First day, month and day.</param>
/// <param name="To">Last day, month and day.</param>
/// <param name="Pool">Pool used on those days (already merged with the main pool for mixed lineups).</param>
public sealed record SeasonalRule(string Name, (int Month, int Day) From, (int Month, int Day) To, IReadOnlyList<PoolItem> Pool)
{
    /// <summary>Whether the lineup covers <paramref name="day"/>.</summary>
    /// <param name="day">Local date.</param>
    /// <returns>True when covered.</returns>
    public bool Covers(DateOnly day)
    {
        var key = (day.Month * 100) + day.Day;
        var from = (From.Month * 100) + From.Day;
        var to = (To.Month * 100) + To.Day;
        return from <= to ? key >= from && key <= to : key >= from || key <= to;
    }

    /// <summary>
    /// Parses "MM-dd".
    /// </summary>
    /// <param name="value">Text to parse.</param>
    /// <param name="monthDay">Month and day.</param>
    /// <returns>True when it parsed.</returns>
    public static bool TryParseMonthDay(string? value, out (int Month, int Day) monthDay)
    {
        monthDay = default;
        var parts = value?.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)
            || month is < 1 or > 12
            || day < 1
            || day > DateTime.DaysInMonth(2024, month))
        {
            return false;
        }

        monthDay = (month, day);
        return true;
    }
}

/// <summary>
/// Restricted hours: the listed items may only air inside the window.
/// </summary>
/// <param name="ItemIds">Items restricted to the window.</param>
/// <param name="Window">Daily window they may air in.</param>
public sealed record RestrictionRule(IReadOnlySet<Guid> ItemIds, TimeWindow Window);

/// <summary>
/// Premieres: on its days at <see cref="At"/>, the channel airs an item added in the previous <see cref="WithinDays"/> days.
/// </summary>
/// <param name="At">Local time.</param>
/// <param name="Days">Days it runs on; empty for every day.</param>
/// <param name="WithinDays">How recently an item must have been added to premiere.</param>
public sealed record PremiereRule(TimeOnly At, IReadOnlyList<DayOfWeek> Days, int WithinDays);

/// <summary>
/// Time-of-day and calendar rules for a channel.
/// </summary>
public sealed record ScheduleRules
{
    /// <summary>Gets the time zone rules are written in.</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>Gets the time slots, earlier ones winning where they overlap.</summary>
    public IReadOnlyList<TimeSlotRule> Slots { get; init; } = [];

    /// <summary>Gets the seasonal lineups, earlier ones winning where they overlap.</summary>
    public IReadOnlyList<SeasonalRule> Seasons { get; init; } = [];

    /// <summary>Gets the restricted-hours rules.</summary>
    public IReadOnlyList<RestrictionRule> Restrictions { get; init; } = [];

    /// <summary>Gets the premiere rule, if any.</summary>
    public PremiereRule? Premiere { get; init; }

    /// <summary>Gets a value indicating whether any rule is set.</summary>
    public bool HasRules => Slots.Count > 0 || Seasons.Count > 0 || Restrictions.Count > 0 || Premiere is not null;

    /// <summary>
    /// Parses day names ("Mon", "monday", "Sat", …), ignoring ones it doesn't recognise.
    /// </summary>
    /// <param name="names">Day names.</param>
    /// <returns>The days.</returns>
    public static IReadOnlyList<DayOfWeek> ParseDays(IEnumerable<string>? names)
        => (names ?? [])
            .Select(n => n.Trim())
            .Select(n => Enum.GetValues<DayOfWeek>().FirstOrDefault(
                d => n.Length >= 2 && d.ToString().StartsWith(n, StringComparison.OrdinalIgnoreCase), (DayOfWeek)(-1)))
            .Where(d => d >= DayOfWeek.Sunday)
            .Distinct()
            .ToList();
}
