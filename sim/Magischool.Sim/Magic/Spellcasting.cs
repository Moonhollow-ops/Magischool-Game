using System;
using Magischool.Sim.Core;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Magic;

/// <summary>
/// Layer 8: spells as inputs to the physics that already exists.
///
/// <para>
/// A spell is never a scripted effect. A caster gathers ether from the field
/// around them (<see cref="Gather"/>) and the spell turns exactly those joules
/// into something the other layers already understand: heat in a tile, a
/// projectile carrying heat to where it strikes, or charge moved to another
/// place. A freeze runs the other way, pulling heat out of a tile and handing
/// it back to the field around the caster. Whatever follows (wood catching,
/// water freezing and bursting its pipe, a crystal rupturing) is the physics
/// of those layers, with no special code. Every joule is counted: what a
/// spell puts into the world it took from the ether, and the reverse.
/// </para>
/// <para>
/// A caster draws on every tile within <see cref="GatherRadiusM"/>, each in
/// proportion to the ether it holds, and never takes more than
/// <see cref="MaxDrawShare"/> of what is there in one cast: in a poor place a
/// spell comes out weaker than asked. An ordinary place holds a hundred
/// megajoules within reach, so everyday spells leave it effectively untouched,
/// and the field flows back in within seconds.
/// </para>
/// <para>
/// Not yet: force and conjured matter, casters with bodies (spells are cast
/// from a point), and rituals that draw from further away.
/// </para>
/// </summary>
public static class Spellcasting
{
    /// <summary>How far a caster reaches into the field around them, m.</summary>
    public const double GatherRadiusM = 3.0;

    /// <summary>Most of the ether within reach one cast can take.</summary>
    public const double MaxDrawShare = 0.5;

    /// <summary>Most of a tile's heat above absolute zero one freeze can pull out.</summary>
    public const double MaxFreezeShare = 0.5;

    private static int RadiusTiles => (int)(GatherRadiusM / WorldGrid.TileSizeM);

    /// <summary>
    /// Takes up to <paramref name="joules"/> of ether from the field around a caster and returns
    /// how much it got: less than asked when there isn't enough within reach.
    /// </summary>
    public static double Gather(Simulation sim, int x, int y, int z, double joules)
    {
        var grid = sim.Grid;
        if (!(joules > 0.0) || !grid.InBounds(x, y, z)) return 0.0;
        double reachable = 0.0;
        ForEachInReach(grid, x, y, z, i => reachable += grid.Ether[i]);
        double take = Math.Min(joules, reachable * MaxDrawShare);
        if (!(take > 0.0)) return 0.0;

        double share = take / reachable, taken = 0.0;
        ForEachInReach(grid, x, y, z, i =>
        {
            double d = grid.Ether[i] * share;
            grid.Ether[i] -= d;
            taken += d;
        });
        WakeReach(sim, x, y, z);
        return taken;
    }

    /// <summary>Hands joules back to the field around a caster, spread by how much each tile can hold.</summary>
    public static void Release(Simulation sim, int x, int y, int z, double joules)
    {
        var grid = sim.Grid;
        if (!(joules > 0.0)) return;
        double capacity = 0.0;
        ForEachInReach(grid, x, y, z, i => capacity += grid.EtherCapacity(i));
        double given = 0.0;
        ForEachInReach(grid, x, y, z, i =>
        {
            double d = joules * grid.EtherCapacity(i) / capacity;
            grid.Ether[i] += d;
            given += d;
        });
        // Whatever rounding left over goes to the caster's own tile, so not a joule goes astray.
        grid.Ether[grid.Index(x, y, z)] += joules - given;
        WakeReach(sim, x, y, z);
    }

    /// <summary>
    /// Puts heat into a tile (or, if negative, takes it out). A tile's solid or liquid takes it
    /// first; the gas around it warms or cools from it, as with <see cref="AddHeat"/>.
    /// </summary>
    public static void Heat(WorldGrid grid, int i, double joules) => EnergyAudit.AddJoules(grid, i, joules, 0.0);

    /// <summary>Heat a freeze can pull from a tile in one cast: a share of its heat above absolute zero.</summary>
    public static double FreezableJoules(WorldGrid grid, int i)
    {
        double heat = grid.HasTwoTemperatures(i)
            ? grid.CondensedHeatCapacity(i) * grid.Temperature[i]
            : grid.HeatCapacity(i) * grid.Temperature[i];
        return MaxFreezeShare * Math.Max(0.0, heat);
    }

    private static void ForEachInReach(WorldGrid grid, int x, int y, int z, Action<int> visit)
    {
        int r = RadiusTiles;
        for (int dz = -r; dz <= r; dz++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx * dx + dy * dy + dz * dz > r * r) continue;
            int nx = x + dx, ny = y + dy, nz = z + dz;
            if (grid.InBounds(nx, ny, nz)) visit(grid.Index(nx, ny, nz));
        }
    }

    private static void WakeReach(Simulation sim, int x, int y, int z)
    {
        int r = RadiusTiles;
        for (int dz = -r; dz <= r; dz += r)
        for (int dy = -r; dy <= r; dy += r)
        for (int dx = -r; dx <= r; dx += r)
        {
            int nx = Math.Clamp(x + dx, 0, sim.Grid.Width - 1);
            int ny = Math.Clamp(y + dy, 0, sim.Grid.Height - 1);
            int nz = Math.Clamp(z + dz, 0, sim.Grid.Depth - 1);
            sim.Activity.WakeAround(nx, ny, nz);
        }
    }
}
