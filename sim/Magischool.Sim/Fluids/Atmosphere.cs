using System;
using System.Collections.Generic;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Fluids;

/// <summary>World generation helpers for the air.</summary>
public static class Atmosphere
{
    /// <summary>Standard sea-level air pressure, Pa.</summary>
    public const double StandardPressurePa = 101_325.0;

    /// <summary>Carbon dioxide in the open air, by mass: 420 parts per million by volume.</summary>
    public const double CarbonDioxideMassFraction = 6.4e-4;

    /// <summary>
    /// Relative humidity of the open air the world starts with and the sky goes on with: a
    /// temperate day.
    /// </summary>
    public const double OutdoorHumidity = 0.5;

    /// <summary>
    /// The open air, as mass shares of the world's gases: air, the carbon dioxide plants live on
    /// and water vapour at <paramref name="relativeHumidity"/>. The air material is a fixed mix of
    /// oxygen and nitrogen, so the carbon dioxide comes with the nitrogen it would leave behind
    /// had it been breathed out of that air (2.4 kg a kg): the oxygen it lacks is the oxygen in
    /// it, and taking it back into a plant gives the air its oxygen again.
    /// </summary>
    public static (string Key, double Fraction)[] Outdoor(double temperatureK, double relativeHumidity = OutdoorHumidity)
    {
        double c = CarbonDioxideMassFraction;
        // Per kg of carbon dioxide breathed out of starch: the oxygen it took, and the nitrogen that leaves.
        double oxygen = Bodies.Physiology.GlycogenO2 / Bodies.Physiology.GlycogenCO2;
        double nitrogen = oxygen / BuiltInMaterials.AirOxygenFraction - oxygen;
        double vapour = Math.Clamp(relativeHumidity, 0.0, 1.0) * Vapour.SaturationPressurePa(temperatureK);
        double q = Vapour.MolarMassWater / 0.028965 * vapour / Math.Max(StandardPressurePa - vapour, 1.0);
        double steam = q / (1.0 + q), dry = 1.0 - steam;
        return new[]
        {
            (BuiltInMaterials.Air, dry * (1.0 - c - nitrogen * c)),
            (BuiltInMaterials.Nitrogen, dry * nitrogen * c),
            (BuiltInMaterials.CarbonDioxide, dry * c),
            (BuiltInMaterials.Steam, steam),
        };
    }

    /// <summary>
    /// Makes the gas in every tile the mixture <paramref name="mix"/> (mass shares by key), keeping
    /// each tile's gas mass and temperature. World generation, like <see cref="WorldGrid.Fill"/>:
    /// the energy it takes is not counted.
    /// </summary>
    public static void Mix(WorldGrid grid, IReadOnlyList<(string Key, double Fraction)> mix)
    {
        var table = grid.Materials;
        var share = new double[table.GasCount];
        double total = 0.0;
        foreach (var (key, fraction) in mix)
        {
            share[table.GasIndexOf(table.Id(key))] += fraction;
            total += fraction;
        }
        for (int s = 0; s < share.Length; s++) share[s] /= total;
        for (int i = 0; i < grid.Count; i++)
        {
            double gas = grid.TotalGasMass(i);
            if (!(gas > 0.0)) continue;
            for (int s = 0; s < share.Length; s++) grid.GasMass[s][i] = gas * share[s];
        }
    }

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
