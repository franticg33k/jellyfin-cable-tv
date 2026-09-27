namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// Whether a channel's Live TV stream burns subtitles into the picture.
/// </summary>
public enum ChannelSubtitles
{
    /// <summary>Follow the global setting.</summary>
    Default = 0,

    /// <summary>Never burn subtitles in.</summary>
    Off = 1,

    /// <summary>Burn in the preferred-language track when an item has one.</summary>
    BurnIn = 2,
}
