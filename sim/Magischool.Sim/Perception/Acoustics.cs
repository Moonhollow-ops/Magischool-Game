using System;
using System.Collections.Generic;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Perception;

/// <summary>
/// Sound (layer 10): what makes it, and how it reaches an ear.
///
/// <para>
/// Sounds are made by physics that already happens: flames, feet, falling rubble, bonds
/// breaking, crystals rupturing, bolts in flight and striking. Each radiates a small share of
/// the energy its event moves, in three octave bands (125, 500 and 2000 Hz) shaped by what
/// made it. That energy is already counted in the heat the event leaves, because sound dies
/// into heat within a second, so the energy audit is untouched.
/// </para>
/// <para>
/// A sound reaches an ear by three paths. Straight through: it spreads over a sphere (the
/// inverse-square law), the air absorbs a little more at high pitch (ISO 9613-1), and each wall
/// on the way takes its mass-law transmission loss, 20·log10(m″·f) − 47 dB, so a stone wall
/// stops almost all of it while a low rumble gets through a door better than a high crackle.
/// Around: along the shortest way through open air, out a doorway and round a corner, with
/// Maekawa's diffraction loss for the extra distance the detour adds. And by echo: within a
/// closed room the reflected sound builds up to 4W/R, where R is the room constant from the area
/// and absorption of its surfaces (Sabine), less what has died away by the time it has come as
/// far as the ear (Barron's revised theory, so a long corridor is quieter at its far end). So a
/// bang rings in a stone hall; in a space a quarter open to the sky or more, sound is gone after
/// a reflection or two and builds no echo at all.
/// </para>
/// <para>
/// Not modelled yet: sound in water or carried through solid ground, flanking paths round a
/// wall, a source room's own echo coming through its walls, wind, and the time sound takes to
/// arrive (it crosses 34 m in a tick, so within a building it arrives the tick it is made).
/// </para>
/// </summary>
public static class Acoustics
{
    public const int Bands = 3;

    /// <summary>Centre frequency of each octave band, Hz.</summary>
    public static readonly double[] BandHz = { 125.0, 500.0, 2000.0 };

    /// <summary>The quietest sound a young ear hears in each band, dB SPL (ISO 226).</summary>
    public static readonly double[] HearingThresholdDb = { 22.0, 5.0, 0.0 };

    /// <summary>A-weighting of each band, dB: how loud a band sounds against how strong it is.</summary>
    public static readonly double[] AWeightDb = { -16.1, -3.2, 1.2 };

    /// <summary>Air's absorption in each band at 20 °C and 50% humidity, dB/m (ISO 9613-1).</summary>
    public static readonly double[] AirAbsorptionDbPerM = { 0.0004, 0.0019, 0.0097 };

    /// <summary>The background of a quiet building at night in each band, dB SPL (about 25 dB(A)).</summary>
    public static readonly double[] QuietBackgroundDb = { 30.0, 20.0, 12.0 };

    public const double SpeedOfSound = 343.0;
    public const double ReferenceIntensity = 1e-12;

    /// <summary>
    /// Share of a fire's heat that comes out as noise. Combustion noise is a tiny share; this one
    /// is chosen so a burning log face crackles at about 65 dB from 2 m, as a campfire does.
    /// </summary>
    public const double FireNoiseEfficiency = 5e-9;

    /// <summary>Share of the kinetic energy of something hard striking the ground that is heard (about 1e-4).</summary>
    public const double ImpactEfficiency = 1e-4;

    /// <summary>
    /// Share of the strain energy a solid holds at its breaking strength that a fracture radiates
    /// as sound: oak snapping at about 110 dB from a metre, stone cracking at about 90.
    /// </summary>
    public const double CrackEfficiency = 1e-5;

    /// <summary>
    /// Share of heat dumped suddenly into air that leaves as a blast's sound. Heat put into a tile
    /// faster than sound can cross it is a small explosion; about a percent is heard far off.
    /// </summary>
    public const double BlastEfficiency = 0.01;

    /// <summary>A footstep of a 70 kg body on a hard floor: about 60 dB from a metre, J per kg of the body.</summary>
    public const double FootstepJPerKg = 1.8e-8;

    /// <summary>Coefficient of a bluff body's aerodynamic noise (Curle's dipole law, W = K·ρ·U⁶·D²/c³).</summary>
    public const double WakeNoiseCoefficient = 1e-4;

    /// <summary>Diffraction never takes more than this off a sound going round a corner, dB.</summary>
    public const double MaxDiffractionDb = 25.0;

    /// <summary>How each kind of sound's energy is shared among the bands, low to high, by <see cref="SoundKind"/>.</summary>
    private static readonly double[][] Spectra =
    {
        new[] { 0.5, 0.3, 0.2 }, // fire: a low roar under the crackle
        new[] { 0.35, 0.4, 0.25 }, // footstep
        new[] { 0.6, 0.3, 0.1 }, // thud
        new[] { 0.2, 0.4, 0.4 }, // crack
        new[] { 0.1, 0.3, 0.6 }, // shatter
        new[] { 0.6, 0.3, 0.1 }, // blast
        new[] { 0.2, 0.3, 0.5 }, // whoosh
    };

    public static ReadOnlySpan<double> Spectrum(SoundKind kind) => Spectra[(int)kind];

    // ---------------------------------------------------------------- making sounds

    public static void Emit(Simulation sim, SoundKind kind, double x, double y, double z, double joules, long subject)
    {
        if (joules > 0.0) sim.Sounds.Add(new SoundEvent(kind, x, y, z, joules, subject));
    }

    /// <summary>A sound from the middle of tile i.</summary>
    public static void EmitAt(Simulation sim, SoundKind kind, int i, double joules, long subject = -1)
    {
        var (x, y, z) = sim.Grid.Coordinates(i);
        Emit(sim, kind, x + 0.5, y + 0.5, z + 0.5, joules, subject < 0 ? i : subject);
    }

    /// <summary>Something hard landing with <paramref name="kineticJ"/> of motion in tile i.</summary>
    public static void Impact(Simulation sim, int i, double kineticJ, long subject = -1) =>
        EmitAt(sim, SoundKind.Thud, i, ImpactEfficiency * kineticJ, subject);

    /// <summary>The bond of the solid in tile i breaking: a share of the strain energy it held at its strength.</summary>
    public static void Crack(Simulation sim, int i, SoundKind kind = SoundKind.Crack)
    {
        var grid = sim.Grid;
        if (!grid.HasCondensed(i)) return;
        var def = grid.Materials[grid.Material[i]];
        if (!(def.YoungsModulus > 0f) || !(def.TensileStrength > 0f)) return;
        double stress = def.TensileStrength * 1e6;
        double strain = stress * stress / (2.0 * def.YoungsModulus * 1e9) * Math.Min(grid.CondensedVolume(i), WorldGrid.TileVolumeM3);
        EmitAt(sim, kind, i, CrackEfficiency * strain);
    }

    /// <summary>Heat suddenly let loose in the air of tile i: a blast.</summary>
    public static void Blast(Simulation sim, int i, double joules, long subject = -1) =>
        EmitAt(sim, SoundKind.Blast, i, BlastEfficiency * joules, subject);

    /// <summary>The rush of a body of diameter <paramref name="diameterM"/> through air at <paramref name="speed"/>, J this tick.</summary>
    public static double WakeJoules(double speed, double diameterM, double airDensity = 1.2)
    {
        double u3 = speed * speed * speed;
        return WakeNoiseCoefficient * airDensity * u3 * u3 * diameterM * diameterM
               / (SpeedOfSound * SpeedOfSound * SpeedOfSound) * Simulation.SecondsPerTick;
    }

    /// <summary>Sound reduction of a wall of <paramref name="kgPerM2"/> at frequency f, dB (the field-incidence mass law).</summary>
    public static double MassLawDb(double kgPerM2, double hz) =>
        kgPerM2 > 0.0 ? Math.Max(0.0, 20.0 * Math.Log10(kgPerM2 * hz) - 47.0) : 0.0;

    public static double ToDb(double intensity) => 10.0 * Math.Log10(Math.Max(intensity, 1e-30) / ReferenceIntensity);

    public static double FromDb(double db) => ReferenceIntensity * Math.Pow(10.0, db / 10.0);

    /// <summary>Whether sound in air is shut out of tile i: more than half of it is solid or liquid.</summary>
    public static bool Blocks(WorldGrid grid, int i) => grid.HasCondensed(i) && grid.FillFraction(i) >= 0.5;
}
