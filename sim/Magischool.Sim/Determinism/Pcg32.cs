using System;

namespace Magischool.Sim.Determinism;

/// <summary>
/// PCG32 (XSH RR 64/32), matching the reference pcg32_random_r. Small,
/// fast, and its whole state is two ulongs, so it saves and replays exactly.
/// Use this, never System.Random, anywhere in the sim.
/// </summary>
public struct Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;

    public ulong State;
    public ulong Increment;

    public Pcg32(ulong seed, ulong sequence = 0xDA3E39CB94B95BDBUL)
    {
        State = 0;
        Increment = (sequence << 1) | 1UL;
        NextUInt();
        State += seed;
        NextUInt();
    }

    public uint NextUInt()
    {
        ulong old = State;
        State = unchecked(old * Multiplier + Increment);
        uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
    }

    /// <summary>Uniform in [0, bound), without modulo bias.</summary>
    public uint NextUInt(uint bound)
    {
        if (bound == 0) throw new ArgumentOutOfRangeException(nameof(bound));
        uint threshold = (uint)(-(int)bound) % bound;
        while (true)
        {
            uint r = NextUInt();
            if (r >= threshold) return r % bound;
        }
    }

    /// <summary>Uniform in [min, maxExclusive).</summary>
    public int NextInt(int min, int maxExclusive)
    {
        if (maxExclusive <= min) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        return min + (int)NextUInt((uint)(maxExclusive - min));
    }

    /// <summary>Uniform in [0, 1), 24 bits of precision.</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / (1 << 24));

    public bool Chance(float probability) => NextFloat() < probability;
}
