using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.CableTv.Configuration;
using Jellyfin.Plugin.CableTv.Library;
using Jellyfin.Plugin.CableTv.Scheduling;

namespace Jellyfin.Plugin.CableTv.Packs;

/// <summary>
/// How imported and suggested channels start out, and how they get free ids and numbers.
/// </summary>
public static class ChannelDefaults
{
    /// <summary>
    /// The id a channel with this name gets.
    /// </summary>
    /// <param name="name">Channel name.</param>
    /// <returns>The id.</returns>
    public static string IdFor(string name) => "ch-" + TitleNormalizer.Slug(name);

    /// <summary>
    /// A new channel with cable-style defaults: shows in rotation, and breaks on a 30-minute grid when the server has
    /// commercials configured.
    /// </summary>
    /// <param name="name">Channel name.</param>
    /// <param name="number">Channel number.</param>
    /// <param name="commercials">Whether the server has commercials configured.</param>
    /// <param name="sources">Content sources.</param>
    /// <returns>The channel.</returns>
    public static ChannelDefinition Create(string name, string number, bool commercials, params ContentSource[] sources)
    {
        return new ChannelDefinition
        {
            Id = IdFor(name),
            Number = number,
            Name = name,
            Enabled = true,
            Sorting = ChannelSorting.RoundRobin,
            ItemTypes = ["Episode", "Movie"],
            Sources = sources,
            CommercialsEnabled = commercials,
            GridMinutes = commercials ? 30 : 0,
            MidBreak = commercials ? MidBreakMode.Halfway : MidBreakMode.None,
        };
    }

    /// <summary>
    /// Hands out channel numbers that aren't taken yet.
    /// </summary>
    public sealed class NumberAllocator
    {
        private readonly HashSet<string> _used;

        /// <summary>
        /// Initializes a new instance of the <see cref="NumberAllocator"/> class.
        /// </summary>
        /// <param name="channels">Channels whose numbers are taken.</param>
        public NumberAllocator(IEnumerable<ChannelDefinition> channels)
        {
            _used = channels.Select(c => Normalize(c.Number)).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a number is taken.
        /// </summary>
        /// <param name="number">Number.</param>
        /// <returns>True when taken.</returns>
        public bool IsTaken(string number) => _used.Contains(Normalize(number));

        /// <summary>
        /// Takes the wanted number when free, else the first free number from <paramref name="from"/>.
        /// </summary>
        /// <param name="wanted">Wanted number, or null.</param>
        /// <param name="from">First number to try otherwise.</param>
        /// <returns>The number.</returns>
        public string Take(string? wanted, int from)
        {
            if (!string.IsNullOrWhiteSpace(wanted) && _used.Add(Normalize(wanted)))
            {
                return wanted.Trim();
            }

            for (var n = Math.Max(from, 1); ; n++)
            {
                var candidate = n.ToString(CultureInfo.InvariantCulture);
                if (_used.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        /// <summary>
        /// Marks a number as taken.
        /// </summary>
        /// <param name="number">Number.</param>
        public void Reserve(string number) => _used.Add(Normalize(number));

        // "007" and "7" are the same channel number.
        private static string Normalize(string? number)
        {
            var n = (number ?? string.Empty).Trim();
            return int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value.ToString(CultureInfo.InvariantCulture)
                : n;
        }
    }
}
