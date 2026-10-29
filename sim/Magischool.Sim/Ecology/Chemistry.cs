using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Ecology;

/// <summary>
/// Slow burning without a flame: what respiring leaves and rotting litter do. The fuel follows
/// its own combustion chemistry (the same oxygen, carbon dioxide and steam per kg as a fire),
/// taking oxygen from the tile's air and giving its heat to the tile's gas, so energy is kept
/// exactly however slowly it goes.
/// </summary>
public static class Chemistry
{
    /// <summary>
    /// Oxidises up to <paramref name="kg"/> of the solid or liquid in tile <paramref name="i"/>
    /// in that tile's air. Returns the kg oxidised, which is less when the air runs short.
    /// </summary>
    public static double Oxidise(WorldGrid grid, int i, double kg)
    {
        if (!(kg > 0.0) || !grid.HasCondensed(i)) return 0.0;
        var table = grid.Materials;
        var def = table[grid.Material[i]];
        if (!(def.HeatOfCombustion > 0f) || !(def.OxygenPerKg > 0f)) return 0.0;
        var oxidizer = EnergyAudit.OxidizerOf(table);
        int air = table.GasIndexOf(table.Id(oxidizer.Key));
        int rest = table.GasIndexOf(table.Id(oxidizer.OxygenRemainder!));
        double o2PerKg = def.OxygenPerKg;
        double airPerKg = o2PerKg / oxidizer.OxygenFraction;

        kg = Math.Min(kg, grid.Mass[i]);
        kg = Math.Min(kg, grid.GasMass[air][i] / airPerKg);
        if (!(kg > 0.0)) return 0.0;

        double total = EnergyAudit.TileJoules(grid, i);
        double before = grid.Mass[i];
        double mass = before - kg;
        if (mass <= 1e-12)
        {
            kg = before;
            grid.SetCondensed(i, MaterialTable.Empty, 0.0);
        }
        else
        {
            grid.LatentEnergy[i] *= mass / before;
            grid.Mass[i] = mass;
        }

        grid.GasMass[air][i] = Math.Max(0.0, grid.GasMass[air][i] - kg * airPerKg);
        grid.GasMass[rest][i] += kg * (airPerKg - o2PerKg);
        foreach (var (key, y) in EnergyAudit.CombustionYields(def))
            grid.GasMass[table.GasIndexOf(table.Id(key))][i] += kg * y;

        // The solid keeps its temperature; the heat goes to the gas.
        double condensed = EnergyAudit.CondensedJoules(grid, i);
        EnergyAudit.SolveTemperatures(grid, i, condensed, total - condensed);
        return kg;
    }
}
