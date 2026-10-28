using System;
using System.Collections.Generic;
using Magischool.Sim.Core;

namespace Magischool.Sim.Perception;

/// <summary>
/// The open air around one ear, out to a box of tiles: the shortest way through it from the ear
/// to every tile (in tiles, stepping to any of 26 neighbours but never squeezing between two
/// solids that meet at a corner), and, when the air is closed in within the box, the room it
/// makes: its walls' area and the sound they absorb. Rebuilt only when the ear moves or the air
/// in the box opens or closes; otherwise the last one is reused, which gives the same answer.
/// </summary>
internal sealed class SoundRegion
{
    public int X0, Y0, Width, Height, Depth;
    public int EarX, EarY, EarZ;
    /// <summary>How far round the ear the box reaches sideways, tiles.</summary>
    public int Radius;
    public double[] Distance = Array.Empty<double>();
    // Per tile: 0 open, else 1 + the material shutting it, so a wall that changes is noticed.
    private ushort[] _blocked = Array.Empty<ushort>();

    /// <summary>Whether the air reached is closed in within the box (otherwise it is the open world).</summary>
    public bool Bounded;
    /// <summary>Area of the surfaces around the air, m², and their absorption, m² (sky and open edges absorb all).</summary>
    public double SurfaceM2, AbsorptionM2;
    /// <summary>Of that area, how much opens onto the sky or the world's edge, m².</summary>
    public double OpenM2;
    /// <summary>Volume of the air reached, m³.</summary>
    public double VolumeM3;

    private readonly PriorityQueue<int, double> _queue = new();

    private static readonly (int Dx, int Dy, int Dz, double Step)[] Steps = BuildSteps();

    private static (int, int, int, double)[] BuildSteps()
    {
        var list = new List<(int, int, int, double)>();
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (dx != 0 || dy != 0 || dz != 0) list.Add((dx, dy, dz, Math.Sqrt(dx * dx + dy * dy + dz * dz)));
        return list.ToArray();
    }

    /// <summary>Makes this the region around an ear in tile (ex, ey, ez), out to <paramref name="radius"/> tiles sideways.</summary>
    public void Build(WorldGrid grid, int ex, int ey, int ez, int radius)
    {
        int x0 = Math.Max(0, ex - radius), x1 = Math.Min(grid.Width, ex + radius + 1);
        int y0 = Math.Max(0, ey - radius), y1 = Math.Min(grid.Height, ey + radius + 1);
        int w = x1 - x0, h = y1 - y0, d = grid.Depth, n = w * h * d;
        bool same = n == _blocked.Length && x0 == X0 && y0 == Y0 && w == Width && h == Height && ex == EarX && ey == EarY && ez == EarZ;
        if (!same) _blocked = new ushort[n];
        for (int z = 0; z < d; z++)
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = grid.Index(x0 + x, y0 + y, z);
            ushort b = Acoustics.Blocks(grid, i) ? (ushort)(grid.Material[i] + 1) : (ushort)0;
            int k = (z * h + y) * w + x;
            if (same && _blocked[k] != b) same = false;
            _blocked[k] = b;
        }
        Radius = radius;
        if (same) return;
        X0 = x0; Y0 = y0; Width = w; Height = h; Depth = d;
        EarX = ex; EarY = ey; EarZ = ez;
        Flood(grid);
    }

    public bool Contains(int x, int y, int z) =>
        x >= X0 && x < X0 + Width && y >= Y0 && y < Y0 + Height && z >= 0 && z < Depth;

    public int Local(int x, int y, int z) => (z * Height + (y - Y0)) * Width + (x - X0);

    private bool Open(int x, int y, int z) => Contains(x, y, z) && _blocked[Local(x, y, z)] == 0;

    private void Flood(WorldGrid grid)
    {
        int n = Width * Height * Depth;
        if (Distance.Length != n) Distance = new double[n];
        Array.Fill(Distance, double.PositiveInfinity);
        Bounded = true;
        SurfaceM2 = AbsorptionM2 = VolumeM3 = OpenM2 = 0.0;
        _queue.Clear();
        int start = Local(EarX, EarY, EarZ);
        Distance[start] = 0.0;
        _queue.Enqueue(start, 0.0);
        while (_queue.TryDequeue(out int k, out double dist))
        {
            if (dist > Distance[k]) continue;
            int lx = k % Width, rest = k / Width, ly = rest % Height, z = rest / Height;
            int x = lx + X0, y = ly + Y0;
            VolumeM3 += WorldGrid.TileVolumeM3;
            Surfaces(grid, x, y, z);
            foreach (var (dx, dy, dz, step) in Steps)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (!Open(nx, ny, nz)) continue;
                // A diagonal step needs each straight step it is made of to be open too.
                if ((dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0) > 1
                    && ((dx != 0 && !Open(x + dx, y, z)) || (dy != 0 && !Open(x, y + dy, z)) || (dz != 0 && !Open(x, y, z + dz))))
                    continue;
                int j = Local(nx, ny, nz);
                double next = dist + step;
                if (next < Distance[j])
                {
                    Distance[j] = next;
                    _queue.Enqueue(j, next);
                }
            }
        }
    }

    /// <summary>Adds the faces of reached tile (x, y, z) that meet a wall, the sky or the world's open edge.</summary>
    private void Surfaces(WorldGrid grid, int x, int y, int z)
    {
        for (int f = 0; f < 6; f++)
        {
            int nx = x + (f == 0 ? -1 : f == 1 ? 1 : 0), ny = y + (f == 2 ? -1 : f == 3 ? 1 : 0), nz = z + (f == 4 ? -1 : f == 5 ? 1 : 0);
            if (!grid.InBounds(nx, ny, nz))
            {
                // The sky above and the country beyond the edges take all the sound; bedrock below reflects.
                SurfaceM2 += WorldGrid.TileFaceAreaM2;
                if (nz < 0) { AbsorptionM2 += 0.02 * WorldGrid.TileFaceAreaM2; continue; }
                AbsorptionM2 += WorldGrid.TileFaceAreaM2;
                OpenM2 += WorldGrid.TileFaceAreaM2;
                continue;
            }
            if (!Contains(nx, ny, nz)) { Bounded = false; continue; }
            if (_blocked[Local(nx, ny, nz)] == 0) continue;
            int i = grid.Index(nx, ny, nz);
            SurfaceM2 += WorldGrid.TileFaceAreaM2;
            AbsorptionM2 += grid.Materials[grid.Material[i]].SoundAbsorption * WorldGrid.TileFaceAreaM2;
        }
    }

    /// <summary>
    /// Shortest way through the air from the ear to tile (x, y, z), tiles; for a tile shut to the
    /// air (rubble on the floor, a cracking wall) the way to the air beside it. Infinite if none.
    /// </summary>
    public double DistanceTo(int x, int y, int z)
    {
        if (!Contains(x, y, z)) return double.PositiveInfinity;
        double best = Distance[Local(x, y, z)];
        if (_blocked[Local(x, y, z)] == 0) return best;
        foreach (var (dx, dy, dz, step) in Steps)
        {
            int nx = x + dx, ny = y + dy, nz = z + dz;
            if (Open(nx, ny, nz)) best = Math.Min(best, Distance[Local(nx, ny, nz)] + step);
        }
        return best;
    }

    /// <summary>The air's absorption in band b over the room's volume, m² (4mV, m its power attenuation per metre).</summary>
    private double AirAbsorptionM2(int band) =>
        4.0 * Acoustics.AirAbsorptionDbPerM[band] / (10.0 * Math.Log10(Math.E)) * VolumeM3;

    /// <summary>
    /// The room constant in band b, m²: the absorption of the room's surfaces and air over the share
    /// of sound they reflect (Sabine). Infinite when the air is open to the world.
    /// </summary>
    public double RoomConstant(int band)
    {
        if (!Bounded || !(SurfaceM2 > 0.0)) return double.PositiveInfinity;
        double absorption = AbsorptionM2 + AirAbsorptionM2(band);
        double mean = Math.Min(absorption / SurfaceM2, 0.999);
        return absorption / (1.0 - mean);
    }

    /// <summary>
    /// How much of a diffuse echo the space holds, 0 to 1: all of it in a closed room, none once a
    /// quarter of its surface is open to the sky or the world beyond, where sound is gone after a
    /// reflection or two and never builds up (the one off the ground is part of the direct sound).
    /// </summary>
    public double Enclosure => Bounded && SurfaceM2 > 0.0 ? Math.Max(0.0, 1.0 - OpenM2 / SurfaceM2 / 0.25) : 0.0;

    /// <summary>Sabine's reverberation time in band b, s: how long the room's echo takes to die by 60 dB.</summary>
    public double ReverberationSeconds(int band)
    {
        if (!Bounded) return 0.0;
        double absorption = AbsorptionM2 + AirAbsorptionM2(band);
        return absorption > 0.0 ? 0.161 * VolumeM3 / absorption : 0.0;
    }
}
