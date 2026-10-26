using System;
using System.Text;

namespace Magischool.Sim.Determinism;

/// <summary>
/// Stable 64-bit hashing (FNV-1a). Never use <see cref="string.GetHashCode()"/>
/// in the sim: .NET randomises it per process, which breaks seeds and replays.
/// </summary>
public static class SeedHash
{
    public const ulong OffsetBasis = 0xCBF29CE484222325UL;
    public const ulong Prime = 0x100000001B3UL;

    public static ulong Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    public static ulong Hash(ReadOnlySpan<byte> bytes, ulong hash = OffsetBasis)
    {
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= Prime;
        }
        return hash;
    }

    /// <summary>
    /// An independent seed for one subsystem ("worldgen", "weather", ...).
    /// Separate streams mean adding randomness in one system never shifts
    /// the rolls of another for the same master seed.
    /// </summary>
    public static ulong Derive(string masterSeed, string stream) => Hash(masterSeed + "/" + stream);
}
