using System;
using System.Collections.Generic;

namespace Magischool.Sim.Bodies;

/// <summary>The kinds of living tissue a body is built from.</summary>
public enum Tissue : byte
{
    Skin,
    Fat,
    Muscle,
    Bone,
    Brain,
    Viscera,
}

/// <summary>
/// Physical constants of one tissue, from the human thermal-physiology literature (Fiala et al.
/// 1999, Gagge 1971) at body temperature. Perfusion is the basal blood flow through it, m³ of
/// blood per m³ of tissue per second; <see cref="BasalShare"/> is its share of the body's resting
/// heat output, spread over all of it by mass.
/// </summary>
public sealed record TissueDef(
    string Name,
    double Density,
    double SpecificHeat,
    double Conductivity,
    double Perfusion,
    double WaterFraction,
    double BasalShare);

public enum PartShape : byte
{
    Cylinder,
    Sphere,
}

/// <summary>
/// One part of a body (a head, a forearm): concentric layers of tissue, outermost first, in the
/// tile <see cref="Dz"/> above the body's feet.
/// </summary>
public sealed record PartDef(
    string Name,
    int Dz,
    PartShape Shape,
    double SkinAreaM2,
    (Tissue Tissue, double Kg)[] Layers,
    double ImpactShare,
    double SoleAreaM2 = 0.0);

/// <summary>
/// The build of one kind of body: its parts and what it starts with. Index lists are fixed, so a
/// body's per-layer state is a flat array in part then layer order.
/// </summary>
public sealed class BodyPlan
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    /// <summary>Tiles from its feet to the top of its head.</summary>
    public required int HeightTiles { get; init; }
    public required PartDef[] Parts { get; init; }
    /// <summary>Resting heat output at 37 °C, W: lying still, as in sleep or a faint.</summary>
    public required double BasalW { get; init; }
    /// <summary>What being awake and on its feet adds, in muscle, W (1.2 met standing, all told).</summary>
    public required double AwakeW { get; init; }
    public required double BloodKg { get; init; }
    /// <summary>The glycogen liver and muscle can hold, kg.</summary>
    public required double GlycogenMaxKg { get; init; }
    /// <summary>Oxygen in blood, lungs and muscle when fully saturated, kg.</summary>
    public required double OxygenStoreKg { get; init; }
    /// <summary>Most the stomach holds, kg.</summary>
    public required double GutMaxKg { get; init; }

    /// <summary>Index of the part holding the brain, and of the part holding heart, lungs and gut.</summary>
    public int HeadPart { get; private set; } = -1;
    public int TorsoPart { get; private set; } = -1;

    /// <summary>Flat index of each part's first layer; <c>LayerStart[Parts.Length]</c> is the node count.</summary>
    public int[] LayerStart { get; private set; } = Array.Empty<int>();
    public int NodeCount { get; private set; }
    /// <summary>Flat index of the brain and of the viscera (where drunk water and digested food go).</summary>
    public int BrainNode { get; private set; } = -1;
    public int VisceraNode { get; private set; } = -1;
    public Tissue[] NodeTissue { get; private set; } = Array.Empty<Tissue>();
    public int[] NodePart { get; private set; } = Array.Empty<int>();
    public double[] InitialMass { get; private set; } = Array.Empty<double>();
    public double TotalSkinAreaM2 { get; private set; }
    public double InitialKg { get; private set; }

    public BodyPlan Build()
    {
        LayerStart = new int[Parts.Length + 1];
        var tissue = new List<Tissue>();
        var part = new List<int>();
        var mass = new List<double>();
        for (int p = 0; p < Parts.Length; p++)
        {
            LayerStart[p] = tissue.Count;
            TotalSkinAreaM2 += Parts[p].SkinAreaM2;
            foreach (var (t, layerKg) in Parts[p].Layers)
            {
                if (t == Tissue.Brain) { BrainNode = tissue.Count; HeadPart = p; }
                if (t == Tissue.Viscera) { VisceraNode = tissue.Count; TorsoPart = p; }
                tissue.Add(t);
                part.Add(p);
                mass.Add(layerKg);
            }
            if (Parts[p].Layers[0].Tissue != Tissue.Skin)
                throw new InvalidOperationException($"Part '{Parts[p].Name}' of '{Key}' must have skin outermost.");
        }
        LayerStart[Parts.Length] = tissue.Count;
        if (BrainNode < 0 || VisceraNode < 0)
            throw new InvalidOperationException($"Body plan '{Key}' needs a brain and viscera.");
        NodeCount = tissue.Count;
        NodeTissue = tissue.ToArray();
        NodePart = part.ToArray();
        InitialMass = mass.ToArray();
        double kg = BloodKg;
        foreach (double m in InitialMass) kg += m;
        InitialKg = kg;
        return this;
    }
}

/// <summary>The built-in tissues and body plans.</summary>
public static class BodyPlans
{
    public static readonly TissueDef[] Tissues =
    {
        // Skin's basal flow is Gagge's 6.3 L/(m²·h); it can open some fourteen-fold to shed heat.
        new("skin", 1085, 3680, 0.47, 9.0e-4, 0.65, 0.04),
        new("fat", 850, 2300, 0.16, 1.3e-4, 0.10, 0.04),
        new("muscle", 1085, 3768, 0.42, 5.4e-4, 0.75, 0.21),
        new("bone", 1357, 1700, 0.75, 0.0, 0.20, 0.0),
        new("brain", 1080, 3850, 0.49, 1.0e-2, 0.77, 0.20),
        new("viscera", 1000, 3700, 0.50, 4.3e-3, 0.75, 0.51),
    };

    public static TissueDef Of(Tissue t) => Tissues[(int)t];

    /// <summary>Blood: what carries heat and oxygen between the tissues.</summary>
    public const double BloodDensity = 1069.0, BloodSpecificHeat = 3650.0;

    /// <summary>
    /// An adult human, 70 kg and 2 m (four tiles) tall: shins and feet in the lowest tile, thighs
    /// above, torso and arms above that, the head on top. 15 kg of it is fat.
    /// </summary>
    public static readonly BodyPlan Human = new BodyPlan
    {
        Key = "human",
        Name = "Human",
        HeightTiles = 4,
        BasalW = 80.0,
        AwakeW = 45.0,
        BloodKg = 5.3,
        GlycogenMaxKg = 0.5,
        OxygenStoreKg = 2.0e-3,
        GutMaxKg = 1.5,
        Parts = new PartDef[]
        {
            new("head", 3, PartShape.Sphere, 0.12,
                new[] { (Tissue.Skin, 0.25), (Tissue.Fat, 0.3), (Tissue.Muscle, 1.0), (Tissue.Bone, 1.4), (Tissue.Brain, 1.4) }, 0.10),
            new("torso", 2, PartShape.Cylinder, 0.62,
                new[] { (Tissue.Skin, 1.6), (Tissue.Fat, 8.0), (Tissue.Muscle, 10.0), (Tissue.Bone, 3.0), (Tissue.Viscera, 7.35) }, 0.25),
            new("left arm", 2, PartShape.Cylinder, 0.16,
                new[] { (Tissue.Skin, 0.3), (Tissue.Fat, 0.9), (Tissue.Muscle, 2.0), (Tissue.Bone, 0.5) }, 0.025),
            new("right arm", 2, PartShape.Cylinder, 0.16,
                new[] { (Tissue.Skin, 0.3), (Tissue.Fat, 0.9), (Tissue.Muscle, 2.0), (Tissue.Bone, 0.5) }, 0.025),
            new("left thigh", 1, PartShape.Cylinder, 0.18,
                new[] { (Tissue.Skin, 0.35), (Tissue.Fat, 1.7), (Tissue.Muscle, 5.0), (Tissue.Bone, 0.8) }, 0.125),
            new("right thigh", 1, PartShape.Cylinder, 0.18,
                new[] { (Tissue.Skin, 0.35), (Tissue.Fat, 1.7), (Tissue.Muscle, 5.0), (Tissue.Bone, 0.8) }, 0.125),
            new("left leg", 0, PartShape.Cylinder, 0.14,
                new[] { (Tissue.Skin, 0.3), (Tissue.Fat, 0.75), (Tissue.Muscle, 1.8), (Tissue.Bone, 0.8) }, 0.175, SoleAreaM2: 0.025),
            new("right leg", 0, PartShape.Cylinder, 0.14,
                new[] { (Tissue.Skin, 0.3), (Tissue.Fat, 0.75), (Tissue.Muscle, 1.8), (Tissue.Bone, 0.8) }, 0.175, SoleAreaM2: 0.025),
        },
    }.Build();

    private static readonly Dictionary<string, BodyPlan> ByKey = new() { [Human.Key] = Human };

    public static BodyPlan Get(string key) =>
        ByKey.TryGetValue(key, out var plan) ? plan : throw new ArgumentException($"No body plan '{key}'.", nameof(key));

    public static bool TryGet(string key, out BodyPlan plan) => ByKey.TryGetValue(key, out plan!);
}
