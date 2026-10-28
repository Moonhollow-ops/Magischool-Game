using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Thermal;

/// <summary>
/// Counts the world's total internal energy, so conservation can be checked
/// at any time. A tile holds sensible heat (mass × specific heat × T), any
/// latent energy it is part-way through absorbing, and the latent heat its
/// material already contains relative to its coldest phase (water carries the
/// heat that melted it; steam also carries the heat that boiled it). Gases
/// count the same way as condensed contents, at the gas temperature.
/// </summary>
public static class EnergyAudit
{
    /// <summary>
    /// Sets a tile's temperatures so its solid or liquid holds <paramref name="condensedJoules"/>
    /// and its gas <paramref name="gasJoules"/>, given what it now contains. In a tile with one
    /// temperature only the sum matters. A part with nothing in it keeps its temperature.
    /// </summary>
    public static void SolveTemperatures(WorldGrid grid, int i, double condensedJoules, double gasJoules)
    {
        double[] offsets = OffsetsFor(grid.Materials);
        var table = grid.Materials;
        double condensedFixed = grid.LatentEnergy[i] + grid.Mass[i] * offsets[grid.Material[i]];
        double gasFixed = 0.0;
        for (int s = 0; s < table.GasCount; s++)
            gasFixed += grid.GasMass[s][i] * offsets[table.GasIds[s]];
        double cc = grid.CondensedHeatCapacity(i), cg = grid.GasHeatCapacity(i);

        if (grid.HasTwoTemperatures(i) && cc > 0.0 && cg > 0.0)
        {
            grid.Temperature[i] = (condensedJoules - condensedFixed) / cc;
            grid.GasTemperature[i] = (gasJoules - gasFixed) / cg;
            return;
        }
        double capacity = cc + cg;
        if (capacity <= 0.0) return;
        grid.SetTemperature(i, (condensedJoules + gasJoules - condensedFixed - gasFixed) / capacity);
    }

    /// <summary>Brings a tile's solid or liquid and its gas to one temperature, keeping their total energy.</summary>
    public static void Equalize(WorldGrid grid, int i)
    {
        double total = TileJoules(grid, i);
        double[] offsets = OffsetsFor(grid.Materials);
        var table = grid.Materials;
        double fixedJoules = grid.LatentEnergy[i] + grid.Mass[i] * offsets[grid.Material[i]];
        for (int s = 0; s < table.GasCount; s++)
            fixedJoules += grid.GasMass[s][i] * offsets[table.GasIds[s]];
        double capacity = grid.HeatCapacity(i);
        if (capacity > 0.0) grid.SetTemperature(i, (total - fixedJoules) / capacity);
    }

    /// <summary>Adds energy to a tile's solid or liquid and to its gas, J.</summary>
    public static void AddJoules(WorldGrid grid, int i, double condensedJoules, double gasJoules) =>
        SolveTemperatures(grid, i, CondensedJoules(grid, i) + condensedJoules, GasJoules(grid, i) + gasJoules);

    /// <summary>All the energy in the world: every tile's heat, chemical and latent energy, and its ether.</summary>
    public static double TotalJoules(WorldGrid grid)
    {
        double total = 0.0;
        for (int i = 0; i < grid.Count; i++)
            total += TileJoules(grid, i) + grid.Ether[i];
        return total;
    }

    /// <summary>All the energy in the simulation: the world's, and that of spells in flight.</summary>
    public static double TotalJoules(Simulation sim)
    {
        double total = TotalJoules(sim.Grid);
        foreach (var bolt in sim.Projectiles) total += bolt.Joules;
        return total;
    }

    /// <summary>Internal energy of one tile: its condensed contents, its gases and any latent energy in progress.</summary>
    public static double TileJoules(WorldGrid grid, int i) => CondensedJoules(grid, i) + GasJoules(grid, i);

    /// <summary>Internal energy of a tile's solid or liquid, latent energy in progress included.</summary>
    public static double CondensedJoules(WorldGrid grid, int i)
    {
        double[] offsets = OffsetsFor(grid.Materials);
        ushort m = grid.Material[i];
        return grid.Mass[i] * (grid.Materials[m].SpecificHeat * grid.Temperature[i] + offsets[m]) + grid.LatentEnergy[i];
    }

    /// <summary>Internal energy of a tile's gas.</summary>
    public static double GasJoules(WorldGrid grid, int i)
    {
        double[] offsets = OffsetsFor(grid.Materials);
        var table = grid.Materials;
        var gasIds = table.GasIds;
        double t = grid.GasTemperatureAt(i);
        double total = 0.0;
        for (int s = 0; s < gasIds.Count; s++)
        {
            double gas = grid.GasMass[s][i];
            if (gas != 0.0) total += gas * (table[gasIds[s]].SpecificHeat * t + offsets[gasIds[s]]);
        }
        return total;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MaterialTable, double[]> Cache = new();

    /// <summary>Cached <see cref="PhaseOffsetsPerKg"/> for a table.</summary>
    public static double[] OffsetsFor(MaterialTable table) => Cache.GetValue(table, PhaseOffsetsPerKg);

    /// <summary>
    /// Energy per kg each material holds above its coldest linked phase,
    /// chosen so that converting at the transition point changes nothing:
    /// offset(upper) = offset(lower) + L + T_transition × (c_lower − c_upper).
    /// </summary>
    public static double[] PhaseOffsetsPerKg(MaterialTable table)
    {
        var offsets = new double[table.Count];
        for (ushort id = 0; id < table.Count; id++)
            offsets[id] = Offset(table, table[id], depth: 0);
        return offsets;
    }

    /// <summary>Reference temperature for heats of combustion, K (25 °C).</summary>
    public const double CombustionReferenceK = 298.15;

    /// <summary>
    /// Energy per kg a fuel holds as chemical energy, chosen so that burning it in air at
    /// 25 °C releases exactly its heat of combustion into the products:
    /// offset(fuel) = Hc + Σ y·(c·T0 + offset) over products and leftover gas − the same for
    /// the air used − c(fuel)·T0.
    /// </summary>
    private static double ChemicalOffset(MaterialTable table, MaterialDef fuel, int depth)
    {
        var air = OxidizerOf(table);
        const double t0 = CombustionReferenceK;
        double airUsed = fuel.OxygenPerKg / (double)air.OxygenFraction;
        var rest = table[table.Id(air.OxygenRemainder!)];
        double products = 0.0;
        foreach (var (key, kg) in CombustionYields(fuel))
        {
            var p = table[table.Id(key)];
            products += kg * (p.SpecificHeat * t0 + Offset(table, p, depth + 1));
        }
        products += (airUsed - fuel.OxygenPerKg) * (rest.SpecificHeat * t0 + Offset(table, rest, depth + 1));
        double reactants = airUsed * (air.SpecificHeat * t0 + Offset(table, air, depth + 1)) + fuel.SpecificHeat * t0;
        return fuel.HeatOfCombustion + products - reactants;
    }

    /// <summary>The gas fuels take their oxygen from: the first gas that carries oxygen (air).</summary>
    public static MaterialDef OxidizerOf(MaterialTable table)
    {
        foreach (var def in table.All)
            if (def.OxygenFraction > 0f) return def;
        throw new InvalidOperationException("Nothing in the table carries oxygen, so nothing can burn.");
    }

    /// <summary>
    /// A fuel's products in kg per kg of fuel, scaled so they add up to exactly
    /// 1 + oxygen per kg (the table stores floats).
    /// </summary>
    public static (string Key, double Kg)[] CombustionYields(MaterialDef fuel)
    {
        double total = 0.0;
        foreach (var p in fuel.CombustionProducts) total += p.KgPerKgFuel;
        double scale = (1.0 + fuel.OxygenPerKg) / total;
        var yields = new (string, double)[fuel.CombustionProducts.Count];
        for (int i = 0; i < yields.Length; i++)
            yields[i] = (fuel.CombustionProducts[i].Key, fuel.CombustionProducts[i].KgPerKgFuel * scale);
        return yields;
    }

    private static double Offset(MaterialTable table, MaterialDef def, int depth)
    {
        if (depth > table.Count) throw new InvalidOperationException($"Phase links around '{def.Key}' form a loop.");

        (string? lowerKey, double temperature, double latent) = def.Phase switch
        {
            Phase.Liquid => (def.FreezesInto, def.MeltingPoint, def.LatentHeatFusion),
            Phase.Gas => (def.CondensesInto, def.BoilingPoint, def.LatentHeatVaporization),
            _ => ((string?)null, 0.0, 0.0),
        };
        if (lowerKey == null) return def.HeatOfCombustion > 0f ? ChemicalOffset(table, def, depth) : 0.0;

        var lower = table[table.Id(lowerKey)];
        return Offset(table, lower, depth + 1) + latent + temperature * (lower.SpecificHeat - def.SpecificHeat);
    }
}
