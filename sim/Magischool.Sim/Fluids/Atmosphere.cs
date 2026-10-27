using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Fluids;

/// <summary>World generation helpers for the air.</summary>
public static class Atmosphere
{
    /// <summary>Standard sea-level air pressure, Pa.</summary>
    public const double StandardPressurePa = 101_325.0;

    /// <summary>
    /// Scales the gas in every tile so the air starts at rest: <paramref name="groundPressurePa"/>
    /// at z = 0, thinning with height exactly as <see cref="FluidSystem"/> balances it for
    /// <paramref name="gas"/> at <paramref name="temperatureK"/>. Without this a freshly built
    /// world spends its first minutes settling its own weight. Tiles closed to gas are skipped.
    /// </summary>
    public static void Balance(WorldGrid grid, ushort gas, double temperatureK, double groundPressurePa = StandardPressurePa)
    {
        var table = grid.Materials;
        if (table.GasIndexOf(gas) < 0) throw new ArgumentException("Not a gas.", nameof(gas));

        var profile = Profile(grid.Depth, table[gas].MolarMass, temperatureK, groundPressurePa);

        int plane = grid.Width * grid.Height;
        for (int i = 0; i < grid.Count; i++)
        {
            if (grid.FreeVolume(i) < FluidSystem.GasLockFraction * WorldGrid.TileVolumeM3) continue;
            double pressure = FluidSystem.PressureAt(grid, i);
            if (pressure <= 0.0) continue;
            double scale = profile[i / plane] / pressure;
            foreach (var species in grid.GasMass) species[i] *= scale;
        }
    }

    /// <summary>
    /// Pressure of still gas at each level z = 0 .. levels - 1, from the discrete balance between
    /// stacked tiles: P(z) - P(z+1) = g·dz·(ρ(z) + ρ(z+1)) / 2, with ρ = P·M / (R·T).
    /// </summary>
    public static double[] Profile(int levels, double molarMass, double temperatureK, double groundPressurePa)
    {
        double a = FluidSystem.Gravity * WorldGrid.TileSizeM * molarMass / (2.0 * FluidSystem.GasConstant * temperatureK);
        var profile = new double[levels];
        profile[0] = groundPressurePa;
        for (int z = 1; z < levels; z++)
            profile[z] = profile[z - 1] * (1.0 - a) / (1.0 + a);
        return profile;
    }
}
