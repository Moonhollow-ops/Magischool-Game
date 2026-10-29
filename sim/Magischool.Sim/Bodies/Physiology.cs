using System;
using System.Runtime.CompilerServices;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Bodies;

/// <summary>
/// How a body's energy and mass are counted, and the chemistry of what it burns.
///
/// <para>
/// A layer of tissue holds m·(c·T + o) − ice·L joules: its heat, less the latent heat its frozen
/// water has given up. The offset o is chosen so that a kilogram of tissue at body temperature
/// holds what a kilogram of water there does, so water drunk, sweated or breathed out moves its
/// energy in and out of the body exactly as the world counts it. Blood is counted the same way.
/// The stores (food in the stomach, glycogen, fat's chemical energy, oxygen, carbon dioxide, urine)
/// are lumps of kg and J that move whole. A falling body's kinetic energy counts too. So the
/// world's energy plus every body's never changes, except by what falling and climbing trade
/// with gravity (<see cref="BodySystem.ReleasedPotentialJ"/>).
/// </para>
/// </summary>
public static class Physiology
{
    /// <summary>Normal core temperature, 37 °C, and the temperature stores are counted at.</summary>
    public const double ReferenceK = 310.15;

    /// <summary>Tissue water freezes a little below 0 °C, for the salts in it.</summary>
    public const double TissueFreezingK = 272.6;

    public const double FusionJPerKg = 334_000.0;
    public const double BoilingK = 373.15;

    /// <summary>Chemical energy of glycogen and of fat, J/kg (heats of combustion, water left liquid).</summary>
    public const double GlycogenJPerKg = 17_500_000.0;
    public const double FatJPerKg = 39_500_000.0;

    // Chemistry per kg burned. Glycogen is starch, (C6H10O5)n; fat is tripalmitin, C51H98O6.
    // The water made is whatever keeps the mass exact.
    public const double GlycogenO2 = 1.18409, GlycogenCO2 = 1.62853;
    public const double FatO2 = 2.87347, FatCO2 = 2.78007;
    // Making fat from glycogen: 12.08 C6H10O5 -> C51H98O6 + 21.5 CO2 + 11.42 H2O.
    public const double LipogenesisFat = 0.41207, LipogenesisCO2 = 0.48295;

    public const double MolarMassO2 = 0.031998;

    /// <summary>Fat burnable from a fat layer: all but what keeps it half water.</summary>
    public static double BurnableFat(Body body, int n) => Math.Max(0.0, body.Mass[n] - 2.0 * body.WaterKg[n]);

    /// <summary>The materials and constants a table gives bodies.</summary>
    public sealed class World
    {
        public readonly MaterialTable Table;
        public readonly double[] Offsets;
        public readonly ushort Water, Blood, Steam, CarbonDioxide;
        public readonly int AirSpecies, NitrogenSpecies, CarbonDioxideSpecies, SteamSpecies;
        public readonly double OxygenFraction;
        /// <summary>Per tissue, and for blood: the energy offset o, J/kg.</summary>
        public readonly double[] TissueOffset;
        public readonly double BloodOffset;
        /// <summary>Energy a kg of oxygen (as the air it leaves behind as nitrogen) and of carbon dioxide holds at <see cref="ReferenceK"/>.</summary>
        public readonly double OxygenJPerKg, CarbonDioxideJPerKg;
        /// <summary>Energy a kg of water holds at <see cref="ReferenceK"/>.</summary>
        public readonly double WaterJPerKg;

        internal World(MaterialTable table)
        {
            Table = table;
            Offsets = EnergyAudit.OffsetsFor(table);
            Water = table.Id(BuiltInMaterials.Water);
            Blood = table.Id(BuiltInMaterials.Blood);
            Steam = table.Id(BuiltInMaterials.Steam);
            CarbonDioxide = table.Id(BuiltInMaterials.CarbonDioxide);
            var air = EnergyAudit.OxidizerOf(table);
            ushort airId = table.Id(air.Key), nitrogenId = table.Id(air.OxygenRemainder!);
            AirSpecies = table.GasIndexOf(airId);
            NitrogenSpecies = table.GasIndexOf(nitrogenId);
            CarbonDioxideSpecies = table.GasIndexOf(CarbonDioxide);
            SteamSpecies = table.GasIndexOf(Steam);
            OxygenFraction = air.OxygenFraction;

            double cw = table[Water].SpecificHeat, ow = Offsets[Water];
            WaterJPerKg = cw * ReferenceK + ow;
            TissueOffset = new double[BodyPlans.Tissues.Length];
            for (int t = 0; t < TissueOffset.Length; t++)
                TissueOffset[t] = ow + (cw - BodyPlans.Tissues[t].SpecificHeat) * ReferenceK;
            BloodOffset = ow + (cw - BodyPlans.BloodSpecificHeat) * ReferenceK;

            double f = OxygenFraction;
            OxygenJPerKg = (GasJPerKg(airId, ReferenceK) - (1.0 - f) * GasJPerKg(nitrogenId, ReferenceK)) / f;
            CarbonDioxideJPerKg = GasJPerKg(CarbonDioxide, ReferenceK);
        }

        /// <summary>Energy of a kg of a material at a temperature, as the world counts it, J.</summary>
        public double GasJPerKg(ushort material, double temperatureK) => Table[material].SpecificHeat * temperatureK + Offsets[material];

        public double Offset(Tissue t) => TissueOffset[(int)t];
    }

    private static readonly ConditionalWeakTable<MaterialTable, World> Cache = new();

    public static World For(MaterialTable table) => Cache.GetValue(table, t => new World(t));

    /// <summary>Energy of one layer of tissue, J.</summary>
    public static double NodeJoules(World w, Body body, int n)
    {
        var tissue = body.Anatomy.NodeTissue[n];
        var def = BodyPlans.Of(tissue);
        return body.Mass[n] * (def.SpecificHeat * body.Temperature[n] + w.Offset(tissue)) - body.FrozenKg[n] * FusionJPerKg;
    }

    public static double BloodJoules(World w, Body body) =>
        body.BloodKg * (BodyPlans.BloodSpecificHeat * body.BloodTemperature + w.BloodOffset);

    /// <summary>All the energy a body holds, J.</summary>
    public static double Joules(World w, Body body)
    {
        double total = BloodJoules(w, body);
        for (int n = 0; n < body.Mass.Length; n++) total += NodeJoules(w, body, n);
        return total + body.GutCarbJ + body.GutWaterJ + body.GlycogenJ + body.FatJ + body.OxygenJ + body.CarbonDioxideJ
               + body.BladderJ + body.SpillJ + body.KineticJ;
    }

    /// <summary>All the mass a body holds, kg.</summary>
    public static double Kg(Body body)
    {
        double total = body.BloodKg;
        foreach (double m in body.Mass) total += m;
        return total + body.GutCarbKg + body.GutWaterKg + body.GlycogenKg + body.OxygenKg + body.CarbonDioxideKg
               + body.BladderKg + body.SpillKg;
    }

    /// <summary>Heat capacity of a layer of tissue, J/K.</summary>
    public static double Capacity(Body body, int n) => body.Mass[n] * BodyPlans.Of(body.Anatomy.NodeTissue[n]).SpecificHeat;

    public static double BloodCapacity(Body body) => body.BloodKg * BodyPlans.BloodSpecificHeat;

    /// <summary>Puts heat into a layer (or takes it out), J.</summary>
    public static void Heat(Body body, int n, double joules) => body.Temperature[n] += joules / Capacity(body, n);

    /// <summary>
    /// Adds <paramref name="kg"/> of matter carrying <paramref name="joules"/> to a layer (or, with
    /// both negative, takes it out), keeping the layer's energy exact. Its temperature follows.
    /// </summary>
    public static void AddMass(World w, Body body, int n, double kg, double joules)
    {
        double e = NodeJoules(w, body, n) + joules;
        double m = body.Mass[n] + kg;
        var tissue = body.Anatomy.NodeTissue[n];
        body.Mass[n] = m;
        body.Temperature[n] = ((e + body.FrozenKg[n] * FusionJPerKg) / m - w.Offset(tissue)) / BodyPlans.Of(tissue).SpecificHeat;
    }

    /// <summary>Energy <paramref name="kg"/> of a layer's own tissue carries at its temperature, J.</summary>
    public static double JoulesOf(World w, Body body, int n, double kg)
    {
        var tissue = body.Anatomy.NodeTissue[n];
        return kg * (BodyPlans.Of(tissue).SpecificHeat * body.Temperature[n] + w.Offset(tissue));
    }

    /// <summary>Adds blood carrying <paramref name="joules"/> (or, both negative, takes it out).</summary>
    public static void AddBlood(World w, Body body, double kg, double joules)
    {
        double e = BloodJoules(w, body) + joules;
        body.BloodKg += kg;
        if (body.BloodKg > 0.0)
            body.BloodTemperature = (e / body.BloodKg - w.BloodOffset) / BodyPlans.BloodSpecificHeat;
    }

    public static double BloodJoulesOf(World w, Body body, double kg) =>
        kg * (BodyPlans.BloodSpecificHeat * body.BloodTemperature + w.BloodOffset);

    /// <summary>Water a layer can give up: what is in it and not frozen.</summary>
    public static double FreeWater(Body body, int n) => Math.Max(0.0, body.WaterKg[n] - body.FrozenKg[n]);

    /// <summary>A healthy, rested, fed body of the plan, warm through, standing with its feet in tile (x, y, z).</summary>
    public static Body Create(World w, BodyPlan plan, int id, int x, int y, int z)
    {
        var anatomy = plan.NewAnatomy();
        var body = new Body(id, plan, anatomy, x, y, z);
        for (int n = 0; n < anatomy.NodeCount; n++)
        {
            var tissue = anatomy.NodeTissue[n];
            body.Mass[n] = anatomy.InitialMass[n];
            body.WaterKg[n] = anatomy.InitialMass[n] * BodyPlans.Of(tissue).WaterFraction;
            // The usual resting profile: warm at the centre, the skin a few degrees cooler.
            body.Temperature[n] = tissue switch
            {
                Tissue.Skin => 307.15,
                Tissue.Fat => 308.65,
                Tissue.Muscle => 309.65,
                Tissue.Bone => 309.95,
                _ => 310.45,
            };
        }
        body.BloodKg = plan.BloodKg;
        body.BloodTemperature = ReferenceK;
        body.GlycogenKg = 0.8 * plan.GlycogenMaxKg;
        body.GlycogenJ = body.GlycogenKg * GlycogenJPerKg;
        double lipid = 0.0;
        for (int n = 0; n < anatomy.NodeCount; n++)
            if (anatomy.NodeTissue[n] == Tissue.Fat) lipid += body.Mass[n] - body.WaterKg[n];
        body.FatJ = lipid * FatJPerKg;
        body.OxygenKg = 0.97 * plan.OxygenStoreKg;
        body.OxygenJ = body.OxygenKg * w.OxygenJPerKg;
        return body;
    }

    /// <summary>Lipid in the fat layers, kg: what <see cref="Body.FatJ"/> is the energy of.</summary>
    public static double LipidKg(Body body)
    {
        double kg = 0.0;
        for (int n = 0; n < body.Mass.Length; n++)
            if (body.Anatomy.NodeTissue[n] == Tissue.Fat) kg += body.Mass[n] - body.WaterKg[n];
        return kg;
    }

    /// <summary>Whether a layer is watery enough to hold the body's spare water (blood plasma bathes it).</summary>
    public static bool HoldsWater(Body body, int n) => BodyPlans.Of(body.Anatomy.NodeTissue[n]).WaterFraction >= 0.5;

    /// <summary>
    /// Takes up to <paramref name="kg"/> of liquid water from the watery layers, each in proportion
    /// to its free water, and returns what it took: kg and J (both positive).
    /// </summary>
    public static (double Kg, double J) RemoveWater(World w, Body body, double kg)
    {
        double free = 0.0;
        for (int n = 0; n < body.Mass.Length; n++)
            if (HoldsWater(body, n)) free += FreeWater(body, n);
        if (free <= 0.0 || kg <= 0.0) return (0.0, 0.0);
        double share = Math.Min(1.0, kg / free), takenKg = 0.0, takenJ = 0.0;
        for (int n = 0; n < body.Mass.Length; n++)
        {
            if (!HoldsWater(body, n)) continue;
            double take = FreeWater(body, n) * share;
            if (take <= 0.0) continue;
            double j = JoulesOf(w, body, n, take);
            AddMass(w, body, n, -take, -j);
            body.WaterKg[n] -= take;
            takenKg += take;
            takenJ += j;
        }
        return (takenKg, takenJ);
    }

    /// <summary>Adds water carrying <paramref name="joules"/> to the watery layers, by their mass.</summary>
    public static void AddWater(World w, Body body, double kg, double joules)
    {
        double total = 0.0;
        for (int n = 0; n < body.Mass.Length; n++)
            if (HoldsWater(body, n)) total += body.Mass[n];
        if (total <= 0.0) { AddMass(w, body, 0, kg, joules); body.WaterKg[0] += kg; return; }
        for (int n = 0; n < body.Mass.Length; n++)
        {
            if (!HoldsWater(body, n)) continue;
            double f = body.Mass[n] / total;
            AddMass(w, body, n, kg * f, joules * f);
            body.WaterKg[n] += kg * f;
        }
    }

    /// <summary>Saturation vapour pressure of water, Pa (Magnus over water, good from -40 to 60 °C).</summary>
    public static double SaturationPressurePa(double temperatureK) => Vapour.SaturationPressurePa(temperatureK);

    /// <summary>Partial pressure of steam in a tile's gas, Pa.</summary>
    public static double SteamPressurePa(World w, WorldGrid grid, int i)
    {
        double free = Math.Max(grid.FreeVolume(i), WorldGrid.MinFreeVolumeM3);
        double mol = grid.GasMass[w.SteamSpecies][i] / (double)w.Table[w.Steam].MolarMass;
        return mol * Fluids.FluidSystem.GasConstant * grid.GasTemperatureAt(i) / free;
    }
}
