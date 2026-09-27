namespace Jellyfin.Plugin.CableTv.Configuration;

/// <summary>
/// What a channel airs.
/// </summary>
public enum ChannelKind
{
    /// <summary>Items from the library on a schedule.</summary>
    Standard = 0,

    /// <summary>An outside HLS or MPEG-TS stream, passed through as is.</summary>
    Stream = 1,

    /// <summary>Local weather, drawn by the client from forecast data (a still card on plain Live TV).</summary>
    Weather = 2,
}
