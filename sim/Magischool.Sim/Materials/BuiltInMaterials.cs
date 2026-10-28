namespace Magischool.Sim.Materials;

/// <summary>
/// The starting material set. Mundane values are real-world approximations
/// at room temperature; arcane values are design numbers.
/// Append new materials at the end of <see cref="All"/> (ids are positional).
/// </summary>
public static class BuiltInMaterials
{
    public const string Air = "air";
    public const string Stone = "stone";
    public const string Soil = "soil";
    public const string Wood = "wood";
    public const string Iron = "iron";
    public const string Lead = "lead";
    public const string Water = "water";
    public const string Ice = "ice";
    public const string Steam = "steam";
    public const string MoltenLead = "molten_lead";
    public const string MoltenIron = "molten_iron";
    public const string Magma = "magma";
    public const string Nitrogen = "nitrogen";
    public const string CarbonDioxide = "carbon_dioxide";
    public const string Smoke = "smoke";
    public const string Crystal = "crystal";
    public const string MoltenCrystal = "molten_crystal";
    public const string Etherium = "etherium";
    public const string Bread = "bread";
    public const string Blood = "blood";
    public const string FrozenBlood = "frozen_blood";

    /// <summary>Mass fraction of oxygen in dry air.</summary>
    public const float AirOxygenFraction = 0.2314f;

    /// <summary>20 °C.</summary>
    public const float RoomTemperatureK = 293.15f;

    public static readonly MaterialDef[] All =
    {
        // id 0 must be a gas: it doubles as "no solid or liquid here" in a tile's condensed slot.
        new()
        {
            Key = Air, Name = "Air", Phase = Phase.Gas, Glyph = ' ', Color = 0x101018,
            // Air is one gas of fixed makeup: 23.14% oxygen by mass, the rest nitrogen and argon.
            // Burning takes its oxygen and leaves the rest behind as nitrogen.
            Density = 1.2f, SpecificHeat = 1005f, ThermalConductivity = 0.026f, MolarMass = 0.028965f,
            OxygenFraction = AirOxygenFraction, OxygenRemainder = Nitrogen,
        },
        new()
        {
            Key = Stone, Name = "Granite", Phase = Phase.Solid, Glyph = '#', Color = 0x6E6A66,
            Density = 2700f, SpecificHeat = 790f, ThermalConductivity = 2.8f,
            TensileStrength = 10f, CompressiveStrength = 130f, YoungsModulus = 50f,
            StrengthLossStartK = 873f, StrengthLossEndK = 1473f, MeltingPoint = 1500f, LatentHeatFusion = 400_000f,
            MeltsInto = Magma,
            EtherPermittivity = 4f, EtherConductivity = 0.4f, EtherBreakdown = 5e8f,
        },
        new()
        {
            Key = Soil, Name = "Soil", Phase = Phase.Solid, Glyph = ',', Color = 0x5A4128,
            Density = 1500f, SpecificHeat = 800f, ThermalConductivity = 0.5f,
            TensileStrength = 0.01f, CompressiveStrength = 0.1f, YoungsModulus = 0.05f,
            EtherPermittivity = 6f, EtherConductivity = 0.3f, EtherBreakdown = 5e8f,
        },
        new()
        {
            Key = Wood, Name = "Oak", Phase = Phase.Solid, Glyph = '=', Color = 0x8B5A2B,
            Density = 750f, SpecificHeat = 2000f, ThermalConductivity = 0.17f,
            TensileStrength = 40f, CompressiveStrength = 40f, YoungsModulus = 11f,
            // Loses strength as it dries and pyrolyses; by its ignition point it is char.
            StrengthLossStartK = 373f, StrengthLossEndK = 573f, IgnitionPoint = 573f,
            // Burns as cellulose: C6H10O5 + 6 O2 -> 6 CO2 + 5 H2O, with 1.5% leaving as soot and ash.
            HeatOfCombustion = 15_000_000f, OxygenPerKg = 1.1672f,
            CombustionProducts = new CombustionProduct[]
            {
                new(CarbonDioxide, 1.6056f), new(Steam, 0.5466f), new(Smoke, 0.015f),
            },
            EtherPermittivity = 5f, EtherConductivity = 0.2f, EtherBreakdown = 2e8f,
        },
        new()
        {
            // Cold iron disrupts magic: it holds little ether and all but stops its flow.
            Key = Iron, Name = "Iron", Phase = Phase.Solid, Glyph = 'I', Color = 0x8C96A0,
            Density = 7870f, SpecificHeat = 450f, ThermalConductivity = 80f,
            TensileStrength = 350f, CompressiveStrength = 350f, YoungsModulus = 200f,
            StrengthLossStartK = 673f, StrengthLossEndK = 1473f, MeltingPoint = 1811f, LatentHeatFusion = 247_000f,
            MeltsInto = MoltenIron,
            EtherPermittivity = 0.5f, EtherConductivity = 0.0005f,
        },
        new()
        {
            // Lead is the classic shield: ether barely moves through it at all.
            Key = Lead, Name = "Lead", Phase = Phase.Solid, Glyph = 'L', Color = 0x50566B,
            Density = 11_340f, SpecificHeat = 129f, ThermalConductivity = 35f,
            TensileStrength = 18f, CompressiveStrength = 18f, YoungsModulus = 16f,
            StrengthLossStartK = 373f, StrengthLossEndK = 600.6f, MeltingPoint = 600.6f, LatentHeatFusion = 23_000f,
            MeltsInto = MoltenLead,
            EtherPermittivity = 0.2f, EtherConductivity = 0.0001f,
        },
        new()
        {
            Key = Water, Name = "Water", Phase = Phase.Liquid, Glyph = '~', Color = 0x2860C8,
            Density = 1000f, SpecificHeat = 4186f, ThermalConductivity = 0.6f, Viscosity = 0.001f,
            MeltingPoint = 273.15f, BoilingPoint = 373.15f,
            LatentHeatFusion = 334_000f, LatentHeatVaporization = 2_257_000f,
            FreezesInto = Ice, BoilsInto = Steam,
            Potable = true,
            EtherPermittivity = 10f, EtherConductivity = 0.5f,
        },
        new()
        {
            Key = Ice, Name = "Ice", Phase = Phase.Solid, Glyph = '*', Color = 0xB4DCF0,
            Density = 917f, SpecificHeat = 2100f, ThermalConductivity = 2.2f,
            TensileStrength = 1f, CompressiveStrength = 5f, YoungsModulus = 9f,
            StrengthLossStartK = 253f, StrengthLossEndK = 273.15f, MeltingPoint = 273.15f, LatentHeatFusion = 334_000f,
            MeltsInto = Water,
            EtherPermittivity = 8f, EtherConductivity = 0.4f, EtherBreakdown = 1e8f,
        },
        new()
        {
            Key = Steam, Name = "Steam", Phase = Phase.Gas, Glyph = '"', Color = 0xD2D7DC,
            Density = 0.6f, SpecificHeat = 2010f, ThermalConductivity = 0.025f, MolarMass = 0.018015f,
            BoilingPoint = 373.15f, LatentHeatVaporization = 2_257_000f,
            CondensesInto = Water,
        },
        new()
        {
            // Boiling lead (2022 K) has no vapour material yet, so it stays liquid above that.
            Key = MoltenLead, Name = "Molten lead", Phase = Phase.Liquid, Glyph = 'l', Color = 0xA8B0C8,
            Density = 10_660f, SpecificHeat = 142f, ThermalConductivity = 16f, Viscosity = 0.0019f,
            MeltingPoint = 600.6f, LatentHeatFusion = 23_000f,
            FreezesInto = Lead,
            EtherPermittivity = 0.2f, EtherConductivity = 0.0001f,
        },
        new()
        {
            Key = MoltenIron, Name = "Molten iron", Phase = Phase.Liquid, Glyph = 'i', Color = 0xFF8C1A,
            Density = 6_980f, SpecificHeat = 820f, ThermalConductivity = 40f, Viscosity = 0.006f,
            MeltingPoint = 1811f, LatentHeatFusion = 247_000f,
            FreezesInto = Iron,
            EtherPermittivity = 0.5f, EtherConductivity = 0.0005f,
        },
        new()
        {
            Key = Magma, Name = "Magma", Phase = Phase.Liquid, Glyph = '%', Color = 0xE0401A,
            Density = 2_600f, SpecificHeat = 1000f, ThermalConductivity = 1.5f, Viscosity = 100f,
            MeltingPoint = 1500f, LatentHeatFusion = 400_000f,
            FreezesInto = Stone,
            EtherPermittivity = 6f, EtherConductivity = 1f,
        },
        new()
        {
            // What air leaves behind when its oxygen burns away (with the argon).
            Key = Nitrogen, Name = "Nitrogen", Phase = Phase.Gas, Glyph = ' ', Color = 0x101820,
            Density = 1.165f, SpecificHeat = 1040f, ThermalConductivity = 0.026f, MolarMass = 0.02816f,
        },
        new()
        {
            Key = CarbonDioxide, Name = "Carbon dioxide", Phase = Phase.Gas, Glyph = ' ', Color = 0x181010,
            Density = 1.84f, SpecificHeat = 844f, ThermalConductivity = 0.0166f, MolarMass = 0.04401f,
        },
        new()
        {
            // Soot and fly ash carried in the air. The particles are so heavy per "molecule" that
            // they add almost nothing to pressure (a huge molar mass), but they do weigh the air down.
            Key = Smoke, Name = "Smoke", Phase = Phase.Gas, Glyph = '.', Color = 0x3C3C3C,
            Density = 1.2f, SpecificHeat = 700f, ThermalConductivity = 0.026f, MolarMass = 1.0f,
        },
        new()
        {
            // A quartz that drinks in ether: it holds forty times what open space does and passes it
            // freely, but past its breakdown potential it ruptures. Physical constants are quartz's.
            Key = Crystal, Name = "Aether crystal", Phase = Phase.Solid, Glyph = 'C', Color = 0xB48CF0,
            Density = 2650f, SpecificHeat = 740f, ThermalConductivity = 8f,
            TensileStrength = 50f, CompressiveStrength = 1100f, YoungsModulus = 70f,
            StrengthLossStartK = 1273f, StrengthLossEndK = 1873f, MeltingPoint = 1986f, LatentHeatFusion = 156_000f,
            MeltsInto = MoltenCrystal,
            EtherPermittivity = 40f, EtherConductivity = 2f, EtherBreakdown = 1e7f,
        },
        new()
        {
            // Molten silica is far stiffer than this (around 10^7 Pa·s); the fluids layer is only
            // proven up to magma's 100, so for now it flows like magma.
            Key = MoltenCrystal, Name = "Molten crystal", Phase = Phase.Liquid, Glyph = 'c', Color = 0xF0B4FF,
            Density = 2200f, SpecificHeat = 1400f, ThermalConductivity = 2f, Viscosity = 100f,
            MeltingPoint = 1986f, LatentHeatFusion = 156_000f,
            FreezesInto = Crystal,
            EtherPermittivity = 40f, EtherConductivity = 2f,
        },
        new()
        {
            // Ether crystallised into matter: a pale blue stone that ties the place it lies in to the
            // etheric plane, enriching the field around it, and that a caster can burn for power at
            // Spellcasting.EtherPerKg. Its mundane constants are a dense glassy mineral's; it has no
            // liquid form, and since it is ether already it never breaks down.
            Key = Etherium, Name = "Etherium", Phase = Phase.Solid, Glyph = 'E', Color = 0x8CDCFF,
            Density = 3200f, SpecificHeat = 800f, ThermalConductivity = 5f,
            TensileStrength = 30f, CompressiveStrength = 300f, YoungsModulus = 60f,
            EtherPermittivity = 100f, EtherConductivity = 3f, EtherWellPotential = 3e7f,
        },
        new()
        {
            // Food: 60% starch and 40% water by mass, so it burns as cellulose does, and a body
            // digests its starch into glycogen (see Bodies.Metabolism). Loose crumbs, no strength.
            Key = Bread, Name = "Bread", Phase = Phase.Solid, Glyph = 'b', Color = 0xD8A860,
            Density = 280f, SpecificHeat = 2700f, ThermalConductivity = 0.2f,
            StrengthLossStartK = 373f, StrengthLossEndK = 573f, IgnitionPoint = 573f,
            // 0.6 (C6H10O5 + 6 O2 -> 6 CO2 + 5 H2O), the bread's own water leaving as steam.
            HeatOfCombustion = 8_700_000f, OxygenPerKg = 0.71045f,
            CombustionProducts = new CombustionProduct[] { new(CarbonDioxide, 0.97712f), new(Steam, 0.73333f) },
            Edible = true,
            EtherPermittivity = 5f, EtherConductivity = 0.2f, EtherBreakdown = 2e8f,
        },
        new()
        {
            // Spilled blood. Thermally it is taken as water, so its energy is counted exactly as
            // water's and the two can share a puddle; only density and colour differ. It freezes,
            // but doesn't yet boil (that needs a vapour that condenses back into blood).
            Key = Blood, Name = "Blood", Phase = Phase.Liquid, Glyph = '~', Color = 0x8A0A0A,
            Density = 1060f, SpecificHeat = 4186f, ThermalConductivity = 0.5f, Viscosity = 0.004f,
            MeltingPoint = 273.15f, LatentHeatFusion = 334_000f,
            FreezesInto = FrozenBlood,
            EtherPermittivity = 10f, EtherConductivity = 0.5f,
        },
        new()
        {
            Key = FrozenBlood, Name = "Frozen blood", Phase = Phase.Solid, Glyph = '*', Color = 0x5A0A14,
            Density = 1000f, SpecificHeat = 2100f, ThermalConductivity = 2.2f,
            TensileStrength = 1f, CompressiveStrength = 5f, YoungsModulus = 9f,
            StrengthLossStartK = 253f, StrengthLossEndK = 273.15f, MeltingPoint = 273.15f, LatentHeatFusion = 334_000f,
            MeltsInto = Blood,
            EtherPermittivity = 8f, EtherConductivity = 0.4f, EtherBreakdown = 1e8f,
        },
    };

    public static MaterialTable CreateTable() => new(All);
}
