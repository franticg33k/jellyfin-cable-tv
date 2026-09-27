using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// A subtitle track of a library item, as far as burning it into the Live TV stream needs to know.
/// </summary>
/// <param name="EmbeddedIndex">Position among the file's own subtitle streams (ffmpeg's <c>0:s:N</c>); -1 for an external file.</param>
/// <param name="Codec">Codec, for example "subrip", "ass" or "hdmv_pgs_subtitle".</param>
/// <param name="Language">ISO 639-2 language, for example "eng", or null.</param>
/// <param name="Path">The subtitle file for an external track, else null.</param>
/// <param name="IsForced">Whether the track only covers foreign-language parts.</param>
/// <param name="IsDefault">Whether the file marks the track as default.</param>
public sealed record SubtitleTrack(int EmbeddedIndex, string? Codec, string? Language, string? Path, bool IsForced, bool IsDefault)
{
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "vtt", "text", "subviewer", "microdvd",
    };

    private static readonly HashSet<string> BitmapCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "pgs", "dvd_subtitle", "dvdsub", "dvb_subtitle", "dvbsub",
    };

    /// <summary>Gets a value indicating whether the track is in a separate file.</summary>
    public bool IsExternal => Path is not null;

    /// <summary>Gets a value indicating whether the track is text (rendered with libass) rather than images.</summary>
    public bool IsText => Codec is not null && TextCodecs.Contains(Codec);

    /// <summary>Gets a value indicating whether ffmpeg can burn the track in.</summary>
    public bool CanBurnIn => IsText || (!IsExternal && Codec is not null && BitmapCodecs.Contains(Codec));

    /// <summary>
    /// Picks the track to burn in: the wanted language (a full track before a forced one), or with no language set the
    /// default track, else the first. Returns null when nothing suitable exists, so the item airs without subtitles.
    /// </summary>
    /// <param name="tracks">The item's subtitle tracks.</param>
    /// <param name="language">Wanted language ("eng", "en", ...); empty for the item's default.</param>
    /// <returns>The track, or null.</returns>
    public static SubtitleTrack? Pick(IReadOnlyList<SubtitleTrack> tracks, string? language)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var usable = tracks.Where(t => t.CanBurnIn).ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            return usable.FirstOrDefault(t => t.IsDefault && !t.IsForced) ?? usable.FirstOrDefault(t => !t.IsForced);
        }

        var wanted = LanguageCodes(language.Trim());
        var matching = usable.Where(t => t.Language is not null && wanted.Contains(t.Language)).ToList();
        return matching.FirstOrDefault(t => !t.IsForced) ?? matching.FirstOrDefault();
    }

    private static HashSet<string> LanguageCodes(string language)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { language };
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            codes.Add(culture.ThreeLetterISOLanguageName);
            codes.Add(culture.TwoLetterISOLanguageName);
        }
        catch (CultureNotFoundException)
        {
            // Not a code .NET knows; match it as written.
        }

        // ISO 639-2/B codes Jellyfin often stores, next to the /T codes .NET returns.
        foreach (var (terminology, bibliographic) in new[] { ("deu", "ger"), ("fra", "fre"), ("zho", "chi"), ("nld", "dut"), ("ces", "cze"), ("ell", "gre"), ("fas", "per"), ("ron", "rum"), ("slk", "slo"), ("msa", "may"), ("sqi", "alb"), ("hye", "arm"), ("eus", "baq"), ("mya", "bur"), ("kat", "geo"), ("isl", "ice"), ("mkd", "mac"), ("mri", "mao"), ("bod", "tib"), ("cym", "wel") })
        {
            if (codes.Contains(terminology) || codes.Contains(bibliographic))
            {
                codes.Add(terminology);
                codes.Add(bibliographic);
            }
        }

        return codes;
    }
}
