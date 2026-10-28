using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Magischool.Sim.Combustion;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Perception;

public enum LightKind : byte
{
    /// <summary>Soot glowing in a flame.</summary>
    Flame,
    /// <summary>The surface of something hot enough to glow.</summary>
    Glow,
    /// <summary>A spell's bolt in flight.</summary>
    Bolt,
}

/// <summary>
/// One source of light: where it is (in tiles), how bright it shines, and how it looks to an
/// eye that looks straight at it. A face of hot matter shines as a Lambertian surface, brightest
/// straight out from its face; a flame or a bolt shines the same every way.
/// </summary>
/// <param name="Candela">Luminous intensity, cd: straight out from a face, or every way for the rest.</param>
/// <param name="Face">Face it shines from (-x, +x, -y, +y, -z, +z), or -1 for every way.</param>
/// <param name="Luminance">Luminance of the source itself, cd/m²: how bright it looks.</param>
/// <param name="AreaM2">The area it shows an eye, m².</param>
/// <param name="Tile">The tile it shines out of.</param>
/// <param name="Subject">What is shining: the hot tile, the flame's tile, or the bolt's id.</param>
public readonly record struct LightSource(
    LightKind Kind, double X, double Y, double Z, double Candela, int Face, double Luminance, double AreaM2, int Tile, long Subject);

/// <summary>
/// How light moves through the world (layer 10). Light here is a measurement, not a carrier of
/// energy: the visible share of a fire's heat is well under a thousandth, and radiant heat between
/// distant surfaces is still the thermal layer's to add. Light leaves its sources in straight
/// lines, falls off with the square of distance, and is dimmed by everything it passes through:
/// an opaque solid by the share of the tile it fills (a wall stops it, a post shades part of it),
/// water, ice and crystal by Beer–Lambert at their own extinction, and smoke by its soot (8.7 m²
/// per gram, so a few grams in a room put it in darkness). The open sky lights every place that
/// can see it, by how much of the sky it sees, weighted as a floor sees it.
/// </summary>
public static class LightField
{
    /// <summary>Light from the sky on open ground under an overcast day, a full moon, and stars alone, lux.</summary>
    public const double OvercastDayLux = 10_000.0, FullMoonLux = 0.25, StarlightLux = 0.001;

    /// <summary>How much of its heat a flaming fire radiates (about 30% for wood and other sooty fires).</summary>
    public const double FlameRadiantFraction = 0.3;

    /// <summary>Temperature of the glowing soot in a wood flame, K; the yellow of a campfire.</summary>
    public const double FlameSootK = 1400.0;

    /// <summary>Emissivity of a flame's luminous zone: soot is thin enough to see through.</summary>
    public const double FlameEmissivity = 0.3;

    /// <summary>Emissivity of hot surfaces (as the combustion layer takes for fuel).</summary>
    public const double SurfaceEmissivity = CombustionSystem.SurfaceEmissivity;

    /// <summary>A fireball in flight shines as a ball of flame this wide, m, at <see cref="BoltK"/>.</summary>
    public const double BoltRadiusM = 0.15;
    public const double BoltK = 1500.0;

    /// <summary>Directions toward the sky each place looks along, for skylight.</summary>
    public const int SkyRays = 32;

    /// <summary>Closest a source counts as being, m: light from inside the same half-metre tile.</summary>
    private const double NearestM = 0.25;

    private sealed class Optics
    {
        public readonly bool[] Opaque;
        public readonly double[] Extinction;
        public readonly int[] SootSpecies;
        public readonly double[] SootExtinction;

        public Optics(MaterialTable table)
        {
            Opaque = new bool[table.Count];
            Extinction = new double[table.Count];
            var species = new List<int>();
            var soot = new List<double>();
            for (ushort id = 0; id < table.Count; id++)
            {
                var def = table[id];
                if (def.Phase == Phase.Gas)
                {
                    if (def.MassExtinction > 0f) { species.Add(table.GasIndexOf(id)); soot.Add(def.MassExtinction); }
                    continue;
                }
                Opaque[id] = float.IsPositiveInfinity(def.LightExtinction);
                Extinction[id] = Opaque[id] ? 0.0 : def.LightExtinction;
            }
            SootSpecies = species.ToArray();
            SootExtinction = soot.ToArray();
        }
    }

    private static readonly ConditionalWeakTable<MaterialTable, Optics> Cache = new();
    private static Optics For(MaterialTable table) => Cache.GetValue(table, t => new Optics(t));

    /// <summary>Whether light can't get through a tile at all: it is full of something opaque.</summary>
    public static bool IsOpaque(WorldGrid grid, int i) =>
        grid.HasCondensed(i) && For(grid.Materials).Opaque[grid.Material[i]] && grid.FillFraction(i) >= 0.999;

    /// <summary>Share of light that gets through tile i along a path of <paramref name="tiles"/> inside it.</summary>
    public static double TileTransmittance(WorldGrid grid, int i, double tiles)
    {
        var o = For(grid.Materials);
        double t = 1.0, metres = tiles * WorldGrid.TileSizeM;
        double free = 1.0;
        if (grid.HasCondensed(i))
        {
            double fill = Math.Min(1.0, grid.FillFraction(i));
            ushort m = grid.Material[i];
            if (o.Opaque[m]) t = fill >= 0.999 ? 0.0 : 1.0 - Math.Pow(fill, 2.0 / 3.0);
            else t = Math.Exp(-o.Extinction[m] * metres * fill);
            free = 1.0 - fill;
            if (t <= 0.0) return 0.0;
        }
        if (o.SootSpecies.Length > 0 && free > 0.0)
        {
            double volume = Math.Max(grid.FreeVolume(i), WorldGrid.MinFreeVolumeM3);
            double k = 0.0;
            for (int s = 0; s < o.SootSpecies.Length; s++)
                k += o.SootExtinction[s] * grid.GasMass[o.SootSpecies[s]][i] / volume;
            if (k > 0.0) t *= Math.Exp(-k * metres * free);
        }
        return t;
    }

    /// <summary>
    /// Share of light that gets from point a to point b (in tiles), through every tile between.
    /// The tile a starts in counts only if <paramref name="countFirst"/>, the tile b is in only if
    /// <paramref name="countLast"/>: a source shines out of its own tile, and light lands on the
    /// surface of whatever is in the last. Off the grid is clear air.
    /// </summary>
    public static double Transmittance(WorldGrid grid, double ax, double ay, double az, double bx, double by, double bz,
        bool countFirst = false, bool countLast = false)
    {
        var walk = new RayWalk(ax, ay, az, bx, by, bz);
        double t = 1.0;
        bool first = true;
        while (walk.Next(out int x, out int y, out int z, out double inside, out bool last))
        {
            bool skip = (first && !countFirst) || (last && !countLast);
            first = false;
            if (skip || inside <= 1e-9 || !grid.InBounds(x, y, z)) continue;
            t *= TileTransmittance(grid, grid.Index(x, y, z), inside);
            if (t < 1e-9) return 0.0;
        }
        return t;
    }

    // ---------------------------------------------------------------- sources

    private static readonly (int Dx, int Dy, int Dz)[] Faces =
        { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) };

    /// <summary>The light sources in one box of tiles, in tile order, added to <paramref name="into"/>.</summary>
    public static void Gather(WorldGrid grid, int x0, int y0, int z0, int x1, int y1, int z1, List<LightSource> into)
    {
        double flameLuminance = FlameEmissivity * Photometry.LuminousExitance(FlameSootK) / Math.PI;
        double flameLumensPerW = FlameRadiantFraction * Photometry.Efficacy(FlameSootK);
        for (int z = z0; z < z1; z++)
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int i = grid.Index(x, y, z);
            byte flame = grid.Flame[i];
            if (flame != 0)
            {
                for (int site = 0; site <= 6; site++)
                {
                    if ((flame & (1 << site)) == 0) continue;
                    double watts = CombustionSystem.FlamePowerW(grid, i, site);
                    if (!(watts > 0.0)) continue;
                    double cd = flameLumensPerW * watts / (4.0 * Math.PI);
                    // The flame lives in the gas beside the face it burns on.
                    double px = x + 0.5, py = y + 0.5, pz = z + 0.5;
                    int at = i;
                    if (site > 0)
                    {
                        var (dx, dy, dz) = Faces[site - 1];
                        px += 0.75 * dx; py += 0.75 * dy; pz += 0.75 * dz;
                        at = grid.Index(x + dx, y + dy, z + dz);
                    }
                    into.Add(new LightSource(LightKind.Flame, px, py, pz, cd, -1, flameLuminance, cd / flameLuminance, at, at));
                }
            }
            if (!grid.HasCondensed(i) || !(grid.Temperature[i] > Photometry.GlowFloorK)) continue;
            double exposed = Math.Pow(Math.Min(1.0, grid.FillFraction(i)), 2.0 / 3.0);
            for (int face = 0; face < 6; face++)
            {
                var (dx, dy, dz) = Faces[face];
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (!grid.InBounds(nx, ny, nz) || IsOpaque(grid, grid.Index(nx, ny, nz))) continue;
                double skin = grid.Skin[i * 6 + face];
                double kelvin = skin > 0.0 ? skin : grid.Temperature[i];
                double exitance = SurfaceEmissivity * Photometry.LuminousExitance(kelvin);
                if (!(exitance > 0.0)) continue;
                double area = WorldGrid.TileFaceAreaM2 * exposed;
                // Just out from the face, in the tile it shines into.
                into.Add(new LightSource(LightKind.Glow, x + 0.5 + 0.501 * dx, y + 0.5 + 0.501 * dy, z + 0.5 + 0.501 * dz,
                    exitance * area / Math.PI, face, exitance / Math.PI, area, grid.Index(nx, ny, nz), i));
            }
        }
    }

    /// <summary>The bolts in flight as light sources.</summary>
    public static void GatherBolts(Simulation sim, List<LightSource> into)
    {
        double exitance = Photometry.LuminousExitance(BoltK);
        double area = Math.PI * BoltRadiusM * BoltRadiusM;
        foreach (var bolt in sim.Projectiles)
        {
            int ix = (int)Math.Floor(bolt.X), iy = (int)Math.Floor(bolt.Y), iz = (int)Math.Floor(bolt.Z);
            int tile = sim.Grid.InBounds(ix, iy, iz) ? sim.Grid.Index(ix, iy, iz) : -1;
            // A sphere's intensity is its exitance over π times the disc it shows.
            into.Add(new LightSource(LightKind.Bolt, bolt.X, bolt.Y, bolt.Z, exitance / Math.PI * area, -1, exitance / Math.PI, area, tile, bolt.Id));
        }
    }

    /// <summary>Every light source in the world: flames, glowing matter and bolts.</summary>
    public static List<LightSource> AllSources(Simulation sim)
    {
        var list = new List<LightSource>();
        var grid = sim.Grid;
        Gather(grid, 0, 0, 0, grid.Width, grid.Height, grid.Depth, list);
        GatherBolts(sim, list);
        return list;
    }

    /// <summary>Luminous intensity of a source toward point (x, y, z), cd, and the distance to it, m.</summary>
    public static double IntensityToward(in LightSource s, double x, double y, double z, out double metres)
    {
        double vx = x - s.X, vy = y - s.Y, vz = z - s.Z;
        double tiles = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        metres = Math.Max(NearestM, tiles * WorldGrid.TileSizeM);
        if (s.Face < 0) return s.Candela;
        if (tiles <= 0.0) return 0.0;
        var (dx, dy, dz) = Faces[s.Face];
        double cos = (vx * dx + vy * dy + vz * dz) / tiles;
        return cos > 0.0 ? s.Candela * cos : 0.0;
    }

    /// <summary>
    /// Illuminance at point (x, y, z) in tiles, lux: what the sources and the sky put on a surface
    /// there. Whatever fills the tile the point is in doesn't shade it.
    /// </summary>
    public static double Illuminance(Simulation sim, IReadOnlyList<LightSource> sources, double x, double y, double z)
    {
        var grid = sim.Grid;
        double lux = 0.0;
        for (int k = 0; k < sources.Count; k++)
        {
            var s = sources[k];
            double cd = IntensityToward(s, x, y, z, out double metres);
            double unshaded = cd / (metres * metres);
            if (!(unshaded > 1e-7)) continue;
            lux += unshaded * Transmittance(grid, s.X, s.Y, s.Z, x, y, z);
        }
        return lux + Skylight(sim, x, y, z);
    }

    private static readonly (double X, double Y, double Z)[] SkyDirections = BuildSky();

    /// <summary>Directions over the upper hemisphere, spread evenly by the cosine weight a floor sees the sky with.</summary>
    private static (double, double, double)[] BuildSky()
    {
        var dirs = new (double, double, double)[SkyRays];
        for (int k = 0; k < SkyRays; k++)
        {
            // Hammersley points: k/n against the base-2 radical inverse of k.
            double u = (k + 0.5) / SkyRays;
            uint bits = (uint)k;
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            double v = bits * 2.3283064365386963e-10;
            double r = Math.Sqrt(u), phi = 2.0 * Math.PI * v;
            dirs[k] = (r * Math.Cos(phi), r * Math.Sin(phi), Math.Sqrt(1.0 - u));
        }
        return dirs;
    }

    /// <summary>
    /// Light from the open sky at point (x, y, z), lux: the sky's illuminance on open ground times
    /// how much of the sky shows through, the way a floor sees it. The world's sides open onto
    /// the same sky; its top is the sky.
    /// </summary>
    public static double Skylight(Simulation sim, double x, double y, double z)
    {
        double sky = sim.SkyIlluminanceLux;
        if (!(sky > 0.0)) return 0.0;
        var grid = sim.Grid;
        double seen = 0.0;
        foreach (var (dx, dy, dz) in SkyDirections)
        {
            // Out to where the ray leaves the grid.
            double reach = double.PositiveInfinity;
            reach = Math.Min(reach, (grid.Depth - z) / dz);
            if (dx > 0) reach = Math.Min(reach, (grid.Width - x) / dx); else if (dx < 0) reach = Math.Min(reach, -x / dx);
            if (dy > 0) reach = Math.Min(reach, (grid.Height - y) / dy); else if (dy < 0) reach = Math.Min(reach, -y / dy);
            reach += 1e-6;
            seen += Transmittance(grid, x, y, z, x + dx * reach, y + dy * reach, z + dz * reach, countLast: true);
        }
        return sky * seen / SkyRays;
    }
}
