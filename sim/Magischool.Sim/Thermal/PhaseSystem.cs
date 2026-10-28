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

        // A bead of liquid too small to hold its temperature through a tick can be cooled by the
        // gas around it past its freezing point by more than it holds above absolute zero as a
        // solid. The gas took that heat this very tick, so it gives it back: the two share one
        // temperature rather than the bead going below zero.
        if (grid.Temperature[i] < 0.0 && grid.HasTwoTemperatures(i)) EnergyAudit.Equalize(grid, i);

        // Nothing is colder than absolute zero. Otherwise only an outside command can push a tile
        // here; flooring it means that command took out less energy than it asked for.
        if (grid.Temperature[i] < 0.0) grid.Temperature[i] = 0.0;
        if (grid.GasTemperature[i] < 0.0) grid.GasTemperature[i] = 0.0;
    }

    /// <summary>Heat capacity of whatever shares the temperature of a tile's solid or liquid, J/K.</summary>
    private static double CondensedCapacity(WorldGrid grid, int i) =>
        grid.HasTwoTemperatures(i) ? grid.CondensedHeatCapacity(i) : grid.HeatCapacity(i);

    /// <summary>Heat capacity of whatever shares the temperature of a tile's gas, J/K.</summary>
    private static double GasCapacity(WorldGrid grid, int i) =>
        grid.HasTwoTemperatures(i) ? grid.GasHeatCapacity(i) : grid.HeatCapacity(i);

    private static void SetCondensedTemperature(WorldGrid grid, int i, double t)
    {
        if (grid.HasTwoTemperatures(i)) grid.Temperature[i] = t;
        else grid.SetTemperature(i, t);
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

        double capacity = CondensedCapacity(grid, i);
        var transition = heating ? up : down;
        double energy = s + (t - transition.TemperatureK) * capacity; // signed: + toward melting, - toward freezing
        double needed = transition.LatentPerKg * grid.Mass[i];

        if (heating ? energy <= 0.0 : energy >= 0.0)
        {
            // Turned back before finishing: return the stored energy as temperature.
            grid.LatentEnergy[i] = 0.0;
            SetCondensedTemperature(grid, i, transition.TemperatureK + energy / capacity);
            return false;
        }

        if (Math.Abs(energy) < needed)
        {
            grid.LatentEnergy[i] = energy;
            SetCondensedTemperature(grid, i, transition.TemperatureK);
            return false;
        }

        double leftover = heating ? energy - needed : energy + needed;
        grid.Material[i] = transition.Into;
        grid.LatentEnergy[i] = 0.0;
        SetCondensedTemperature(grid, i, transition.TemperatureK + leftover / CondensedCapacity(grid, i));
        return true;
    }

    /// <summary>Liquid left in a boiling tile below this, kg, goes up with the rest.</summary>
    private const double ResidueKg = 1e-9;

    /// <summary>
    /// A liquid above its boiling point turns into its gas, a little at a time: exactly
    /// as much as the excess heat can pay for, which pins the liquid at the boiling point.
    /// The gas joins the tile's mixture at the boiling point, and the fluids layer carries it away.
    /// </summary>
    private static bool Boiling(WorldGrid grid, PhaseRules rules, int i)
    {
        ushort m = grid.Material[i];
        if (m == MaterialTable.Empty) return false;
        var boil = rules.Boil[m];
        double t = grid.Temperature[i];
        if (!boil.Exists || t <= boil.TemperatureK) return false;

        double excess = (t - boil.TemperatureK) * CondensedCapacity(grid, i);
        double mass = grid.Mass[i];
        double dm = Math.Min(mass, excess / boil.LatentPerKg);
        if (dm <= 0.0) return false;
        // The last film boils off whole, paid for by the gas around it, rather than dwindling forever.
        if (mass - dm < ResidueKg) dm = mass;

        // Each kilogram leaves as gas at the boiling point, carrying the energy that holds there.
        double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
        double carried = dm * Joules(grid, boil.Into, boil.TemperatureK);
        int species = grid.Materials.GasIndexOf(boil.Into);
        grid.GasMass[species][i] += dm;
        if (dm >= mass) grid.SetCondensed(i, MaterialTable.Empty, 0.0);
        else grid.Mass[i] = mass - dm;
        EnergyAudit.SolveTemperatures(grid, i, condensed - carried, gas + carried);
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
            double gasKg = grid.GasMass[s][i];
            double t = grid.GasTemperatureAt(i);
            ushort m = grid.Material[i];
            if (!c.Exists || gasKg <= 0.0 || t >= c.TemperatureK) continue;
            if (m != MaterialTable.Empty && m != c.Into) continue;

            double deficit = (c.TemperatureK - t) * GasCapacity(grid, i);
            double dm = Math.Min(gasKg, deficit / c.LatentPerKg);
            if (dm <= 0.0) continue;

            // The liquid forms at the condensation point; the latent heat it gives up stays in the gas.
            double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
            double carried = dm * Joules(grid, c.Into, c.TemperatureK);
            grid.GasMass[s][i] = dm >= gasKg ? 0.0 : gasKg - dm;
            if (m == MaterialTable.Empty)
            {
                grid.SetCondensed(i, c.Into, dm);
                grid.Temperature[i] = c.TemperatureK;
            }
            else grid.Mass[i] += dm;
            EnergyAudit.SolveTemperatures(grid, i, condensed + carried, gas - carried);
            changed = true;
        }
        return changed;
    }

    /// <summary>Energy, as the audit counts it, of one kg of a material at a temperature.</summary>
    private static double Joules(WorldGrid grid, ushort material, double temperatureK) =>
        grid.Materials[material].SpecificHeat * temperatureK + EnergyAudit.OffsetsFor(grid.Materials)[material];
}
