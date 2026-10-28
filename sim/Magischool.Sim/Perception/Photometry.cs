using System;

namespace Magischool.Sim.Perception;

/// <summary>
/// Visible light from hot matter: Planck's law weighted by the eye's daylight sensitivity, the
/// CIE 1931 luminosity function V(λ), at 683 lumens per watt at its peak. So nothing glows by a
/// rule: iron at the Draper point (798 K) gives 0.02 lm/m², which a dark-adapted eye just sees
/// and nobody notices by day; at 1000 K it is a dull red 8.5 lm/m²; a tungsten filament at
/// 2800 K turns 15 lm of every watt it radiates into light, the sun at 5800 K 92.
/// </summary>
public static class Photometry
{
    /// <summary>Lumens per watt of light at 555 nm, by the definition of the candela.</summary>
    public const double PeakEfficacy = 683.0;

    public const double StefanBoltzmann = 5.670374419e-8;

    /// <summary>Below this nothing gives off light anyone could see, K.</summary>
    public const double GlowFloorK = 600.0;

    private const double TableMaxK = 6000.0;
    private const double TableStepK = 1.0;

    private static readonly Lazy<double[]> Table = new(Build);

    /// <summary>
    /// Luminous exitance of a black body at temperature <paramref name="kelvin"/>, lm/m²: the
    /// visible light leaving each square metre of its surface. A grey surface gives its
    /// emissivity times this.
    /// </summary>
    public static double LuminousExitance(double kelvin)
    {
        if (!(kelvin > GlowFloorK)) return 0.0;
        var table = Table.Value;
        double at = Math.Min(kelvin, TableMaxK - TableStepK) / TableStepK;
        int k = (int)at;
        double f = at - k;
        return table[k] + (table[k + 1] - table[k]) * f;
    }

    /// <summary>Lumens per watt of the whole radiation of a black body at this temperature.</summary>
    public static double Efficacy(double kelvin) =>
        kelvin > GlowFloorK ? LuminousExitance(kelvin) / (StefanBoltzmann * Math.Pow(kelvin, 4)) : 0.0;

    /// <summary>
    /// CIE 1931 photopic luminosity function at wavelength <paramref name="nm"/>, the two-lobe
    /// fit of Wyman, Sloan and Shirley (2013) to the tabulated ȳ(λ).
    /// </summary>
    public static double Luminosity(double nm)
    {
        static double Lobe(double x, double mu, double below, double above)
        {
            double s = x < mu ? below : above;
            double t = (x - mu) / s;
            return Math.Exp(-0.5 * t * t);
        }
        return 0.821 * Lobe(nm, 568.8, 46.9, 40.5) + 0.286 * Lobe(nm, 530.9, 16.3, 31.1);
    }

    private static double[] Build()
    {
        const double h = 6.62607015e-34, c = 2.99792458e8, k = 1.380649e-23;
        int n = (int)(TableMaxK / TableStepK) + 1;
        var table = new double[n];
        for (int t = 0; t < n; t++)
        {
            double kelvin = t * TableStepK;
            if (kelvin < GlowFloorK - 2 * TableStepK) continue;
            double sum = 0.0;
            for (int nm = 360; nm <= 830; nm++)
            {
                double l = nm * 1e-9;
                double x = h * c / (l * k * kelvin);
                if (x > 700.0) continue;
                // Spectral exitance, W/(m²·m), over 1 nm.
                double planck = 2.0 * Math.PI * h * c * c / (l * l * l * l * l) / (Math.Exp(x) - 1.0);
                sum += planck * Luminosity(nm) * 1e-9;
            }
            table[t] = PeakEfficacy * sum;
        }
        return table;
    }
}
