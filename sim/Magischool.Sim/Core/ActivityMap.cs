using System;
using System.Collections.Generic;

namespace Magischool.Sim.Core;

/// <summary>
/// Splits the world into chunks and tracks which are awake. Physics layers
/// only simulate awake chunks; a chunk falls asleep once nothing in it or on
/// its border is changing, and wakes when a neighbour starts pushing on it or
/// a command touches it. This is what lets a whole city sit through a two-hour
/// class without simulating every quiet basement.
///
/// Sleeping is part of the world state (saved, hashed, replayed), so results
/// stay deterministic.
/// </summary>
public sealed class ActivityMap
{
    public const int SizeX = 16;
    public const int SizeY = 16;
    public const int SizeZ = 8;

    private readonly int _width, _height, _depth;
    private readonly List<int> _awakeList = new();

    public ActivityMap(int width, int height, int depth)
    {
        _width = width;
        _height = height;
        _depth = depth;
        ChunksX = (width + SizeX - 1) / SizeX;
        ChunksY = (height + SizeY - 1) / SizeY;
        ChunksZ = (depth + SizeZ - 1) / SizeZ;
        Count = ChunksX * ChunksY * ChunksZ;
        Awake = new bool[Count];
        Stirred = new bool[Count];
        Array.Fill(Awake, true);
    }

    public readonly int ChunksX, ChunksY, ChunksZ, Count;

    /// <summary>Per chunk: true when its physics runs this tick.</summary>
    public readonly bool[] Awake;

    /// <summary>
    /// Per chunk: set during a tick when something moved in it (or wants to move into it,
    /// for a sleeping chunk). <see cref="ActivitySystem"/> keeps stirred chunks awake, wakes
    /// stirred sleepers, and clears the flags. Systems only ever set flags to true, so
    /// parallel writers can't disagree.
    /// </summary>
    internal readonly bool[] Stirred;

    public int AwakeCount
    {
        get
        {
            int n = 0;
            foreach (bool a in Awake) if (a) n++;
            return n;
        }
    }

    public int ChunkAt(int x, int y, int z) => ((z / SizeZ) * ChunksY + y / SizeY) * ChunksX + x / SizeX;

    public bool IsAwakeAt(int x, int y, int z) => Awake[ChunkAt(x, y, z)];

    /// <summary>Tile bounds of a chunk, max exclusive.</summary>
    public (int X0, int Y0, int Z0, int X1, int Y1, int Z1) Bounds(int chunk)
    {
        int cx = chunk % ChunksX;
        int rest = chunk / ChunksX;
        int cy = rest % ChunksY;
        int cz = rest / ChunksY;
        int x0 = cx * SizeX, y0 = cy * SizeY, z0 = cz * SizeZ;
        return (x0, y0, z0, Math.Min(x0 + SizeX, _width), Math.Min(y0 + SizeY, _height), Math.Min(z0 + SizeZ, _depth));
    }

    /// <summary>Wake the chunk holding this tile and every chunk touching it.</summary>
    public void WakeAround(int x, int y, int z)
    {
        int cx = x / SizeX, cy = y / SizeY, cz = z / SizeZ;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int nx = cx + dx, ny = cy + dy, nz = cz + dz;
            if ((uint)nx < (uint)ChunksX && (uint)ny < (uint)ChunksY && (uint)nz < (uint)ChunksZ)
                Awake[(nz * ChunksY + ny) * ChunksX + nx] = true;
        }
    }

    public void WakeAll() => Array.Fill(Awake, true);

    /// <summary>Awake chunk ids in ascending order. The list is reused; don't hold on to it across ticks.</summary>
    public List<int> AwakeChunks()
    {
        _awakeList.Clear();
        for (int c = 0; c < Count; c++)
            if (Awake[c]) _awakeList.Add(c);
        return _awakeList;
    }
}
