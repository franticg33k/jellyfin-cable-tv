using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.CableTv.Packs;

/// <summary>
/// One title on a channel in a CSV lineup.
/// </summary>
/// <param name="Number">Channel number.</param>
/// <param name="Channel">Channel name.</param>
/// <param name="Title">Show or movie title.</param>
/// <param name="Year">Release year, used to tell remakes apart; null when not given.</param>
public sealed record LineupRow(string Number, string Channel, string Title, int? Year);

/// <summary>
/// One pinned episode in an episodes CSV.
/// </summary>
/// <param name="Show">Show title, optionally with "(Year)".</param>
/// <param name="Episode">Episode title.</param>
public sealed record EpisodeRow(string Show, string Episode);

/// <summary>
/// Reads and writes lineup CSVs: <c>Channel Number,Channel Name,Title,Release Year</c>, one row per title, and episode
/// CSVs: <c>Show Title,Episode Title</c>. Quoting follows RFC 4180; a byte-order mark and a header row are optional.
/// </summary>
public static class LineupCsv
{
    /// <summary>Header written on export.</summary>
    public const string LineupHeader = "Channel Number,Channel Name,Title,Release Year";

    /// <summary>
    /// Splits CSV text into records.
    /// </summary>
    /// <param name="text">CSV text.</param>
    /// <returns>The records, blank lines skipped.</returns>
    public static List<string[]> ReadRecords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var start = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                case '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    EndRecord();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        EndRecord();
        return records;

        void EndRecord()
        {
            fields.Add(field.ToString());
            field.Clear();
            if (fields.Count > 1 || fields[0].Trim().Length > 0)
            {
                records.Add(fields.Select(f => f.Trim()).ToArray());
            }

            fields.Clear();
        }
    }

    /// <summary>
    /// Whether the CSV is an episodes file (its header names an episode column).
    /// </summary>
    /// <param name="text">CSV text.</param>
    /// <returns>True for an episodes CSV.</returns>
    public static bool IsEpisodes(string text)
    {
        var firstLine = (text ?? string.Empty).TrimStart('﻿').Split('\n', 2)[0];
        return firstLine.Contains("episode", StringComparison.OrdinalIgnoreCase)
               && !firstLine.Contains("channel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a lineup CSV. With a header, columns are found by name (<c>Channel Number</c>/<c>Number</c>,
    /// <c>Channel Name</c>/<c>Channel</c>, <c>Title</c>/<c>Show</c>, <c>Release Year</c>/<c>Year</c>); without one they
    /// are taken in that order. A title may carry its year as "Title (1999)".
    /// </summary>
    /// <param name="text">CSV text.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="FormatException">The CSV has no channel or title column.</exception>
    public static List<LineupRow> ParseLineup(string text)
    {
        var records = ReadRecords(text);
        if (records.Count == 0)
        {
            return [];
        }

        int number = 0, channel = 1, title = 2, year = 3;
        var header = records[0];
        if (header.Any(h => h.Contains("title", StringComparison.OrdinalIgnoreCase) || h.Contains("channel", StringComparison.OrdinalIgnoreCase)))
        {
            number = Column(header, h => h.Contains("number", StringComparison.OrdinalIgnoreCase) || h.Equals("ch", StringComparison.OrdinalIgnoreCase));
            channel = Column(header, h => (h.Contains("channel", StringComparison.OrdinalIgnoreCase) && !h.Contains("number", StringComparison.OrdinalIgnoreCase))
                                          || h.Equals("name", StringComparison.OrdinalIgnoreCase)
                                          || h.Equals("network", StringComparison.OrdinalIgnoreCase));
            title = Column(header, h => h.Contains("title", StringComparison.OrdinalIgnoreCase) || h.Equals("show", StringComparison.OrdinalIgnoreCase) || h.Equals("movie", StringComparison.OrdinalIgnoreCase));
            year = Column(header, h => h.Contains("year", StringComparison.OrdinalIgnoreCase));
            records.RemoveAt(0);
        }
        else if (header.Length == 3)
        {
            // Three columns without a header: name, title, year when the last is a year, else number, name, title.
            (number, channel, title, year) = ParseYear(header[2]) is not null ? (-1, 0, 1, 2) : (0, 1, 2, -1);
        }

        if (title < 0 || (channel < 0 && number < 0))
        {
            throw new FormatException("The CSV needs a Title column and a Channel Name or Channel Number column.");
        }

        var rows = new List<LineupRow>(records.Count);
        foreach (var record in records)
        {
            var name = Get(record, channel);
            var num = Get(record, number);
            var show = Get(record, title);
            if (show.Length == 0 || (name.Length == 0 && num.Length == 0))
            {
                continue;
            }

            rows.Add(new LineupRow(num, name.Length > 0 ? name : num, show, ParseYear(Get(record, year))));
        }

        return rows;
    }

    /// <summary>
    /// Parses an episodes CSV: show title, then episode title.
    /// </summary>
    /// <param name="text">CSV text.</param>
    /// <returns>The episodes.</returns>
    public static List<EpisodeRow> ParseEpisodes(string text)
    {
        var records = ReadRecords(text);
        int show = 0, episode = 1;
        if (records.Count > 0 && records[0].Any(h => h.Contains("episode", StringComparison.OrdinalIgnoreCase)))
        {
            var header = records[0];
            episode = Column(header, h => h.Contains("episode", StringComparison.OrdinalIgnoreCase));
            show = Column(header, h => (h.Contains("show", StringComparison.OrdinalIgnoreCase) || h.Contains("series", StringComparison.OrdinalIgnoreCase)
                                        || h.Equals("title", StringComparison.OrdinalIgnoreCase)) && !h.Contains("episode", StringComparison.OrdinalIgnoreCase));
            records.RemoveAt(0);
        }

        return records
            .Select(r => new EpisodeRow(Get(r, show), Get(r, episode)))
            .Where(r => r.Show.Length > 0 && r.Episode.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Writes a lineup CSV with a header row.
    /// </summary>
    /// <param name="rows">Rows.</param>
    /// <returns>CSV text.</returns>
    public static string WriteLineup(IEnumerable<LineupRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder(LineupHeader).Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(Quote(row.Number)).Append(',')
                .Append(Quote(row.Channel)).Append(',')
                .Append(Quote(row.Title)).Append(',')
                .Append(row.Year?.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes a field when it holds a comma, quote or line break.
    /// </summary>
    /// <param name="value">Field.</param>
    /// <returns>The CSV field.</returns>
    public static string Quote(string? value)
    {
        value ??= string.Empty;
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || value != value.Trim()
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static int? ParseYear(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is >= 1880 and <= 2200 ? year : null;

    private static int Column(string[] header, Func<string, bool> match) => Array.FindIndex(header, h => match(h));

    private static string Get(string[] record, int index) => index >= 0 && index < record.Length ? record[index] : string.Empty;
}
