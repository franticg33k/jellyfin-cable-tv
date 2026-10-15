using System;
using System.Globalization;
using Jellyfin.Plugin.CableTv.Configuration;

namespace Jellyfin.Plugin.CableTv.Logos;

/// <summary>
/// What a generated logo looks like once a channel's own choices and the plugin-wide defaults are put together.
/// </summary>
/// <param name="Style">Plate style.</param>
/// <param name="Text">The text drawn.</param>
/// <param name="Background">Plate or outline colour as "#RRGGBB", or null to derive one from the text.</param>
/// <param name="TextColor">Text colour as "#RRGGBB", or null for the style's own (white on plates, the accent colour otherwise).</param>
public sealed record LogoLook(LogoStyle Style, string Text, string? Background, string? TextColor)
{
    /// <summary>Gets a short string that changes whenever the look does, for naming cached images.</summary>
    public string Key => string.Join('|', Style, Text, Background, TextColor);

    /// <summary>
    /// Puts a channel's choices over the defaults: whatever the channel leaves unset comes from the defaults, and what
    /// they leave unset comes from the built-in look (a plate coloured from the name, white text).
    /// </summary>
    /// <param name="name">Channel name.</param>
    /// <param name="text">The channel's own text, or empty to draw the name.</param>
    /// <param name="style">The channel's style.</param>
    /// <param name="background">The channel's colour.</param>
    /// <param name="textColor">The channel's text colour.</param>
    /// <param name="defaultStyle">The default style.</param>
    /// <param name="defaultBackground">The default colour.</param>
    /// <param name="defaultTextColor">The default text colour.</param>
    /// <returns>The look.</returns>
    public static LogoLook Create(
        string name,
        string? text,
        LogoStyle style,
        string? background,
        string? textColor,
        LogoStyle defaultStyle,
        string? defaultBackground,
        string? defaultTextColor)
    {
        var resolved = style != LogoStyle.Default ? style : defaultStyle != LogoStyle.Default ? defaultStyle : LogoStyle.Plate;
        var drawn = string.IsNullOrWhiteSpace(text) ? name : text;
        return new LogoLook(
            resolved,
            (drawn ?? string.Empty).Trim(),
            NormalizeColor(background) ?? NormalizeColor(defaultBackground),
            NormalizeColor(textColor) ?? NormalizeColor(defaultTextColor));
    }

    /// <summary>
    /// Reads "#RGB" or "#RRGGBB" (the # is optional).
    /// </summary>
    /// <param name="value">The colour as typed.</param>
    /// <param name="red">Red.</param>
    /// <param name="green">Green.</param>
    /// <param name="blue">Blue.</param>
    /// <returns>True when the value is a colour.</returns>
    public static bool TryParseColor(string? value, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        var hex = value?.Trim().TrimStart('#');
        if (hex is null || hex.Length is not (3 or 6))
        {
            return false;
        }

        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        red = (byte)(rgb >> 16);
        green = (byte)(rgb >> 8);
        blue = (byte)rgb;
        return true;
    }

    /// <summary>
    /// A colour as "#RRGGBB", or null when it isn't one.
    /// </summary>
    /// <param name="value">The colour as typed.</param>
    /// <returns>The normalized colour.</returns>
    public static string? NormalizeColor(string? value)
        => TryParseColor(value, out var r, out var g, out var b) ? string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}") : null;
}
