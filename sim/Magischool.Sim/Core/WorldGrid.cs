using System;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Core;

/// <summary>
/// The world as flat per-property arrays (structure of arrays), indexed by
/// <c>(z * Height + y) * Width + x</c>. One tile is one cubic metre.
/// z = 0 is the lowest layer.
///
/// Each tile holds one material with an explicit mass. A tile filled exactly
/// to its material's density has <see cref="FillFraction"/> 1. Less than 1
/// means part of the tile is empty (melted ice is 917 kg of water, not a full
/// 1000 kg); more than 1 means the material is squeezed in under pressure
/// (freezing water expands), which the structure layer turns into stress.
///
/// Each physics layer adds its own channel here. Keep channels as flat
/// primitive arrays: no per-tile objects, no dictionaries.
/// </summary>
public sealed class WorldGrid
{
    public const float TileVolumeM3 = 1f;

    public readonly int Width;
    public readonly int Height;
    public readonly int Depth;
    public readonly int Count;
    public readonly MaterialTable Materials;

    /// <summary>Material id per tile, see <see cref="Materials.MaterialTable"/>.</summary>
    public readonly ushort[] Material;

    /// <summary>
    /// Temperature per tile in kelvin. Double, not float: a float can't resolve
    /// the tiny per-tick changes of slow conduction at room temperature, and the
    /// rounding silently destroys energy.
    /// </summary>
    public readonly double[] Temperature;

    /// <summary>Mass of the tile's material in kg.</summary>
    public readonly double[] Mass;

    /// <summary>
    /// Energy (J) a tile is holding part-way through a phase change, while its
    /// temperature is pinned at the transition point. Positive: absorbed toward
    /// melting or boiling. Negative: released toward freezing or condensing.
    /// </summary>
    public readonly double[] LatentEnergy;

    public WorldGrid(int width, int height, int depth, MaterialTable materials)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Grid dimensions must be positive.");
        long count = (long)width * height * depth;
        if (count > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "Grid is too large.");

        Width = width;
        Height = height;
        Depth = depth;
        Count = (int)count;
        Materials = materials;
        Material = new ushort[Count];
        Temperature = new double[Count];
        Mass = new double[Count];
        LatentEnergy = new double[Count];
    }

    public int Index(int x, int y, int z) => (z * Height + y) * Width + x;

    public (int X, int Y, int Z) Coordinates(int index)
    {
        int x = index % Width;
        int rest = index / Width;
        return (x, rest % Height, rest / Height);
    }

    public bool InBounds(int x, int y, int z) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height && (uint)z < (uint)Depth;

    /// <summary>Fill every tile completely with one material.</summary>
    public void Fill(ushort material, double temperatureK)
    {
        Array.Fill(Material, material);
        Array.Fill(Temperature, temperatureK);
        Array.Fill(Mass, Materials[material].FullTileMass);
        Array.Clear(LatentEnergy);
    }

    /// <summary>Fill one tile completely with a material.</summary>
    public void Set(int x, int y, int z, ushort material, double temperatureK) =>
        SetAt(Index(x, y, z), material, temperatureK, Materials[material].FullTileMass);

    /// <summary>Put an exact mass of a material in one tile.</summary>
    public void SetAt(int index, ushort material, double temperatureK, double massKg)
    {
        Material[index] = material;
        Temperature[index] = temperatureK;
        Mass[index] = massKg;
        LatentEnergy[index] = 0.0;
    }

    /// <summary>Heat capacity of one tile's contents, J/K.</summary>
    public double HeatCapacity(int index) => Mass[index] * Materials[Material[index]].SpecificHeat;

    /// <summary>How full a tile is relative to its material's natural density (1 = exactly full).</summary>
    public double FillFraction(int index) => Mass[index] / Materials[Material[index]].FullTileMass;

    public WorldGrid Clone()
    {
        var copy = new WorldGrid(Width, Height, Depth, Materials);
        Array.Copy(Material, copy.Material, Count);
        Array.Copy(Temperature, copy.Temperature, Count);
        Array.Copy(Mass, copy.Mass, Count);
        Array.Copy(LatentEnergy, copy.LatentEnergy, Count);
        return copy;
    }
}
