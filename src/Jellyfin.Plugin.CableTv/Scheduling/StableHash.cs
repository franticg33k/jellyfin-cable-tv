using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// Process-independent hashing. <see cref="string.GetHashCode()"/> is randomized per process and must never seed a schedule.
/// </summary>
internal static class StableHash
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Starts a hash.</summary>
    public static ulong Start() => FnvOffset;

    /// <summary>Mixes a string into the hash.</summary>
    public static ulong Add(ulong hash, string value)
    {
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash = (hash ^ b) * FnvPrime;
        }

        // Field separator so ("ab","c") and ("a","bc") differ.
        return (hash ^ 0xFF) * FnvPrime;
    }

    /// <summary>Mixes a number into the hash.</summary>
    public static ulong Add(ulong hash, long value)
    {
        var v = unchecked((ulong)value);
        for (var i = 0; i < 8; i++)
        {
            hash = (hash ^ (v & 0xFF)) * FnvPrime;
            v >>= 8;
        }

        return hash;
    }

    /// <summary>Mixes a Guid into the hash.</summary>
    public static ulong Add(ulong hash, Guid value) => Add(hash, value.ToString("N", CultureInfo.InvariantCulture));

    /// <summary>Formats a hash as lower-case hex of the given length.</summary>
    public static string ToHex(ulong hash, int length = 16)
        => hash.ToString("x16", CultureInfo.InvariantCulture)[..Math.Clamp(length, 1, 16)];
}
