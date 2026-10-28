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
/// count the same way as condensed contents.
/// </summary>
public static class EnergyAudit
{
    /// <summary>
    /// Sets a tile's temperature so that its energy equals <paramref name="joules"/>, given what
    /// it now contains. Leaves the temperature alone if the tile holds nothing.
    /// </summary>
    public static void SolveTemperature(WorldGrid grid, int i, double joules)
    {
        double capacity = grid.HeatCapacity(i);
        if (capacity <= 0.0) return;
        double[] offsets = OffsetsFor(grid.Materials);
        var table = grid.Materials;
        double fixedPart = grid.LatentEnergy[i] + grid.Mass[i] * offsets[grid.Material[i]];
        for (int s = 0; s < table.GasCount; s++)
            fixedPart += grid.GasMass[s][i] * offsets[table.GasIds[s]];
        grid.Temperature[i] = (joules - fixedPart) / capacity;
    }

    public static double TotalJoules(WorldGrid grid)
    {
        double total = 0.0;
        for (int i = 0; i < grid.Count; i++)
            total += TileJoules(grid, i);
        return total;
    }

    /// <summary>Internal energy of one tile: its condensed contents, its gases and any latent energy in progress.</summary>
    public static double TileJoules(WorldGrid grid, int i)
    {
        double[] offsets = OffsetsFor(grid.Materials);
        var table = grid.Materials;
        double t = grid.Temperature[i];
        ushort m = grid.Material[i];
        double total = grid.Mass[i] * (table[m].SpecificHeat * t + offsets[m]) + grid.LatentEnergy[i];
        var gasIds = table.GasIds;
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
