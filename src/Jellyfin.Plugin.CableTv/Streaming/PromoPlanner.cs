using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.CableTv.Scheduling;

namespace Jellyfin.Plugin.CableTv.Streaming;

/// <summary>
/// What a promo card announces.
/// </summary>
public enum PromoKind
{
    /// <summary>The programme that follows the current one.</summary>
    UpNext = 0,

    /// <summary>A movie airing later today or tomorrow.</summary>
    Tonight = 1,
}

/// <summary>
/// The content of a promo card.
/// </summary>
/// <param name="Kind">What it announces.</param>
/// <param name="Heading">The line above the title, such as "COMING UP NEXT" or "TONIGHT AT 8:00 PM".</param>
/// <param name="Title">The programme's title.</param>
/// <param name="Subtitle">The episode title, when it is an episode.</param>
/// <param name="Details">Year, rating and length, such as "1999 · R · 2 h 16 min".</param>
/// <param name="TimeText">When it airs, such as "8:30 PM".</param>
public sealed record PromoInfo(PromoKind Kind, string Heading, string Title, string? Subtitle, string? Details, string TimeText)
{
    /// <summary>Gets a short string that changes whenever the card does, for naming the cached image.</summary>
    public string Key => string.Join('|', Kind, Heading, Title, Subtitle, Details, TimeText);
}

/// <summary>
/// Chooses what a break or gap between programmes announces: the next programme, or now and then a movie later on.
/// </summary>
public static class PromoPlanner
{
    private static readonly TimeSpan LookAhead = TimeSpan.FromHours(10);

    /// <summary>
    /// Picks the card for a filler slot.
    /// </summary>
    /// <param name="timeline">The channel's timeline.</param>
    /// <param name="filler">The filler slot (a break or a gap).</param>
    /// <param name="zone">The time zone times are told in.</param>
    /// <returns>The card, or null when nothing is coming up that is worth announcing.</returns>
    public static PromoInfo? Pick(IChannelTimeline timeline, ScheduledSlot filler, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(filler);
        ArgumentNullException.ThrowIfNull(zone);

        var blocks = timeline.GetBlocks(filler.StartUtc.AddSeconds(-1), filler.EndUtc + LookAhead)
            .Where(b => b.Item is not null)
            .OrderBy(b => b.StartUtc)
            .ToList();

        // A break inside a programme announces what comes after that programme, not the programme resuming.
        var current = blocks.FirstOrDefault(b => b.Slots.Any(s => s.SlotId == filler.SlotId));
        var after = current is null ? filler.StartUtc : current.EndUtc;
        var upcoming = blocks.Where(b => b.StartUtc >= after.AddSeconds(-1) && b != current).ToList();
        var next = upcoming.FirstOrDefault();
        if (next?.Item is null)
        {
            return null;
        }

        // Every third break promotes a movie later on, when there is one, instead of what is next.
        if (Variant(filler.SlotId) == 0)
        {
            var movie = upcoming.Skip(1).FirstOrDefault(b => b.Item is { IsMovie: true } && b.StartUtc >= next.StartUtc.AddMinutes(30));
            if (movie is not null)
            {
                return Describe(movie, PromoKind.Tonight, filler.StartUtc, zone);
            }
        }

        return Describe(next, PromoKind.UpNext, filler.StartUtc, zone);
    }

    private static int Variant(string slotId)
    {
        var hash = 17;
        foreach (var c in slotId)
        {
            hash = unchecked((hash * 31) + c);
        }

        return (int)((uint)hash % 3);
    }

    private static PromoInfo Describe(ScheduledBlock block, PromoKind kind, DateTime nowUtc, TimeZoneInfo zone)
    {
        var item = block.Item!;
        var start = TimeZoneInfo.ConvertTimeFromUtc(block.StartUtc, zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        var time = start.ToString("h:mm tt", CultureInfo.InvariantCulture);

        string heading;
        if (kind == PromoKind.UpNext)
        {
            heading = "COMING UP NEXT";
        }
        else if (start.Date == today)
        {
            heading = (start.Hour >= 17 ? "TONIGHT AT " : "LATER TODAY AT ") + time;
        }
        else if (start.Date == today.AddDays(1))
        {
            heading = "TOMORROW AT " + time;
        }
        else
        {
            heading = start.ToString("dddd", CultureInfo.InvariantCulture).ToUpperInvariant() + " AT " + time;
        }

        var details = new List<string>();
        if (item.IsMovie && item.ProductionYear is int year)
        {
            details.Add(year.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(item.OfficialRating))
        {
            details.Add(item.OfficialRating!);
        }

        var minutes = (int)Math.Round((block.EndUtc - block.StartUtc).TotalMinutes);
        if (item.IsMovie && minutes > 0)
        {
            details.Add(minutes >= 60 ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60:00} min") : string.Create(CultureInfo.InvariantCulture, $"{minutes} min"));
        }

        var subtitle = item.IsMovie || string.IsNullOrWhiteSpace(item.EpisodeTitle) ? null : item.EpisodeTitle;
        return new PromoInfo(kind, heading, item.Title, subtitle, details.Count > 0 ? string.Join("  ·  ", details) : null, time);
    }
}
