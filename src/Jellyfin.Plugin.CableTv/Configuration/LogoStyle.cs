namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// How a generated channel logo (the one drawn when a channel has no logo of its own) looks.
/// </summary>
public enum LogoStyle
{
    /// <summary>Not chosen: the channel uses the plugin-wide style, which is Plate unless changed.</summary>
    Default = 0,

    /// <summary>The name on a rounded plate with a gradient and a light border.</summary>
    Plate = 1,

    /// <summary>The name on a plain, solid rounded plate.</summary>
    Flat = 2,

    /// <summary>The name inside a coloured outline, without a fill.</summary>
    Outline = 3,

    /// <summary>Only the name, in colour, with no plate.</summary>
    TextOnly = 4,
}
