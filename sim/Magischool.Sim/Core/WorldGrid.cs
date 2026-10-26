using System;

namespace Magischool.Sim.Core;

/// <summary>
/// The world as flat per-property arrays (structure of arrays), indexed by
/// <c>(z * Height + y) * Width + x</c>. One tile is one cubic metre.
/// z = 0 is the lowest layer.
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

    /// <summary>Material id per tile, see <see cref="Materials.MaterialTable"/>.</summary>
    public readonly ushort[] Material;

    /// <summary>Temperature per tile in kelvin.</summary>
    public readonly float[] Temperature;

    public WorldGrid(int width, int height, int depth)
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
        Material = new ushort[Count];
        Temperature = new float[Count];
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

    public void Fill(ushort material, float temperatureK)
    {
        Array.Fill(Material, material);
        Array.Fill(Temperature, temperatureK);
    }

    public void Set(int x, int y, int z, ushort material, float temperatureK)
    {
        int i = Index(x, y, z);
        Material[i] = material;
        Temperature[i] = temperatureK;
    }

    public WorldGrid Clone()
    {
        var copy = new WorldGrid(Width, Height, Depth);
        Array.Copy(Material, copy.Material, Count);
        Array.Copy(Temperature, copy.Temperature, Count);
        return copy;
    }
}
