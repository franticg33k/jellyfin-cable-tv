namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// How the continuous Live TV stream treats video.
/// </summary>
public enum FallbackStreamMode
{
    /// <summary>
    /// Copy video from items in the channel's main codec and transcode only the rest. Audio is always re-encoded.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Always copy video. Cheapest; mixed codecs or resolutions on one channel may upset some players.
    /// </summary>
    Copy = 1,

    /// <summary>
    /// Always transcode to H.264 at a fixed resolution. Works everywhere; costs CPU per watched channel.
    /// </summary>
    Transcode = 2,

    /// <summary>
    /// No continuous stream: tuning in plays the airing file from its start (phase 1 behaviour).
    /// </summary>
    Off = 3,
}
