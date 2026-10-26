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

    /// <summary>20 °C.</summary>
    public const float RoomTemperatureK = 293.15f;

    public static readonly MaterialDef[] All =
    {
        // id 0 must be air: a zeroed grid is an empty world.
        new()
        {
            Key = Air, Name = "Air", Phase = Phase.Gas, Glyph = ' ', Color = 0x101018,
            Density = 1.2f, SpecificHeat = 1005f, ThermalConductivity = 0.026f,
            EthericCapacity = 1f, ArcaneConductivity = 0.1f,
        },
        new()
        {
            Key = Stone, Name = "Granite", Phase = Phase.Solid, Glyph = '#', Color = 0x6E6A66,
            Density = 2700f, SpecificHeat = 790f, ThermalConductivity = 2.8f,
            TensileStrength = 10f, MeltingPoint = 1500f, LatentHeatFusion = 400_000f,
            MeltsInto = Magma,
            EthericCapacity = 50f, ArcaneConductivity = 0.3f,
        },
        new()
        {
            Key = Soil, Name = "Soil", Phase = Phase.Solid, Glyph = ',', Color = 0x5A4128,
            Density = 1500f, SpecificHeat = 800f, ThermalConductivity = 0.5f,
            TensileStrength = 0.01f,
            EthericCapacity = 40f, ArcaneConductivity = 0.4f,
        },
        new()
        {
            Key = Wood, Name = "Oak", Phase = Phase.Solid, Glyph = '=', Color = 0x8B5A2B,
            Density = 750f, SpecificHeat = 2000f, ThermalConductivity = 0.17f,
            TensileStrength = 40f, IgnitionPoint = 573f,
            EthericCapacity = 30f, ArcaneConductivity = 0.5f,
        },
        new()
        {
            // Cold iron disrupts magic: low capacity, poor conduction, some insulation.
            Key = Iron, Name = "Iron", Phase = Phase.Solid, Glyph = 'I', Color = 0x8C96A0,
            Density = 7870f, SpecificHeat = 450f, ThermalConductivity = 80f,
            TensileStrength = 350f, MeltingPoint = 1811f, LatentHeatFusion = 247_000f,
            MeltsInto = MoltenIron,
            EthericCapacity = 10f, ArcaneConductivity = 0.05f, AntiMagicInsulation = 0.3f,
        },
        new()
        {
            // Lead is the classic shield: almost blocks charge entirely.
            Key = Lead, Name = "Lead", Phase = Phase.Solid, Glyph = 'L', Color = 0x50566B,
            Density = 11_340f, SpecificHeat = 129f, ThermalConductivity = 35f,
            TensileStrength = 18f, MeltingPoint = 600.6f, LatentHeatFusion = 23_000f,
            MeltsInto = MoltenLead,
            EthericCapacity = 5f, ArcaneConductivity = 0.02f, AntiMagicInsulation = 0.9f,
        },
        new()
        {
            Key = Water, Name = "Water", Phase = Phase.Liquid, Glyph = '~', Color = 0x2860C8,
            Density = 1000f, SpecificHeat = 4186f, ThermalConductivity = 0.6f,
            MeltingPoint = 273.15f, BoilingPoint = 373.15f,
            LatentHeatFusion = 334_000f, LatentHeatVaporization = 2_257_000f,
            FreezesInto = Ice, BoilsInto = Steam,
            EthericCapacity = 20f, ArcaneConductivity = 0.8f,
        },
        new()
        {
            Key = Ice, Name = "Ice", Phase = Phase.Solid, Glyph = '*', Color = 0xB4DCF0,
            Density = 917f, SpecificHeat = 2100f, ThermalConductivity = 2.2f,
            TensileStrength = 1f, MeltingPoint = 273.15f, LatentHeatFusion = 334_000f,
            MeltsInto = Water,
            EthericCapacity = 25f, ArcaneConductivity = 0.6f,
        },
        new()
        {
            Key = Steam, Name = "Steam", Phase = Phase.Gas, Glyph = '"', Color = 0xD2D7DC,
            Density = 0.6f, SpecificHeat = 2010f, ThermalConductivity = 0.025f,
            BoilingPoint = 373.15f, LatentHeatVaporization = 2_257_000f,
            CondensesInto = Water,
            EthericCapacity = 2f, ArcaneConductivity = 0.3f,
        },
        new()
        {
            // Boiling lead (2022 K) has no vapour material yet, so it stays liquid above that.
            Key = MoltenLead, Name = "Molten lead", Phase = Phase.Liquid, Glyph = 'l', Color = 0xA8B0C8,
            Density = 10_660f, SpecificHeat = 142f, ThermalConductivity = 16f,
            MeltingPoint = 600.6f, LatentHeatFusion = 23_000f,
            FreezesInto = Lead,
            EthericCapacity = 5f, ArcaneConductivity = 0.05f, AntiMagicInsulation = 0.9f,
        },
        new()
        {
            Key = MoltenIron, Name = "Molten iron", Phase = Phase.Liquid, Glyph = 'i', Color = 0xFF8C1A,
            Density = 6_980f, SpecificHeat = 820f, ThermalConductivity = 40f,
            MeltingPoint = 1811f, LatentHeatFusion = 247_000f,
            FreezesInto = Iron,
            EthericCapacity = 10f, ArcaneConductivity = 0.1f, AntiMagicInsulation = 0.3f,
        },
        new()
        {
            Key = Magma, Name = "Magma", Phase = Phase.Liquid, Glyph = '%', Color = 0xE0401A,
            Density = 2_600f, SpecificHeat = 1000f, ThermalConductivity = 1.5f,
            MeltingPoint = 1500f, LatentHeatFusion = 400_000f,
            FreezesInto = Stone,
            EthericCapacity = 60f, ArcaneConductivity = 0.4f,
        },
    };

    public static MaterialTable CreateTable() => new(All);
}
