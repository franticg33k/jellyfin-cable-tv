namespace Jellyfin.Plugin.CableTv.Scheduling;

/// <summary>
/// Small deterministic PRNG. Used instead of <see cref="System.Random"/>, whose seeded sequence is not a documented contract.
/// </summary>
internal struct SplitMix64
{
    private ulong _state;

    public SplitMix64(ulong seed) => _state = seed;

    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Returns a value in [0, bound).</summary>
    public int NextInt(int bound) => (int)(Next() % (ulong)bound);
}
