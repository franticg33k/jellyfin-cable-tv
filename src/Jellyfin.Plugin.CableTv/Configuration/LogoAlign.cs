namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// Where a channel's logo sits in its image when it is smaller than the whole image (see <see cref="ChannelDefinition.LogoScale"/>).
/// </summary>
public enum LogoAlign
{
    /// <summary>Centered.</summary>
    Center = 0,

    /// <summary>Top, centered.</summary>
    Top = 1,

    /// <summary>Bottom, centered.</summary>
    Bottom = 2,

    /// <summary>Left, centered vertically.</summary>
    Left = 3,

    /// <summary>Right, centered vertically.</summary>
    Right = 4,

    /// <summary>Top left corner.</summary>
    TopLeft = 5,

    /// <summary>Top right corner.</summary>
    TopRight = 6,

    /// <summary>Bottom left corner.</summary>
    BottomLeft = 7,

    /// <summary>Bottom right corner.</summary>
    BottomRight = 8,
}
