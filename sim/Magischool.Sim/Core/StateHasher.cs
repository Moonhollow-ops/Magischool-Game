using System;
using System.Runtime.InteropServices;
using Magischool.Sim.Determinism;

namespace Magischool.Sim.Core;

internal static class StateHasher
{
    public static ulong Hash(Simulation sim)
    {
        var grid = sim.Grid;
        Span<long> header = stackalloc long[]
        {
            sim.Tick,
            unchecked((long)sim.Rng.State),
            unchecked((long)sim.Rng.Increment),
            grid.Width,
            grid.Height,
            grid.Depth,
        };

        ulong hash = SeedHash.Hash(MemoryMarshal.AsBytes(header));
        hash = SeedHash.Hash(MemoryMarshal.AsBytes(grid.Material.AsSpan()), hash);
        hash = SeedHash.Hash(MemoryMarshal.AsBytes(grid.Temperature.AsSpan()), hash);
        hash = SeedHash.Hash(MemoryMarshal.AsBytes(grid.Mass.AsSpan()), hash);
        hash = SeedHash.Hash(MemoryMarshal.AsBytes(grid.LatentEnergy.AsSpan()), hash);
        hash = SeedHash.Hash(MemoryMarshal.AsBytes(sim.Activity.Awake.AsSpan()), hash);
        return hash;
    }
}
