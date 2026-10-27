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

    private static double Offset(MaterialTable table, MaterialDef def, int depth)
    {
        if (depth > table.Count) throw new InvalidOperationException($"Phase links around '{def.Key}' form a loop.");

        (string? lowerKey, double temperature, double latent) = def.Phase switch
        {
            Phase.Liquid => (def.FreezesInto, def.MeltingPoint, def.LatentHeatFusion),
            Phase.Gas => (def.CondensesInto, def.BoilingPoint, def.LatentHeatVaporization),
            _ => ((string?)null, 0.0, 0.0),
        };
        if (lowerKey == null) return 0.0;

        var lower = table[table.Id(lowerKey)];
        return Offset(table, lower, depth + 1) + latent + temperature * (lower.SpecificHeat - def.SpecificHeat);
    }
}
