using System;
using System.Collections.Generic;

namespace Magischool.Sim.Bodies;

/// <summary>The kinds of living tissue bodies are built from. Append new ones at the end: saves store them by number.</summary>
public enum Tissue : byte
{
    Skin,
    Fat,
    Muscle,
    Bone,
    Brain,
    /// <summary>Connective tissue, fluid and the lesser organs (spleen, pancreas) that fill the belly around the rest.</summary>
    Viscera,
    Heart,
    Lung,
    Liver,
    Kidney,
    Gut,
    Eye,
    Tooth,
}

/// <summary>
/// Physical constants of one tissue, from the thermal-physiology literature (Fiala et al. 1999,
/// Gagge 1971, ICRP 89) at body temperature. Perfusion is the resting blood flow through it, m³
/// of blood per m³ of tissue per second; <see cref="BasalShare"/> its share of the body's resting
/// heat, spread over all of it by mass; <see cref="ImpactToleranceJPerKg"/> the energy a
/// kilogram of it absorbs from a blow before it breaks.
/// </summary>
public sealed record TissueDef(
    string Name,
    double Density,
    double SpecificHeat,
    double Conductivity,
    double Perfusion,
    double WaterFraction,
    double BasalShare,
    double ImpactToleranceJPerKg);

public enum PartShape : byte
{
    Cylinder,
    Sphere,
}

/// <summary>
/// What a part does for the body. A part does its job as well as it is whole: burned, frozen or
/// broken tissue does less, and a part cut off does nothing.
/// </summary>
public enum PartFunction : byte
{
    None,
    /// <summary>Thinks, and keeps the body awake and alive.</summary>
    Brain,
    /// <summary>Pumps the blood: heat, oxygen and fuel only move as well as it beats.</summary>
    Heart,
    /// <summary>Takes oxygen from each breath.</summary>
    Lung,
    /// <summary>Stores glycogen and clears the blood of poisons.</summary>
    Liver,
    /// <summary>Makes urine, clearing the blood of what the body can't keep.</summary>
    Kidney,
    /// <summary>Digests food and takes up water.</summary>
    Gut,
    /// <summary>Sees.</summary>
    Eye,
    /// <summary>Chews: without them a body can only eat slowly.</summary>
    Teeth,
    /// <summary>Holds things (and, later, makes a caster's gestures).</summary>
    Hand,
    /// <summary>Carries the body: walking needs at least one sound leg ending in one.</summary>
    Foot,
    /// <summary>Carries the nerves: broken, the body below can't move and the diaphragm stops.</summary>
    Spine,
}

/// <summary>
/// One part of a body (a head, a hand, a heart): concentric layers of tissue, outermost first.
/// An external part is in the tile <see cref="Dz"/> above the body's feet and meets the world
/// through its outer layer; an internal part (an organ) sits inside its parent and meets only its
/// parent's innermost layer. Parts form a tree through <see cref="Parent"/> (by name, empty for the
/// root): cut a part off and everything attached through it comes away too.
/// </summary>
public sealed record PartDef(
    string Name,
    string Parent,
    int Dz,
    PartShape Shape,
    double SkinAreaM2,
    (Tissue Tissue, double Kg)[] Layers,
    double ImpactShare,
    PartFunction Function = PartFunction.None,
    bool Internal = false,
    double SoleAreaM2 = 0.0);

/// <summary>
/// The parts of one body, as it is now. Built from a <see cref="BodyPlan"/>'s parts when a body is
/// made, but each body has its own: a mutation or an enchantment can add a part or change a
/// layer's tissue, and a part cut off leaves with its children as a body of its own.
/// Per-layer state lives in flat arrays over the nodes (part by part, outermost layer first).
/// </summary>
public sealed class Anatomy
{
    public Anatomy(IReadOnlyList<PartDef> parts)
    {
        Parts = new PartDef[parts.Count];
        for (int p = 0; p < parts.Count; p++) Parts[p] = parts[p];
        ParentOf = new int[Parts.Length];
        LayerStart = new int[Parts.Length + 1];
        var tissue = new List<Tissue>();
        var part = new List<int>();
        var mass = new List<double>();
        int lowest = int.MaxValue, highest = int.MinValue;
        for (int p = 0; p < Parts.Length; p++)
        {
            var def = Parts[p];
            ParentOf[p] = def.Parent.Length == 0 ? -1 : IndexOf(def.Parent);
            if (def.Parent.Length > 0 && (ParentOf[p] < 0 || ParentOf[p] >= p))
                throw new InvalidOperationException($"Part '{def.Name}' must come after its parent '{def.Parent}'.");
            if (def.Internal && ParentOf[p] < 0)
                throw new InvalidOperationException($"Internal part '{def.Name}' needs a parent to sit inside.");
            if (def.Layers.Length == 0) throw new InvalidOperationException($"Part '{def.Name}' has no tissue.");
            LayerStart[p] = tissue.Count;
            if (!def.Internal) TotalSkinAreaM2 += def.SkinAreaM2;
            lowest = Math.Min(lowest, def.Dz);
            highest = Math.Max(highest, def.Dz);
            foreach (var (t, layerKg) in def.Layers)
            {
                tissue.Add(t);
                part.Add(p);
                mass.Add(layerKg);
            }
            if (def.Function == PartFunction.Brain) BrainPart = p;
        }
        LayerStart[Parts.Length] = tissue.Count;
        if (lowest != 0 && Parts.Length > 0) throw new InvalidOperationException("The lowest part must be at Dz 0.");
        HeightTiles = Parts.Length == 0 ? 0 : highest + 1;
        NodeTissue = tissue.ToArray();
        NodePart = part.ToArray();
        InitialMass = mass.ToArray();
    }

    public PartDef[] Parts { get; }
    /// <summary>Index of each part's parent, -1 for the root.</summary>
    public int[] ParentOf { get; }
    /// <summary>Flat index of each part's first layer; <c>LayerStart[Parts.Length]</c> is the node count.</summary>
    public int[] LayerStart { get; }
    public Tissue[] NodeTissue { get; }
    public int[] NodePart { get; }
    /// <summary>What each layer weighed when the part was made, kg: thirst and the like measure from it.</summary>
    public double[] InitialMass { get; }
    public int NodeCount => NodeTissue.Length;
    /// <summary>What all its tissue weighed when made, kg.</summary>
    public double InitialKg
    {
        get
        {
            double kg = 0.0;
            foreach (double m in InitialMass) kg += m;
            return kg;
        }
    }
    /// <summary>Area of the skin (and other outer surfaces) meeting the world, m².</summary>
    public double TotalSkinAreaM2 { get; }
    public int HeightTiles { get; }
    /// <summary>The part with the brain, -1 if none (remains).</summary>
    public int BrainPart { get; } = -1;

    public int IndexOf(string name)
    {
        for (int p = 0; p < Parts.Length; p++)
            if (Parts[p].Name == name) return p;
        return -1;
    }

    /// <summary>Innermost layer of a part.</summary>
    public int Inner(int part) => LayerStart[part + 1] - 1;

    /// <summary>Whether part <paramref name="part"/> is <paramref name="ancestor"/> or attached through it.</summary>
    public bool IsWithin(int part, int ancestor)
    {
        for (int p = part; p >= 0; p = ParentOf[p])
            if (p == ancestor) return true;
        return false;
    }

    /// <summary>The part itself and the organs inside it (and theirs), the part first.</summary>
    public IEnumerable<int> WithOrgans(int part)
    {
        yield return part;
        for (int q = part + 1; q < Parts.Length; q++)
            if (Parts[q].Internal && OuterOf(q) == part) yield return q;
    }

    /// <summary>The external part an internal one sits in (itself if external).</summary>
    public int OuterOf(int part)
    {
        int p = part;
        while (Parts[p].Internal) p = ParentOf[p];
        return p;
    }

    /// <summary>A copy with only the parts <paramref name="keep"/> keeps, parents that are dropped cut away, and Dz shifted so the lowest is 0.</summary>
    internal Anatomy Subset(Func<int, bool> keep, out int[] partMap)
    {
        var parts = new List<PartDef>();
        partMap = new int[Parts.Length];
        int shift = int.MaxValue;
        for (int p = 0; p < Parts.Length; p++)
            if (keep(p)) shift = Math.Min(shift, Parts[p].Dz);
        for (int p = 0; p < Parts.Length; p++)
        {
            if (!keep(p)) { partMap[p] = -1; continue; }
            var def = Parts[p];
            string parent = ParentOf[p] >= 0 && keep(ParentOf[p]) ? def.Parent : "";
            partMap[p] = parts.Count;
            parts.Add(def with { Parent = parent, Dz = def.Dz - shift });
        }
        return new Anatomy(parts);
    }

    /// <summary>A copy with one more part, added last.</summary>
    internal Anatomy With(PartDef part)
    {
        var parts = new List<PartDef>(Parts) { part };
        return new Anatomy(parts);
    }
}

/// <summary>The build of one kind of creature: the parts it is born with and what it needs to live.</summary>
public sealed class BodyPlan
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required PartDef[] Parts { get; init; }
    /// <summary>Resting heat output at 37 °C, W: lying still, as in sleep or a faint.</summary>
    public required double BasalW { get; init; }
    /// <summary>What being awake and on its feet adds, in muscle, W (1.2 met standing, all told).</summary>
    public required double AwakeW { get; init; }
    public required double BloodKg { get; init; }
    /// <summary>The glycogen liver and muscle can hold, kg (a fifth of it in the liver).</summary>
    public required double GlycogenMaxKg { get; init; }
    /// <summary>Oxygen in blood, lungs and muscle when fully saturated, kg.</summary>
    public required double OxygenStoreKg { get; init; }
    /// <summary>Most the stomach holds, kg.</summary>
    public required double GutMaxKg { get; init; }

    /// <summary>What it weighs when made, kg.</summary>
    public double InitialKg
    {
        get
        {
            double kg = BloodKg;
            foreach (var part in Parts)
                foreach (var (_, layerKg) in part.Layers) kg += layerKg;
            return kg;
        }
    }

    /// <summary>Tiles it stands tall when whole.</summary>
    public int HeightTiles
    {
        get
        {
            int top = 0;
            foreach (var part in Parts) top = Math.Max(top, part.Dz);
            return top + 1;
        }
    }

    public Anatomy NewAnatomy() => new(Parts);

    /// <summary>A part of the plan by name, for grafting a copy of it.</summary>
    public PartDef? Part(string name)
    {
        foreach (var part in Parts)
            if (part.Name == name) return part;
        return null;
    }
}

/// <summary>The built-in tissues and body plans.</summary>
public static class BodyPlans
{
    public static readonly TissueDef[] Tissues =
    {
        // Skin's resting flow is Gagge's 6.3 L/(m²·h); it can open some fourteen-fold to shed heat.
        new("skin", 1085, 3680, 0.47, 9.0e-4, 0.65, 0.03, 250),
        new("fat", 850, 2300, 0.16, 1.3e-4, 0.10, 0.02, 250),
        new("muscle", 1085, 3768, 0.42, 5.4e-4, 0.75, 0.20, 250),
        new("bone", 1357, 1700, 0.75, 0.0, 0.20, 0.0, 300),
        new("brain", 1080, 3850, 0.49, 1.0e-2, 0.77, 0.20, 250),
        new("viscera", 1000, 3700, 0.50, 4.3e-3, 0.75, 0.05, 250),
        // Coronary flow is 250 mL/min through 300 g; the heart burns a tenth of the body's resting fuel.
        new("heart", 1060, 3720, 0.56, 1.4e-2, 0.79, 0.09, 250),
        // Inflated lung is mostly air. Its own (bronchial) flow is small; the whole blood passes through it to breathe, not to warm it.
        new("lung", 260, 3890, 0.39, 1.0e-2, 0.80, 0.04, 150),
        new("liver", 1080, 3540, 0.52, 1.7e-2, 0.71, 0.24, 200),
        new("kidney", 1070, 3760, 0.53, 6.7e-2, 0.79, 0.07, 200),
        new("gut", 1040, 3690, 0.50, 6.0e-3, 0.79, 0.06, 250),
        new("eye", 1010, 4000, 0.58, 5.0e-4, 0.90, 0.0, 60),
        // Enamel over dentin.
        new("tooth", 2200, 1200, 0.90, 0.0, 0.10, 0.0, 400),
    };

    public static TissueDef Of(Tissue t) => Tissues[(int)t];

    /// <summary>Blood: what carries heat and oxygen between the tissues.</summary>
    public const double BloodDensity = 1069.0, BloodSpecificHeat = 3650.0;

    private static (Tissue, double)[] Limb(double skin, double fat, double muscle, double bone) =>
        new[] { (Tissue.Skin, skin), (Tissue.Fat, fat), (Tissue.Muscle, muscle), (Tissue.Bone, bone) };

    /// <summary>
    /// An adult human, 70 kg and 2 m (four tiles) tall: feet and shins in the lowest tile, thighs
    /// above, belly, chest and arms above that, neck and head on top. 15 kg of it is fat.
    /// </summary>
    public static readonly BodyPlan Human = new()
    {
        Key = "human",
        Name = "Human",
        BasalW = 80.0,
        AwakeW = 45.0,
        BloodKg = 5.3,
        GlycogenMaxKg = 0.5,
        OxygenStoreKg = 2.0e-3,
        GutMaxKg = 1.5,
        Parts = new PartDef[]
        {
            new("abdomen", "", 2, PartShape.Cylinder, 0.28,
                new[] { (Tissue.Skin, 0.75), (Tissue.Fat, 4.0), (Tissue.Muscle, 5.2), (Tissue.Bone, 1.0), (Tissue.Viscera, 1.8) }, 0.12),
            new("liver", "abdomen", 2, PartShape.Sphere, 0.0, new[] { (Tissue.Liver, 1.5) }, 0.0, PartFunction.Liver, Internal: true),
            new("gut", "abdomen", 2, PartShape.Sphere, 0.0, new[] { (Tissue.Gut, 1.15) }, 0.0, PartFunction.Gut, Internal: true),
            new("left kidney", "abdomen", 2, PartShape.Sphere, 0.0, new[] { (Tissue.Kidney, 0.15) }, 0.0, PartFunction.Kidney, Internal: true),
            new("right kidney", "abdomen", 2, PartShape.Sphere, 0.0, new[] { (Tissue.Kidney, 0.15) }, 0.0, PartFunction.Kidney, Internal: true),
            new("chest", "abdomen", 2, PartShape.Cylinder, 0.33,
                new[] { (Tissue.Skin, 0.85), (Tissue.Fat, 4.0), (Tissue.Muscle, 5.6), (Tissue.Bone, 2.0) }, 0.10),
            new("heart", "chest", 2, PartShape.Sphere, 0.0, new[] { (Tissue.Heart, 0.3) }, 0.0, PartFunction.Heart, Internal: true),
            new("left lung", "chest", 2, PartShape.Sphere, 0.0, new[] { (Tissue.Lung, 0.5) }, 0.0, PartFunction.Lung, Internal: true),
            new("right lung", "chest", 2, PartShape.Sphere, 0.0, new[] { (Tissue.Lung, 0.5) }, 0.0, PartFunction.Lung, Internal: true),
            new("neck", "chest", 3, PartShape.Cylinder, 0.03, Limb(0.05, 0.15, 0.6, 0.2), 0.01, PartFunction.Spine),
            new("head", "neck", 3, PartShape.Sphere, 0.11, Limb(0.25, 0.3, 0.8, 1.2), 0.04),
            new("brain", "head", 3, PartShape.Sphere, 0.0, new[] { (Tissue.Brain, 1.4) }, 0.0, PartFunction.Brain, Internal: true),
            new("teeth", "head", 3, PartShape.Sphere, 0.0, new[] { (Tissue.Tooth, 0.02) }, 0.0, PartFunction.Teeth, Internal: true),
            new("left eye", "head", 3, PartShape.Sphere, 0.0004, new[] { (Tissue.Eye, 0.0075) }, 0.0002, PartFunction.Eye),
            new("right eye", "head", 3, PartShape.Sphere, 0.0004, new[] { (Tissue.Eye, 0.0075) }, 0.0002, PartFunction.Eye),
            new("left upper arm", "chest", 2, PartShape.Cylinder, 0.07, Limb(0.13, 0.4, 1.1, 0.25), 0.01),
            new("left forearm", "left upper arm", 2, PartShape.Cylinder, 0.06, Limb(0.1, 0.3, 0.8, 0.2), 0.01),
            new("left hand", "left forearm", 2, PartShape.Cylinder, 0.045, Limb(0.07, 0.05, 0.2, 0.12), 0.0048, PartFunction.Hand),
            new("right upper arm", "chest", 2, PartShape.Cylinder, 0.07, Limb(0.13, 0.4, 1.1, 0.25), 0.01),
            new("right forearm", "right upper arm", 2, PartShape.Cylinder, 0.06, Limb(0.1, 0.3, 0.8, 0.2), 0.01),
            new("right hand", "right forearm", 2, PartShape.Cylinder, 0.045, Limb(0.07, 0.05, 0.2, 0.12), 0.0048, PartFunction.Hand),
            new("left thigh", "abdomen", 1, PartShape.Cylinder, 0.18, Limb(0.35, 1.7, 5.0, 0.8), 0.13),
            new("left shin", "left thigh", 0, PartShape.Cylinder, 0.11, Limb(0.22, 0.5, 1.5, 0.55), 0.105),
            new("left foot", "left shin", 0, PartShape.Cylinder, 0.06, Limb(0.1, 0.15, 0.25, 0.3), 0.07, PartFunction.Foot, SoleAreaM2: 0.025),
            new("right thigh", "abdomen", 1, PartShape.Cylinder, 0.18, Limb(0.35, 1.7, 5.0, 0.8), 0.13),
            new("right shin", "right thigh", 0, PartShape.Cylinder, 0.11, Limb(0.22, 0.5, 1.5, 0.55), 0.105),
            new("right foot", "right shin", 0, PartShape.Cylinder, 0.06, Limb(0.1, 0.15, 0.25, 0.3), 0.07, PartFunction.Foot, SoleAreaM2: 0.025),
        },
    };

    private static readonly Dictionary<string, BodyPlan> ByKey = new() { [Human.Key] = Human };

    public static BodyPlan Get(string key) =>
        ByKey.TryGetValue(key, out var plan) ? plan : throw new ArgumentException($"No body plan '{key}'.", nameof(key));

    public static bool TryGet(string key, out BodyPlan plan) => ByKey.TryGetValue(key, out plan!);
}
