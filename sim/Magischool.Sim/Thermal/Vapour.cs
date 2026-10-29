using System;

namespace Magischool.Sim.Thermal;

/// <summary>Water vapour in air: how much the air can hold before it condenses.</summary>
public static class Vapour
{
    /// <summary>Molar mass of water, kg/mol.</summary>
    public const double MolarMassWater = 0.018015;

    /// <summary>
    /// Saturation vapour pressure of water, Pa: by the Magnus formula over water above 0 °C (good
    /// to 60 °C, and kept to 120), and over ice below (good to -65 °C, and falling to nothing in
    /// the deep cold beyond).
    /// </summary>
    public static double SaturationPressurePa(double temperatureK)
    {
        double t = temperatureK - 273.15;
        if (t >= 0.0)
        {
            t = Math.Min(t, 120.0);
            return 610.94 * Math.Exp(17.625 * t / (t + 243.04));
        }
        t = Math.Max(t, -150.0);
        return 611.15 * Math.Exp(22.452 * t / (t + 272.55));
    }

    /// <summary>Most water vapour a volume of gas can hold at a temperature before it condenses, kg.</summary>
    public static double SaturationKg(double volumeM3, double temperatureK) =>
        SaturationPressurePa(temperatureK) * volumeM3 * MolarMassWater / (Fluids.FluidSystem.GasConstant * Math.Max(temperatureK, 1.0));
}
