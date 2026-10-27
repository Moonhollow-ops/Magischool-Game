using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Thermal;

/// <summary>
/// Layer 3: melting, freezing, boiling and condensing with latent heat.
///
/// <para>
/// Melting and freezing: a tile that reaches its melting point stops changing
/// temperature. Extra energy goes into <see cref="WorldGrid.LatentEnergy"/>, and
/// only when the full latent heat (J/kg × mass) has been absorbed does the whole
/// condensed mass change. A half-melted tile that starts cooling gives its
/// stored energy back first.
/// </para>
/// <para>
/// Boiling and condensing happen a little at a time instead: each tick, as much
/// liquid turns to gas (or gas to liquid) as the heat above (or below) the
/// boiling point pays for, so a pot boils away gradually and steam beads on a
/// cold ceiling. The gas joins the tile's gas mixture.
/// </para>
/// <para>
/// Mass is kept through every change, so a tile of ice becomes the same mass
/// of water (a tile 92% full), and a tile of water freezes into ice that
/// needs 109% of a tile.
/// </para>
/// Boiling points don't yet depend on pressure, and there is no evaporation
/// below the boiling point (puddles don't dry); both need vapour pressure.
///
/// Runs after <see cref="ThermalSystem"/> each tick, on awake chunks only, in
/// parallel (each tile is independent). Energy is conserved exactly;
/// <see cref="EnergyAudit"/> counts it.
/// </summary>
public sealed class PhaseSystem : ISimSystem
{
    // A tile that receives enormous energy in one tick can pass through several transitions (ice to steam).
    private const int MaxTransitionsPerTick = 4;

    public string Name => "phase";

    public void Update(Simulation sim)
    {
        var grid = sim.Grid;
        var rules = PhaseRules.For(sim.Materials);
        var activity = sim.Activity;
        var chunks = activity.AwakeChunks();

        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                ProcessTile(grid, rules, grid.Index(x, y, z));
        });
    }

    private static void ProcessTile(WorldGrid grid, PhaseRules rules, int i)
    {
        for (int pass = 0; pass < MaxTransitionsPerTick; pass++)
        {
            if (grid.HeatCapacity(i) <= 0.0) break;
            bool changed = Fusion(grid, rules, i);
            changed |= Boiling(grid, rules, i);
            changed |= Condensing(grid, rules, i);
            if (!changed) break;
        }

        // Nothing is colder than absolute zero. Only an outside command can push a tile here;
        // flooring it means that command took out less energy than it asked for.
        if (grid.Temperature[i] < 0.0) grid.Temperature[i] = 0.0;
    }

    /// <summary>
    /// Melting and freezing of the condensed slot, all at once: the temperature is pinned at
    /// the melting point while latent energy builds up, then the whole mass changes.
    /// Returns true when the material changed.
    /// </summary>
    private static bool Fusion(WorldGrid grid, PhaseRules rules, int i)
    {
        ushort m = grid.Material[i];
        if (m == MaterialTable.Empty) return false;
        var up = rules.Melt[m];
        var down = rules.Freeze[m];
        double t = grid.Temperature[i];
        double s = grid.LatentEnergy[i];

        bool heating = s > 0.0 || (s == 0.0 && up.Exists && t > up.TemperatureK);
        bool cooling = !heating && (s < 0.0 || (s == 0.0 && down.Exists && t < down.TemperatureK));
        if (!heating && !cooling) return false;

        double capacity = grid.HeatCapacity(i);
        var transition = heating ? up : down;
        double energy = s + (t - transition.TemperatureK) * capacity; // signed: + toward melting, - toward freezing
        double needed = transition.LatentPerKg * grid.Mass[i];

        if (heating ? energy <= 0.0 : energy >= 0.0)
        {
            // Turned back before finishing: return the stored energy as temperature.
            grid.LatentEnergy[i] = 0.0;
            grid.Temperature[i] = transition.TemperatureK + energy / capacity;
            return false;
        }

        if (Math.Abs(energy) < needed)
        {
            grid.LatentEnergy[i] = energy;
            grid.Temperature[i] = transition.TemperatureK;
            return false;
        }

        double leftover = heating ? energy - needed : energy + needed;
        grid.Material[i] = transition.Into;
        grid.LatentEnergy[i] = 0.0;
        grid.Temperature[i] = transition.TemperatureK + leftover / grid.HeatCapacity(i);
        return true;
    }

    /// <summary>
    /// A liquid above its boiling point turns into its gas, a little at a time: exactly
    /// as much as the excess heat can pay for, which pins the tile at the boiling point.
    /// The gas joins the tile's mixture, and the fluids layer carries it away.
    /// </summary>
    private static bool Boiling(WorldGrid grid, PhaseRules rules, int i)
    {
        ushort m = grid.Material[i];
        if (m == MaterialTable.Empty) return false;
        var boil = rules.Boil[m];
        double t = grid.Temperature[i];
        if (!boil.Exists || t <= boil.TemperatureK) return false;

        double energy = (t - boil.TemperatureK) * grid.HeatCapacity(i);
        double mass = grid.Mass[i];
        double dm = Math.Min(mass, energy / boil.LatentPerKg);
        if (dm <= 0.0) return false;

        int species = grid.Materials.GasIndexOf(boil.Into);
        grid.GasMass[species][i] += dm;
        if (dm >= mass) grid.SetCondensed(i, MaterialTable.Empty, 0.0);
        else grid.Mass[i] = mass - dm;
        grid.Temperature[i] = boil.TemperatureK + (energy - dm * boil.LatentPerKg) / grid.HeatCapacity(i);
        return true;
    }

    /// <summary>
    /// A gas below its condensation point turns back into liquid, as much as its released
    /// latent heat allows, into the tile's condensed slot (if that is empty or already
    /// the same liquid). This is how steam beads on a cold ceiling.
    /// </summary>
    private static bool Condensing(WorldGrid grid, PhaseRules rules, int i)
    {
        bool changed = false;
        for (int s = 0; s < rules.Condense.Length; s++)
        {
            var c = rules.Condense[s];
            double gas = grid.GasMass[s][i];
            double t = grid.Temperature[i];
            ushort m = grid.Material[i];
            if (!c.Exists || gas <= 0.0 || t >= c.TemperatureK) continue;
            if (m != MaterialTable.Empty && m != c.Into) continue;

            double deficit = (c.TemperatureK - t) * grid.HeatCapacity(i);
            double dm = Math.Min(gas, deficit / c.LatentPerKg);
            if (dm <= 0.0) continue;

            grid.GasMass[s][i] = dm >= gas ? 0.0 : gas - dm;
            if (m == MaterialTable.Empty) grid.SetCondensed(i, c.Into, dm);
            else grid.Mass[i] += dm;
            grid.Temperature[i] = c.TemperatureK - (deficit - dm * c.LatentPerKg) / grid.HeatCapacity(i);
            changed = true;
        }
        return changed;
    }
}
