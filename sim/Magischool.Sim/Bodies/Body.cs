using System;
using System.IO;

namespace Magischool.Sim.Bodies;

/// <summary>Why a body died, for whoever finds it.</summary>
public enum CauseOfDeath : byte
{
    None,
    /// <summary>The brain went without oxygen: drowning, smothering, foul air, a stopped heart or a broken neck.</summary>
    Hypoxia,
    /// <summary>The brain itself was cooked, frozen or crushed.</summary>
    BrainInjury,
    /// <summary>Torn apart: the brain went with another piece.</summary>
    Dismembered,
}

/// <summary>
/// One body: a creature built of tissue, standing in a column of tiles, or remains (a hand cut
/// off, say) with no brain to keep them alive. Its state is all physical: the mass and
/// temperature of each layer of tissue in each part, the blood, the stores of fuel, oxygen and
/// water. Everything it feels or suffers (cold, burns, blindness, thirst, suffocation) is read
/// from that state. See <see cref="BodySystem"/>.
///
/// <para>
/// Its parts are its own <see cref="Anatomy"/>, which can change: a part grafted on, a layer's
/// tissue changed, a limb cut off. Layers are flat arrays over the anatomy's nodes. The energy
/// and mass it holds are counted with the world's (<see cref="Physiology"/>).
/// </para>
/// </summary>
public sealed class Body
{
    public Body(int id, BodyPlan plan, Anatomy anatomy, int x, int y, int z)
    {
        Id = id;
        Plan = plan;
        X = x; Y = y; Z = z;
        Anatomy = anatomy;
        Mass = Temperature = WaterKg = FrozenKg = Burn = Frostbite = Trauma = Bleed = Array.Empty<double>();
        Resize(anatomy, null, null);
    }

    public int Id { get; }
    /// <summary>The kind of creature it is (or was part of): what it needs to live.</summary>
    public BodyPlan Plan { get; }
    public Anatomy Anatomy { get; private set; }

    /// <summary>The tile its lowest part is in; part p is in the tile <c>Anatomy.Parts[p].Dz</c> above.</summary>
    public int X, Y, Z;

    // Per layer of tissue.
    /// <summary>kg, of which <see cref="WaterKg"/> is water and <see cref="FrozenKg"/> of that water is ice.</summary>
    public double[] Mass, WaterKg, FrozenKg;
    /// <summary>K.</summary>
    public double[] Temperature;
    /// <summary>
    /// Heat damage, the Henriques damage integral Ω: cells die as 1 − e^−Ω. One is a burn through
    /// that layer; 44 °C takes hours to do it, 60 °C seconds.
    /// </summary>
    public double[] Burn;
    /// <summary>The most of a layer's water that has frozen (0 to 1): ice crystals tear the cells.</summary>
    public double[] Frostbite;

    // Per part.
    /// <summary>
    /// Blunt injury: energy of blows the part has taken over what its tissue absorbs. One is
    /// broken (a shattered bone, a ruptured organ); <see cref="BodySystem.SeverTrauma"/> tears it off.
    /// </summary>
    public double[] Trauma;
    /// <summary>Blood lost from the part's wounds, kg/s. Clots close it over minutes.</summary>
    public double[] Bleed;

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

    /// <summary>Poisons the liver and kidneys have failed to clear (1 stops the heart): days of organ failure.</summary>
    public double Toxins;

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

    public int HeightTiles => Anatomy.HeightTiles;

    /// <summary>Remains have no brain: they never lived on their own and never will.</summary>
    public bool IsRemains => Anatomy.BrainPart < 0;

    /// <summary>How whole a part is, 0 to 1: what is left of its working tissue after burns, frost and blows.</summary>
    public double Health(int part)
    {
        int first = Anatomy.LayerStart[part], end = Anatomy.LayerStart[part + 1];
        double kg = 0.0, dead = 0.0;
        for (int n = first; n < end; n++)
        {
            // Skin and fat don't do a limb's work; its muscle and bone do.
            var t = Anatomy.NodeTissue[n];
            if (end - first > 1 && t is Tissue.Skin or Tissue.Fat) continue;
            kg += Mass[n];
            dead += Mass[n] * Necrosis(n);
        }
        double tissue = kg > 0.0 ? 1.0 - dead / kg : 0.0;
        return Math.Max(0.0, tissue * (1.0 - Math.Min(1.0, Trauma[part])));
    }

    /// <summary>
    /// How well the body can do one job, against what its kind is built with (1 is normal, so a
    /// lost kidney leaves 0.5, and a grafted extra lung can lift breathing past 1).
    /// </summary>
    public double FunctionLevel(PartFunction function)
    {
        int planned = 0;
        foreach (var part in Plan.Parts)
            if (part.Function == function) planned++;
        double have = 0.0;
        for (int p = 0; p < Anatomy.Parts.Length; p++)
            if (Anatomy.Parts[p].Function == function) have += Health(p);
        return planned > 0 ? have / planned : have;
    }

    /// <summary>How well it sees, 0 to 1: its best eye.</summary>
    public double Vision
    {
        get
        {
            double best = 0.0;
            for (int p = 0; p < Anatomy.Parts.Length; p++)
                if (Anatomy.Parts[p].Function == PartFunction.Eye) best = Math.Max(best, Health(p));
            return Conscious ? best : 0.0;
        }
    }

    /// <summary>Whether the spine carries the brain's orders down to the body: every spinal part sound enough.</summary>
    public bool SpineIntact
    {
        get
        {
            for (int p = 0; p < Anatomy.Parts.Length; p++)
                if (Anatomy.Parts[p].Function == PartFunction.Spine && Health(p) < 0.3) return false;
            return true;
        }
    }

    /// <summary>Kilograms of fat: what the fat layers hold.</summary>
    public double FatKg
    {
        get
        {
            double kg = 0.0;
            for (int n = 0; n < Mass.Length; n++)
                if (Anatomy.NodeTissue[n] == Tissue.Fat) kg += Mass[n];
            return kg;
        }
    }

    public double OxygenFraction => OxygenKg / Plan.OxygenStoreKg;
    public double BloodFraction => BloodKg / Plan.BloodKg;

    /// <summary>Body water short of what its parts were made with, kg: what thirst measures.</summary>
    public double WaterDeficitKg
    {
        get
        {
            double start = IsRemains ? 0.0 : Plan.BloodKg, now = BloodKg;
            for (int n = 0; n < Mass.Length; n++)
            {
                start += Anatomy.InitialMass[n] * BodyPlans.Of(Anatomy.NodeTissue[n]).WaterFraction;
                now += WaterKg[n];
            }
            return start - now;
        }
    }

    /// <summary>How well its brain works, 0 (dead or none) to 1. Below a half it can't stay awake.</summary>
    public double BrainFunction => IsRemains ? 0.0 : Math.Max(0.0, Math.Min(1.0 - BrainHypoxia, Health(Anatomy.BrainPart)));

    /// <summary>
    /// Whether it is awake and aware: a beating heart, enough oxygen, blood and brain, and a core
    /// between 30 and 41.5 °C.
    /// </summary>
    public bool Conscious =>
        Alive && !HeartStopped && !Asleep && OxygenFraction > 0.4 && BloodFraction > 0.6
        && BloodTemperature > 303.15 && BloodTemperature < 314.65 && BrainFunction > 0.5;

    /// <summary>The glycogen its liver and muscle can hold now, kg: a failing liver holds less.</summary>
    public double GlycogenCapacityKg => Plan.GlycogenMaxKg * (0.8 + 0.2 * Math.Min(1.0, FunctionLevel(PartFunction.Liver)));

    /// <summary>0 fed to 1 starving: the glycogen it has left, with food still in the stomach counting toward it.</summary>
    public double Hunger => Math.Clamp(1.0 - (GlycogenKg + GutCarbKg) / Plan.GlycogenMaxKg, 0.0, 1.0);

    /// <summary>0 to 1: a water deficit of 4% of its weight at birth is all-consuming thirst.</summary>
    public double Thirst => Math.Clamp(WaterDeficitKg / (0.04 * Plan.InitialKg), 0.0, 1.0);

    public double Fatigue => Math.Clamp(SleepPressure, 0.0, 1.0);

    /// <summary>Share of a layer's cells dead from heat or frost.</summary>
    public double Necrosis(int node) => Math.Max(1.0 - Math.Exp(-Burn[node]), Frostbite[node]);

    /// <summary>Share of the skin, by area, that is burned or frozen through (dead).</summary>
    public double DeadSkinShare
    {
        get
        {
            double area = 0.0, total = 0.0;
            for (int p = 0; p < Anatomy.Parts.Length; p++)
            {
                int s = Anatomy.LayerStart[p];
                if (Anatomy.Parts[p].Internal || Anatomy.NodeTissue[s] != Tissue.Skin) continue;
                total += Anatomy.Parts[p].SkinAreaM2;
                area += Anatomy.Parts[p].SkinAreaM2 * Necrosis(s);
            }
            return total > 0.0 ? area / total : 0.0;
        }
    }

    /// <summary>
    /// Gives the body a new anatomy. <paramref name="nodeMap"/> and <paramref name="partMap"/> say
    /// which old node and part each new one continues (-1 for a new one, left zero for the caller
    /// to fill); null keeps nothing.
    /// </summary>
    internal void Resize(Anatomy anatomy, int[]? nodeMap, int[]? partMap)
    {
        static double[] Remap(double[] old, int[]? map, int length)
        {
            var fresh = new double[length];
            if (map != null)
                for (int k = 0; k < length; k++)
                    if (map[k] >= 0) fresh[k] = old[map[k]];
            return fresh;
        }
        int n = anatomy.NodeCount, parts = anatomy.Parts.Length;
        Mass = Remap(Mass, nodeMap, n);
        WaterKg = Remap(WaterKg, nodeMap, n);
        FrozenKg = Remap(FrozenKg, nodeMap, n);
        Temperature = Remap(Temperature, nodeMap, n);
        Burn = Remap(Burn, nodeMap, n);
        Frostbite = Remap(Frostbite, nodeMap, n);
        Trauma = Remap(Trauma, partMap, parts);
        Bleed = Remap(Bleed, partMap, parts);
        Anatomy = anatomy;
    }

    internal void Write(BinaryWriter w)
    {
        w.Write(Id);
        w.Write(Plan.Key);
        w.Write(X); w.Write(Y); w.Write(Z);
        w.Write(Anatomy.Parts.Length);
        foreach (var part in Anatomy.Parts)
        {
            w.Write(part.Name); w.Write(part.Parent); w.Write(part.Dz); w.Write((byte)part.Shape);
            w.Write(part.SkinAreaM2); w.Write(part.ImpactShare); w.Write((byte)part.Function);
            w.Write(part.Internal); w.Write(part.SoleAreaM2);
            w.Write(part.Layers.Length);
            foreach (var (tissue, kg) in part.Layers) { w.Write((byte)tissue); w.Write(kg); }
        }
        foreach (var array in new[] { Mass, WaterKg, FrozenKg, Temperature, Burn, Frostbite, Trauma, Bleed })
            foreach (double v in array) w.Write(v);
        foreach (double v in Scalars()) w.Write(v);
        w.Write(HeartStopped); w.Write(Alive); w.Write((byte)Cause); w.Write(Asleep);
    }

    internal static Body Read(BinaryReader r)
    {
        int id = r.ReadInt32();
        var plan = BodyPlans.Get(r.ReadString());
        int x = r.ReadInt32(), y = r.ReadInt32(), z = r.ReadInt32();
        var parts = new PartDef[r.ReadInt32()];
        for (int p = 0; p < parts.Length; p++)
        {
            string name = r.ReadString(), parent = r.ReadString();
            int dz = r.ReadInt32();
            var shape = (PartShape)r.ReadByte();
            double area = r.ReadDouble(), impact = r.ReadDouble();
            var function = (PartFunction)r.ReadByte();
            bool inside = r.ReadBoolean();
            double sole = r.ReadDouble();
            var layers = new (Tissue, double)[r.ReadInt32()];
            for (int k = 0; k < layers.Length; k++) layers[k] = ((Tissue)r.ReadByte(), r.ReadDouble());
            parts[p] = new PartDef(name, parent, dz, shape, area, layers, impact, function, inside, sole);
        }
        var body = new Body(id, plan, new Anatomy(parts), x, y, z);
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

    private const int ScalarCount = 27;

    private double[] Scalars() => new[]
    {
        BloodKg, BloodTemperature, GutCarbKg, GutCarbJ, GutWaterKg, GutWaterJ, GlycogenKg, GlycogenJ, FatJ,
        OxygenKg, OxygenJ, CarbonDioxideKg, CarbonDioxideJ, BladderKg, BladderJ, SpillKg, SpillJ,
        KineticJ, FallSpeed, FallTravel, BrainHypoxia, SleepPressure, ClothingClo, PendingWorkJ, StepX, StepY, Toxins,
    };

    private void SetScalars(double[] s)
    {
        (BloodKg, BloodTemperature, GutCarbKg, GutCarbJ, GutWaterKg, GutWaterJ, GlycogenKg, GlycogenJ, FatJ) = (s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8]);
        (OxygenKg, OxygenJ, CarbonDioxideKg, CarbonDioxideJ, BladderKg, BladderJ, SpillKg, SpillJ) = (s[9], s[10], s[11], s[12], s[13], s[14], s[15], s[16]);
        (KineticJ, FallSpeed, FallTravel, BrainHypoxia, SleepPressure, ClothingClo, PendingWorkJ) = (s[17], s[18], s[19], s[20], s[21], s[22], s[23]);
        (StepX, StepY, Toxins) = ((int)s[24], (int)s[25], s[26]);
    }
}
