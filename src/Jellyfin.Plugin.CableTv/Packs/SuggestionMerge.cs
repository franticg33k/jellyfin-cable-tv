using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.CableTv.Packs;

/// <summary>
/// Keeps a suggestion list to one channel per name.
/// </summary>
public static class SuggestionMerge
{
    /// <summary>
    /// Drops suggestions whose channel name (so channel id) another suggestion already has. A preset lineup channel
    /// wins over a generic one, since it is the more carefully made; otherwise the first stays.
    /// </summary>
    /// <param name="suggestions">Suggestions, in display order.</param>
    /// <returns>The list without duplicates, in the same order.</returns>
    public static List<ChannelSuggestion> OneChannelPerName(IEnumerable<ChannelSuggestion> suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        var all = suggestions.ToList();
        var winner = new Dictionary<string, ChannelSuggestion>(StringComparer.Ordinal);
        foreach (var suggestion in all)
        {
            var id = ChannelDefaults.IdFor(suggestion.Channel.Name);
            if (!winner.TryGetValue(id, out var current) || (suggestion.Preset && !current.Preset))
            {
                winner[id] = suggestion;
            }
        }

        return all.Where(s => ReferenceEquals(winner[ChannelDefaults.IdFor(s.Channel.Name)], s)).ToList();
    }
}
