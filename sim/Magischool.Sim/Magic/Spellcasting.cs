using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
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
/// All matter a spell makes is ether crystallised, at <see cref="EtherPerKg"/>,
/// and once made it is ordinary matter like any other. Etherium, ether
/// crystallised and left as itself, gives the same back: when the field
/// within reach can't supply a spell, the caster burns etherium within reach
/// to make up the rest.
/// </para>
/// <para>
/// Not yet: force, casters with bodies (spells are cast from a point), and
/// rituals that draw from further away.
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

    /// <summary>
    /// Ether that crystallises into one kilogram of matter, J/kg, and that a kilogram of etherium
    /// gives back when burned. Ten megajoules: a cast in an ordinary place can make a bucket of water.
    /// </summary>
    public const double EtherPerKg = 10_000_000.0;

    /// <summary>Etherium left in a tile below this, kg, is burned with the rest.</summary>
    private const double ResidueKg = 1e-9;

    private static int RadiusTiles => (int)(GatherRadiusM / WorldGrid.TileSizeM);

    /// <summary>
    /// Takes up to <paramref name="joules"/> of ether from the field around a caster, burning
    /// etherium within reach for whatever the field can't give, and returns how much it got:
    /// less than asked when there isn't enough within reach.
    /// </summary>
    public static double Gather(Simulation sim, int x, int y, int z, double joules)
    {
        var grid = sim.Grid;
        if (!(joules > 0.0) || !grid.InBounds(x, y, z)) return 0.0;
        double reachable = 0.0;
        ForEachInReach(grid, x, y, z, i => reachable += grid.Ether[i]);
        double take = Math.Min(joules, reachable * MaxDrawShare);

        double taken = 0.0;
        if (take > 0.0)
        {
            double share = take / reachable;
            ForEachInReach(grid, x, y, z, i =>
            {
                double d = grid.Ether[i] * share;
                grid.Ether[i] -= d;
                taken += d;
            });
        }
        if (taken < joules) taken += BurnEtherium(sim, x, y, z, joules - taken);
        if (taken > 0.0) WakeReach(sim, x, y, z);
        return taken;
    }

    /// <summary>
    /// Burns etherium within reach of a caster, nearest tiles first, for up to
    /// <paramref name="joules"/>: each kilogram gives <see cref="EtherPerKg"/> and the heat it held.
    /// </summary>
    private static double BurnEtherium(Simulation sim, int x, int y, int z, double joules)
    {
        var grid = sim.Grid;
        double got = 0.0;
        int r = RadiusTiles;
        for (int d2 = 0; d2 <= r * r && got < joules; d2++)
        {
            ForEachInReach(grid, x, y, z, i =>
            {
                if (got >= joules || !grid.HasCondensed(i)) return;
                if (grid.Materials[grid.Material[i]].EtherWellPotential <= 0f) return;

                double mass = grid.Mass[i];
                double before = EnergyAudit.TileJoules(grid, i), gasJoules = EnergyAudit.GasJoules(grid, i);
                double perKg = EtherPerKg + (EnergyAudit.CondensedJoules(grid, i) - grid.LatentEnergy[i]) / mass;
                double kg = Math.Min(mass, (joules - got) / perKg);
                if (mass - kg < ResidueKg) kg = mass;
                double latent = grid.LatentEnergy[i];
                if (kg >= mass) grid.SetCondensed(i, MaterialTable.Empty, 0.0);
                else
                {
                    grid.Mass[i] = mass - kg;
                    grid.LatentEnergy[i] = latent * (mass - kg) / mass;
                }
                // The burned share keeps nothing: its heat goes into the spell with its ether.
                double after = grid.HasCondensed(i) ? EnergyAudit.CondensedJoules(grid, i) : 0.0;
                EnergyAudit.SolveTemperatures(grid, i, after, gasJoules);
                got += kg * EtherPerKg + (before - EnergyAudit.TileJoules(grid, i));
                sim.EtherMatterKg -= kg;
            }, d2);
        }
        return got;
    }

    /// <summary>
    /// Crystallises up to <paramref name="kg"/> of a material out of ether gathered around a caster
    /// into tile <paramref name="target"/>, at the temperature of what is already there: each
    /// kilogram costs <see cref="EtherPerKg"/> plus the heat it holds at that temperature. A solid
    /// or liquid goes in the tile's condensed slot (only if that is empty or already the same
    /// material, and never past full); a gas joins the tile's gas. Returns the kilograms made.
    /// </summary>
    public static double Conjure(Simulation sim, int x, int y, int z, int target, ushort material, double kg)
    {
        var grid = sim.Grid;
        var table = grid.Materials;
        if (!(kg > 0.0) || material >= table.Count) return 0.0;
        var def = table[material];
        int species = table.GasIndexOf(material);
        bool gas = species >= 0;
        if (!gas)
        {
            if (grid.HasCondensed(target) && grid.Material[target] != material) return 0.0;
            double room = def.Density * grid.FreeVolume(target);
            kg = Math.Min(kg, room);
            if (!(kg > 0.0)) return 0.0;
        }
        double temperature = gas || !grid.HasCondensed(target) ? grid.GasTemperatureAt(target) : grid.Temperature[target];
        double perKg = EtherPerKg + def.SpecificHeat * temperature + EnergyAudit.OffsetsFor(table)[material];
        double joules = Gather(sim, x, y, z, kg * perKg);
        kg = joules / perKg;
        if (!(kg > 0.0)) return 0.0;

        // The new matter brings its heat with it; everything else in the tile keeps what it had.
        double condensed = EnergyAudit.CondensedJoules(grid, target), gasJoules = EnergyAudit.GasJoules(grid, target);
        double heat = joules - kg * EtherPerKg;
        if (gas)
        {
            grid.GasMass[species][target] += kg;
            gasJoules += heat;
        }
        else
        {
            if (!grid.HasCondensed(target))
            {
                grid.SetCondensed(target, material, kg);
                grid.Temperature[target] = temperature;
                grid.ResetBonds(target);
            }
            else grid.Mass[target] += kg;
            condensed += heat;
        }
        EnergyAudit.SolveTemperatures(grid, target, condensed, gasJoules);
        sim.EtherMatterKg += kg;
        var (tx, ty, tz) = grid.Coordinates(target);
        sim.Activity.WakeAround(tx, ty, tz);
        return kg;
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

    private static void ForEachInReach(WorldGrid grid, int x, int y, int z, Action<int> visit, int onlyDistance2 = -1)
    {
        int r = RadiusTiles;
        for (int dz = -r; dz <= r; dz++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            int d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > r * r || (onlyDistance2 >= 0 && d2 != onlyDistance2)) continue;
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
