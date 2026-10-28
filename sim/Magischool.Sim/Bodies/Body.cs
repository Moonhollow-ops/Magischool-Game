using System;
using System.IO;

namespace Magischool.Sim.Bodies;

/// <summary>Why a body died, for whoever finds it.</summary>
public enum CauseOfDeath : byte
{
    None,
    /// <summary>The brain went without oxygen: drowning, smothering, foul air, or a stopped heart.</summary>
    Hypoxia,
    /// <summary>The brain itself was cooked, frozen or crushed.</summary>
    BrainInjury,
}

/// <summary>
/// One living (or dead) body: a creature built of tissue, standing in a column of tiles. Its
/// state is all physical: the mass and temperature of each layer of tissue, the blood, the
/// stores of fuel, oxygen and water. Everything it feels or suffers (cold, burns, thirst,
/// suffocation) is read from that state. See <see cref="BodySystem"/>.
///
/// <para>
/// Layers are flat arrays over the plan's nodes (part by part, outermost layer first).
/// The energy and mass it holds are counted with the world's (<see cref="Physiology"/>).
/// </para>
/// </summary>
public sealed class Body
{
    public Body(int id, BodyPlan plan, int x, int y, int z)
    {
        Id = id;
        Plan = plan;
        X = x; Y = y; Z = z;
        int n = plan.NodeCount, parts = plan.Parts.Length;
        Mass = new double[n];
        Temperature = new double[n];
        WaterKg = new double[n];
        FrozenKg = new double[n];
        Burn = new double[n];
        Frostbite = new double[n];
        Trauma = new double[parts];
        Bleed = new double[parts];
    }

    public int Id { get; }
    public BodyPlan Plan { get; }

    /// <summary>The tile its feet are in; part p is in the tile <c>Plan.Parts[p].Dz</c> above.</summary>
    public int X, Y, Z;

    // Per layer of tissue.
    /// <summary>kg, of which <see cref="WaterKg"/> is water and <see cref="FrozenKg"/> of that water is ice.</summary>
    public readonly double[] Mass, WaterKg, FrozenKg;
    /// <summary>K.</summary>
    public readonly double[] Temperature;
    /// <summary>
    /// Heat damage, the Henriques damage integral Ω: cells die as 1 − e^−Ω. One is a burn through
    /// that layer; 44 °C takes hours to do it, 60 °C seconds.
    /// </summary>
    public readonly double[] Burn;
    /// <summary>The most of a layer's water that has frozen (0 to 1): ice crystals tear the cells.</summary>
    public readonly double[] Frostbite;

    // Per part.
    /// <summary>
    /// Blunt injury: energy of impacts the part has taken over what its tissue can absorb (1 is
    /// broken: a shattered limb, a crushed chest or skull).
    /// </summary>
    public readonly double[] Trauma;
    /// <summary>Blood lost from the part's wounds, kg/s. Clots close it over minutes.</summary>
    public readonly double[] Bleed;

    /// <summary>Circulating blood, kg, and its temperature, K: the body's core temperature.</summary>
    public double BloodKg, BloodTemperature;

    /// <summary>What is in the stomach: starch and water, each in kg with the energy it holds, J.</summary>
    public double GutCarbKg, GutCarbJ, GutWaterKg, GutWaterJ;

    /// <summary>Glycogen in liver and muscle, kg, and its chemical energy, J. Fat is held in the fat layers, its chemical energy in <see cref="FatJ"/>.</summary>
    public double GlycogenKg, GlycogenJ, FatJ;

    /// <summary>Oxygen held in blood, lungs and muscle, and carbon dioxide waiting to be breathed out: kg and J.</summary>
    public double OxygenKg, OxygenJ, CarbonDioxideKg, CarbonDioxideJ;

    /// <summary>Urine in the bladder, and blood from wounds not yet on the ground: kg and J.</summary>
    public double BladderKg, BladderJ, SpillKg, SpillJ;

    /// <summary>Kinetic energy of the whole body while falling, J, its speed, m/s, and how far it has fallen since it last moved a whole tile, m.</summary>
    public double KineticJ, FallSpeed, FallTravel;

    /// <summary>Brain damage from want of oxygen (1 is brain death).</summary>
    public double BrainHypoxia;

    public bool HeartStopped;
    public bool Alive = true;
    public CauseOfDeath Cause;

    /// <summary>Sleep pressure: 0 rested, 1 after sixteen hours awake.</summary>
    public double SleepPressure;
    public bool Asleep;

    /// <summary>What it wears, in clo (1 is a suit or a school robe; 0 naked).</summary>
    public double ClothingClo = 1.0;

    /// <summary>A step it means to take this tick, -1 to 1 in x and y (see <see cref="MoveBody"/>).</summary>
    public int StepX, StepY;

    /// <summary>Work it has done (walking, climbing) that its metabolism still has to pay for, J of fuel.</summary>
    public double PendingWorkJ;

    // Readouts from the last tick: not state, never saved or hashed.
    public double MetabolicW { get; internal set; }
    public double ShiverW { get; internal set; }
    public double SweatKgPerS { get; internal set; }
    public double MeanSkinTemperature { get; internal set; }
    public double OxygenSaturation { get; internal set; }

    /// <summary>Kilograms of fat: what the fat layers hold.</summary>
    public double FatKg
    {
        get
        {
            double kg = 0.0;
            for (int n = 0; n < Mass.Length; n++)
                if (Plan.NodeTissue[n] == Tissue.Fat) kg += Mass[n];
            return kg;
        }
    }

    public double OxygenFraction => OxygenKg / Plan.OxygenStoreKg;
    public double BloodFraction => BloodKg / Plan.BloodKg;

    /// <summary>Body water short of where it started, kg: what thirst measures.</summary>
    public double WaterDeficitKg
    {
        get
        {
            double start = Plan.BloodKg, now = BloodKg;
            for (int n = 0; n < Mass.Length; n++)
            {
                start += Plan.InitialMass[n] * BodyPlans.Of(Plan.NodeTissue[n]).WaterFraction;
                now += WaterKg[n];
            }
            return start - now;
        }
    }

    /// <summary>How well its brain works, 0 (dead) to 1. Below a half it can't stay awake.</summary>
    public double BrainFunction => Math.Max(0.0, 1.0 - Math.Max(BrainHypoxia, BrainInjury));

    /// <summary>Damage to the brain itself from heat, frost or blows (1 is destroyed).</summary>
    public double BrainInjury
    {
        get
        {
            int b = Plan.BrainNode;
            return Math.Max(Math.Max(1.0 - Math.Exp(-Burn[b]), Frostbite[b]), Trauma[Plan.HeadPart]);
        }
    }

    /// <summary>
    /// Whether it is awake and aware: a beating heart, enough oxygen, blood and brain, and a core
    /// between 30 and 41.5 °C.
    /// </summary>
    public bool Conscious =>
        Alive && !HeartStopped && !Asleep && OxygenFraction > 0.4 && BloodFraction > 0.6
        && BloodTemperature > 303.15 && BloodTemperature < 314.65 && BrainFunction > 0.5;

    /// <summary>0 fed to 1 starving: the glycogen it has left, with food still in the stomach counting toward it.</summary>
    public double Hunger => Math.Clamp(1.0 - (GlycogenKg + GutCarbKg) / Plan.GlycogenMaxKg, 0.0, 1.0);

    /// <summary>0 to 1: a water deficit of 4% of its starting weight is all-consuming thirst.</summary>
    public double Thirst => Math.Clamp(WaterDeficitKg / (0.04 * Plan.InitialKg), 0.0, 1.0);

    public double Fatigue => Math.Clamp(SleepPressure, 0.0, 1.0);

    /// <summary>Share of a layer's cells dead from heat or frost.</summary>
    public double Necrosis(int node) => Math.Max(1.0 - Math.Exp(-Burn[node]), Frostbite[node]);

    /// <summary>Share of the skin, by area, that is burned or frozen through (dead).</summary>
    public double DeadSkinShare
    {
        get
        {
            double area = 0.0;
            for (int p = 0; p < Plan.Parts.Length; p++)
                area += Plan.Parts[p].SkinAreaM2 * Necrosis(Plan.LayerStart[p]);
            return area / Plan.TotalSkinAreaM2;
        }
    }

    internal void Write(BinaryWriter w)
    {
        w.Write(Id);
        w.Write(Plan.Key);
        w.Write(X); w.Write(Y); w.Write(Z);
        foreach (var array in new[] { Mass, WaterKg, FrozenKg, Temperature, Burn, Frostbite, Trauma, Bleed })
            foreach (double v in array) w.Write(v);
        foreach (double v in Scalars()) w.Write(v);
        w.Write(HeartStopped); w.Write(Alive); w.Write((byte)Cause); w.Write(Asleep);
    }

    internal static Body Read(BinaryReader r)
    {
        int id = r.ReadInt32();
        var plan = BodyPlans.Get(r.ReadString());
        var body = new Body(id, plan, r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        foreach (var array in new[] { body.Mass, body.WaterKg, body.FrozenKg, body.Temperature, body.Burn, body.Frostbite, body.Trauma, body.Bleed })
            for (int k = 0; k < array.Length; k++) array[k] = r.ReadDouble();
        var s = new double[ScalarCount];
        for (int k = 0; k < s.Length; k++) s[k] = r.ReadDouble();
        body.SetScalars(s);
        body.HeartStopped = r.ReadBoolean();
        body.Alive = r.ReadBoolean();
        body.Cause = (CauseOfDeath)r.ReadByte();
        body.Asleep = r.ReadBoolean();
        return body;
    }

    private const int ScalarCount = 26;

    private double[] Scalars() => new[]
    {
        BloodKg, BloodTemperature, GutCarbKg, GutCarbJ, GutWaterKg, GutWaterJ, GlycogenKg, GlycogenJ, FatJ,
        OxygenKg, OxygenJ, CarbonDioxideKg, CarbonDioxideJ, BladderKg, BladderJ, SpillKg, SpillJ,
        KineticJ, FallSpeed, FallTravel, BrainHypoxia, SleepPressure, ClothingClo, PendingWorkJ, StepX, StepY,
    };

    private void SetScalars(double[] s)
    {
        (BloodKg, BloodTemperature, GutCarbKg, GutCarbJ, GutWaterKg, GutWaterJ, GlycogenKg, GlycogenJ, FatJ) = (s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8]);
        (OxygenKg, OxygenJ, CarbonDioxideKg, CarbonDioxideJ, BladderKg, BladderJ, SpillKg, SpillJ) = (s[9], s[10], s[11], s[12], s[13], s[14], s[15], s[16]);
        (KineticJ, FallSpeed, FallTravel, BrainHypoxia, SleepPressure, ClothingClo, PendingWorkJ) = (s[17], s[18], s[19], s[20], s[21], s[22], s[23]);
        (StepX, StepY) = ((int)s[24], (int)s[25]);
    }
}
