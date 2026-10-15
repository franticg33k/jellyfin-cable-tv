using System;

namespace Jellyfin.Plugin.CableTv.Logos;

/// <summary>
/// Checks an uploaded logo image before it is kept.
/// </summary>
public static class LogoUpload
{
    /// <summary>The largest upload accepted, in bytes.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Prefix of a channel logo setting that names an uploaded file.</summary>
    public const string Prefix = "upload:";

    /// <summary>
    /// Tells what kind of image the bytes are from their first bytes, so a renamed file of another kind isn't kept.
    /// </summary>
    /// <param name="data">The file.</param>
    /// <returns>The file extension (".png", ".jpg", ".gif", ".webp"), or null when it isn't a supported image.</returns>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ".png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ".jpg";
        }

        if (data.Length >= 6 && data[..4].SequenceEqual("GIF8"u8))
        {
            return ".gif";
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        return null;
    }
}
