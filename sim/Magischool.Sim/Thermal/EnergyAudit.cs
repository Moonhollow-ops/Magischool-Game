using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Thermal;

/// <summary>
/// Counts the world's total internal energy, so conservation can be checked
/// at any time. A tile holds sensible heat (mass × specific heat × T), any
/// latent energy it is part-way through absorbing, and the latent heat its
/// material already contains relative to its coldest phase (water carries the
/// heat that melted it; steam also carries the heat that boiled it).
/// </summary>
public static class EnergyAudit
{
    public static double TotalJoules(WorldGrid grid)
    {
        double[] offsets = PhaseOffsetsPerKg(grid.Materials);
        double total = 0.0;
        for (int i = 0; i < grid.Count; i++)
        {
            ushort m = grid.Material[i];
            total += grid.Mass[i] * (grid.Materials[m].SpecificHeat * grid.Temperature[i] + offsets[m])
                     + grid.LatentEnergy[i];
        }
        return total;
    }

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
