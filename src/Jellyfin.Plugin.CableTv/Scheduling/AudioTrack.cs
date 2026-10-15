using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// An audio track of a library item, as far as choosing which one the Live TV stream carries needs to know.
/// </summary>
/// <param name="EmbeddedIndex">Position among the file's own audio streams (ffmpeg's <c>0:a:N</c>).</param>
/// <param name="Language">ISO 639-2 language, for example "jpn", or null.</param>
/// <param name="IsDefault">Whether the file marks the track as default.</param>
public sealed record AudioTrack(int EmbeddedIndex, string? Language, bool IsDefault)
{
    /// <summary>
    /// Picks the track to stream: the wanted language, or with no language set (or none in that language) the track the
    /// file marks as default, else the first.
    /// </summary>
    /// <param name="tracks">The item's audio tracks.</param>
    /// <param name="language">Wanted language ("eng", "en", ...); empty for the file's default.</param>
    /// <returns>The track's index among the audio streams; 0 when the item has no track information.</returns>
    public static int Pick(IReadOnlyList<AudioTrack> tracks, string? language)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            var wanted = SubtitleTrack.LanguageCodes(language.Trim());
            var match = tracks.FirstOrDefault(t => t.Language is not null && wanted.Contains(t.Language));
            if (match is not null)
            {
                return match.EmbeddedIndex;
            }
        }

        return (tracks.FirstOrDefault(t => t.IsDefault) ?? tracks[0]).EmbeddedIndex;
    }
}
