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
            EthericCapacity = 1f, ArcaneConductivity = 0.1f,
        },
        new()
        {
            Key = Stone, Name = "Granite", Phase = Phase.Solid, Glyph = '#', Color = 0x6E6A66,
            Density = 2700f, SpecificHeat = 790f, ThermalConductivity = 2.8f,
            TensileStrength = 10f, CompressiveStrength = 130f, YoungsModulus = 50f,
            StrengthLossStartK = 873f, StrengthLossEndK = 1473f, MeltingPoint = 1500f, LatentHeatFusion = 400_000f,
            MeltsInto = Magma,
            EthericCapacity = 50f, ArcaneConductivity = 0.3f,
        },
        new()
        {
            Key = Soil, Name = "Soil", Phase = Phase.Solid, Glyph = ',', Color = 0x5A4128,
            Density = 1500f, SpecificHeat = 800f, ThermalConductivity = 0.5f,
            TensileStrength = 0.01f, CompressiveStrength = 0.1f, YoungsModulus = 0.05f,
            EthericCapacity = 40f, ArcaneConductivity = 0.4f,
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
            EthericCapacity = 30f, ArcaneConductivity = 0.5f,
        },
        new()
        {
            // Cold iron disrupts magic: low capacity, poor conduction, some insulation.
            Key = Iron, Name = "Iron", Phase = Phase.Solid, Glyph = 'I', Color = 0x8C96A0,
            Density = 7870f, SpecificHeat = 450f, ThermalConductivity = 80f,
            TensileStrength = 350f, CompressiveStrength = 350f, YoungsModulus = 200f,
            StrengthLossStartK = 673f, StrengthLossEndK = 1473f, MeltingPoint = 1811f, LatentHeatFusion = 247_000f,
            MeltsInto = MoltenIron,
            EthericCapacity = 10f, ArcaneConductivity = 0.05f, AntiMagicInsulation = 0.3f,
        },
        new()
        {
            // Lead is the classic shield: almost blocks charge entirely.
            Key = Lead, Name = "Lead", Phase = Phase.Solid, Glyph = 'L', Color = 0x50566B,
            Density = 11_340f, SpecificHeat = 129f, ThermalConductivity = 35f,
            TensileStrength = 18f, CompressiveStrength = 18f, YoungsModulus = 16f,
            StrengthLossStartK = 373f, StrengthLossEndK = 600.6f, MeltingPoint = 600.6f, LatentHeatFusion = 23_000f,
            MeltsInto = MoltenLead,
            EthericCapacity = 5f, ArcaneConductivity = 0.02f, AntiMagicInsulation = 0.9f,
        },
        new()
        {
            Key = Water, Name = "Water", Phase = Phase.Liquid, Glyph = '~', Color = 0x2860C8,
            Density = 1000f, SpecificHeat = 4186f, ThermalConductivity = 0.6f, Viscosity = 0.001f,
            MeltingPoint = 273.15f, BoilingPoint = 373.15f,
            LatentHeatFusion = 334_000f, LatentHeatVaporization = 2_257_000f,
            FreezesInto = Ice, BoilsInto = Steam,
            EthericCapacity = 20f, ArcaneConductivity = 0.8f,
        },
        new()
        {
            Key = Ice, Name = "Ice", Phase = Phase.Solid, Glyph = '*', Color = 0xB4DCF0,
            Density = 917f, SpecificHeat = 2100f, ThermalConductivity = 2.2f,
            TensileStrength = 1f, CompressiveStrength = 5f, YoungsModulus = 9f,
            StrengthLossStartK = 253f, StrengthLossEndK = 273.15f, MeltingPoint = 273.15f, LatentHeatFusion = 334_000f,
            MeltsInto = Water,
            EthericCapacity = 25f, ArcaneConductivity = 0.6f,
        },
        new()
        {
            Key = Steam, Name = "Steam", Phase = Phase.Gas, Glyph = '"', Color = 0xD2D7DC,
            Density = 0.6f, SpecificHeat = 2010f, ThermalConductivity = 0.025f, MolarMass = 0.018015f,
            BoilingPoint = 373.15f, LatentHeatVaporization = 2_257_000f,
            CondensesInto = Water,
            EthericCapacity = 2f, ArcaneConductivity = 0.3f,
        },
        new()
        {
            // Boiling lead (2022 K) has no vapour material yet, so it stays liquid above that.
            Key = MoltenLead, Name = "Molten lead", Phase = Phase.Liquid, Glyph = 'l', Color = 0xA8B0C8,
            Density = 10_660f, SpecificHeat = 142f, ThermalConductivity = 16f, Viscosity = 0.0019f,
            MeltingPoint = 600.6f, LatentHeatFusion = 23_000f,
            FreezesInto = Lead,
            EthericCapacity = 5f, ArcaneConductivity = 0.05f, AntiMagicInsulation = 0.9f,
        },
        new()
        {
            Key = MoltenIron, Name = "Molten iron", Phase = Phase.Liquid, Glyph = 'i', Color = 0xFF8C1A,
            Density = 6_980f, SpecificHeat = 820f, ThermalConductivity = 40f, Viscosity = 0.006f,
            MeltingPoint = 1811f, LatentHeatFusion = 247_000f,
            FreezesInto = Iron,
            EthericCapacity = 10f, ArcaneConductivity = 0.1f, AntiMagicInsulation = 0.3f,
        },
        new()
        {
            Key = Magma, Name = "Magma", Phase = Phase.Liquid, Glyph = '%', Color = 0xE0401A,
            Density = 2_600f, SpecificHeat = 1000f, ThermalConductivity = 1.5f, Viscosity = 100f,
            MeltingPoint = 1500f, LatentHeatFusion = 400_000f,
            FreezesInto = Stone,
            EthericCapacity = 60f, ArcaneConductivity = 0.4f,
        },
        new()
        {
            // What air leaves behind when its oxygen burns away (with the argon).
            Key = Nitrogen, Name = "Nitrogen", Phase = Phase.Gas, Glyph = ' ', Color = 0x101820,
            Density = 1.165f, SpecificHeat = 1040f, ThermalConductivity = 0.026f, MolarMass = 0.02816f,
            EthericCapacity = 1f, ArcaneConductivity = 0.1f,
        },
        new()
        {
            Key = CarbonDioxide, Name = "Carbon dioxide", Phase = Phase.Gas, Glyph = ' ', Color = 0x181010,
            Density = 1.84f, SpecificHeat = 844f, ThermalConductivity = 0.0166f, MolarMass = 0.04401f,
            EthericCapacity = 1f, ArcaneConductivity = 0.1f,
        },
        new()
        {
            // Soot and fly ash carried in the air. The particles are so heavy per "molecule" that
            // they add almost nothing to pressure (a huge molar mass), but they do weigh the air down.
            Key = Smoke, Name = "Smoke", Phase = Phase.Gas, Glyph = '.', Color = 0x3C3C3C,
            Density = 1.2f, SpecificHeat = 700f, ThermalConductivity = 0.026f, MolarMass = 1.0f,
            EthericCapacity = 1f, ArcaneConductivity = 0.1f,
        },
    };

    public static MaterialTable CreateTable() => new(All);
}
