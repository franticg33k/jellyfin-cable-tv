namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// What a slot plays. Serialized in lower case in the API contract.
/// </summary>
public enum SlotKind
{
    /// <summary>A programme (or one segment of it, when split by a break).</summary>
    Program = 0,

    /// <summary>A commercial.</summary>
    Commercial = 1,

    /// <summary>A channel bumper.</summary>
    Bumper = 2,

    /// <summary>Filler that pads to the grid.</summary>
    Filler = 3,

    /// <summary>An outside HLS/TS stream.</summary>
    Stream = 4,

    /// <summary>Content rendered on the client, for example weather.</summary>
    Generated = 5,
}
