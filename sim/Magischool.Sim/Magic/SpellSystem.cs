using System;
using Magischool.Sim.Bodies;
using Magischool.Sim.Combustion;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Magic;

/// <summary>
/// Flies the spells in flight (<see cref="Simulation.Projectiles"/>). A bolt moves in a straight
/// line, checking every quarter tile, and strikes the first tile with a solid or liquid in it.
/// Half its energy goes into the surface it hits (for a fuel, the thin surface layer of the face
/// it struck, so wood catches as real wood does under a flash of heat) and half into the gas just
/// in front, the fireball's own flame, which the air currents then carry upward. A bolt that
/// leaves the world or flies too long gives all its heat to the gas where it is (or, in a vacuum,
/// back to the ether there).
/// </summary>
public sealed class SpellSystem : ISimSystem
{
    /// <summary>A bolt gutters out after this long in the air, s.</summary>
    public const double MaxFlightSeconds = 10.0;

    /// <summary>Share of a bolt's energy that goes into the surface it strikes; the rest heats the gas before it.</summary>
    public const double SurfaceShare = 0.5;

    /// <summary>Anything filling at least this much of a tile stops a bolt.</summary>
    public const double StopFill = 0.05;

    private const double StepTiles = 0.25;

    public string Name => "spells";

    public void Update(Simulation sim)
    {
        var bolts = sim.Projectiles;
        for (int k = 0; k < bolts.Count; k++)
        {
            var bolt = bolts[k];
            if (Fly(sim, ref bolt)) { bolts.RemoveAt(k); k--; }
            else bolts[k] = bolt;
        }
    }

    /// <summary>Moves a bolt one tick. Returns true when it has struck or guttered out and is spent.</summary>
    private static bool Fly(Simulation sim, ref Projectile bolt)
    {
        var grid = sim.Grid;
        const double dt = Simulation.SecondsPerTick;
        double speed = Math.Sqrt(bolt.VX * bolt.VX + bolt.VY * bolt.VY + bolt.VZ * bolt.VZ);
        double tiles = speed * dt / WorldGrid.TileSizeM;
        int steps = Math.Max(1, (int)Math.Ceiling(tiles / StepTiles));
        double sx = bolt.VX * dt / WorldGrid.TileSizeM / steps;
        double sy = bolt.VY * dt / WorldGrid.TileSizeM / steps;
        double sz = bolt.VZ * dt / WorldGrid.TileSizeM / steps;

        int here = TileAt(grid, bolt.X, bolt.Y, bolt.Z);
        for (int s = 0; s < steps; s++)
        {
            double nx = bolt.X + sx, ny = bolt.Y + sy, nz = bolt.Z + sz;
            int next = TileAt(grid, nx, ny, nz);
            if (next < 0)
            {
                Gutter(sim, here, bolt.Joules);
                return true;
            }
            if (next != here && BodySystem.TryPartAt(sim, next, out var body, out int part))
            {
                StrikeBody(sim, body, part, here, bolt.Joules);
                return true;
            }
            if (next != here && Stops(grid, next))
            {
                Strike(sim, next, here, bolt.Joules);
                return true;
            }
            bolt.X = nx; bolt.Y = ny; bolt.Z = nz;
            here = next;
        }
        bolt.Age += dt;
        if (bolt.Age >= MaxFlightSeconds)
        {
            Gutter(sim, here, bolt.Joules);
            return true;
        }
        return false;
    }

    private static int TileAt(WorldGrid grid, double x, double y, double z)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), iz = (int)Math.Floor(z);
        return grid.InBounds(ix, iy, iz) ? grid.Index(ix, iy, iz) : -1;
    }

    private static bool Stops(WorldGrid grid, int i) => grid.HasCondensed(i) && grid.FillFraction(i) >= StopFill;

    /// <summary>The bolt hits tile <paramref name="hit"/>, coming from the gas of tile <paramref name="from"/>.</summary>
    private static void Strike(Simulation sim, int hit, int from, double joules)
    {
        var grid = sim.Grid;
        double surface = joules * SurfaceShare;
        double flame = joules - surface;

        var def = grid.Materials[grid.Material[hit]];
        if (def.HeatOfCombustion > 0f && def.Phase == Phase.Solid)
        {
            // The flash lands on the face it struck: that thin layer takes it all at first.
            int face = FaceToward(grid, hit, from);
            double layer = CombustionSystem.SurfaceLayerCapacity(grid, hit);
            if (face >= 0 && layer > 0.0)
            {
                double stored = grid.Skin[hit * 6 + face];
                double skin = stored > 0.0 ? stored : grid.Temperature[hit];
                grid.Skin[hit * 6 + face] = skin + surface / layer;
            }
        }
        Spellcasting.Heat(grid, hit, surface);
        HeatGas(grid, from, flame);
        Wake(sim, hit);
        Wake(sim, from);
    }

    /// <summary>The bolt hits a body: half its energy lands on the skin of the part it struck, half in the air before it.</summary>
    private static void StrikeBody(Simulation sim, Body body, int part, int from, double joules)
    {
        double surface = joules * SurfaceShare;
        BodySystem.HeatSkin(sim, body, part, surface);
        HeatGas(sim.Grid, from, joules - surface);
        Wake(sim, from);
    }

    private static void Gutter(Simulation sim, int here, double joules)
    {
        HeatGas(sim.Grid, here, joules);
        Wake(sim, here);
    }

    /// <summary>Heats the gas of a tile; in a vacuum, with nothing to heat, the energy goes back into the ether there.</summary>
    private static void HeatGas(WorldGrid grid, int i, double joules)
    {
        if (grid.HeatCapacity(i) > 0.0) EnergyAudit.AddJoules(grid, i, 0.0, joules);
        else grid.Ether[i] += joules;
    }

    private static void Wake(Simulation sim, int i)
    {
        var (x, y, z) = sim.Grid.Coordinates(i);
        sim.Activity.WakeAround(x, y, z);
    }

    /// <summary>Which face of tile <paramref name="i"/> (-x, +x, -y, +y, -z, +z) borders tile <paramref name="from"/>, or -1.</summary>
    private static int FaceToward(WorldGrid grid, int i, int from)
    {
        int d = from - i, plane = grid.Width * grid.Height;
        if (d == -1) return 0;
        if (d == 1) return 1;
        if (d == -grid.Width) return 2;
        if (d == grid.Width) return 3;
        if (d == -plane) return 4;
        if (d == plane) return 5;
        return -1;
    }
}
