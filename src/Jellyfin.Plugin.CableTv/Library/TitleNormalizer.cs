using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.CableTv.Library;

/// <summary>
/// Turns titles into lookup keys that survive the usual differences between a lineup list and library metadata:
/// case, accents, punctuation, "&amp;" versus "and", a leading article, and a year in parentheses.
/// </summary>
public static partial class TitleNormalizer
{
    /// <summary>
    /// Splits "Title (1999)" into the title and the year.
    /// </summary>
    /// <param name="value">Title, optionally followed by a year in parentheses.</param>
    /// <returns>The title and the year, if any.</returns>
    public static (string Title, int? Year) SplitYear(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var match = TrailingYear().Match(value);
        if (!match.Success)
        {
            return (value.Trim(), null);
        }

        return (value[..match.Index].Trim(), int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The lookup key for a title: lower case, accents and punctuation removed, "&amp;" read as "and", a leading
    /// "the", "a" or "an" dropped, and any trailing "(year)" ignored.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The key; empty when nothing is left.</returns>
    public static string Key(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var (bare, _) = SplitYear(title);
        var decomposed = bare.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var raw in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark || raw is '\'' or '’' or '`')
            {
                continue;
            }

            if (raw == '&' || raw == '+')
            {
                Append(builder, raw == '&' ? "and" : "plus", ref pendingSpace);
                continue;
            }

            var c = char.ToLowerInvariant(raw);
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(c);
            }
            else
            {
                pendingSpace = true;
            }
        }

        var key = builder.ToString();
        foreach (var article in Articles)
        {
            if (key.StartsWith(article, StringComparison.Ordinal) && key.Length > article.Length)
            {
                return key[article.Length..];
            }
        }

        return key;
    }

    /// <summary>
    /// A looser key with spaces removed too, so "Spider-Man" and "Spiderman" meet.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The key.</returns>
    public static string LooseKey(string title) => Key(title).Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// A file-name friendly slug of a channel name, used to look logos up in a logo pack ("Cartoon Network" becomes
    /// "cartoon_network", "HBO+" becomes "hbo_plus").
    /// </summary>
    /// <param name="name">The channel name.</param>
    /// <returns>The slug.</returns>
    public static string Slug(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var raw in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark || raw is '\'' or '’' or '.')
            {
                continue;
            }

            if (raw == '+')
            {
                builder.Append("_plus");
                continue;
            }

            if (raw == '&')
            {
                builder.Append("_and_");
                continue;
            }

            var c = char.ToLowerInvariant(raw);
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return CollapseUnderscores().Replace(builder.ToString(), "_").Trim('_');
    }

    private static readonly string[] Articles = ["the ", "a ", "an "];

    private static void Append(StringBuilder builder, string word, ref bool pendingSpace)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(word);
        pendingSpace = true;
    }

    [GeneratedRegex(@"\s*\((\d{4})\)\s*$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex("_+")]
    private static partial Regex CollapseUnderscores();
}
