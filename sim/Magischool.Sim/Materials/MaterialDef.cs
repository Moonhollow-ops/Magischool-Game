namespace Magischool.Sim.Materials;

public enum Phase : byte
{
    Solid,
    Liquid,
    Gas,
}

/// <summary>
/// Constants for one material. Real-world SI units throughout; the arcane
/// constants are invented but keep the same "per tile" scale.
/// A threshold that never applies is <see cref="float.PositiveInfinity"/>.
/// </summary>
public sealed record MaterialDef
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required Phase Phase { get; init; }

    // Display only. The sim never reads these.
    public char Glyph { get; init; } = '?';
    /// <summary>0xRRGGBB.</summary>
    public uint Color { get; init; } = 0xFF00FF;

    // Mundane physics
    /// <summary>kg/m^3</summary>
    public required float Density { get; init; }
    /// <summary>J/(kg·K)</summary>
    public required float SpecificHeat { get; init; }
    /// <summary>W/(m·K)</summary>
    public required float ThermalConductivity { get; init; }
    /// <summary>MPa. Zero for fluids.</summary>
    public float TensileStrength { get; init; }
    /// <summary>K. Solid becomes liquid at or above this.</summary>
    public float MeltingPoint { get; init; } = float.PositiveInfinity;
    /// <summary>K. Liquid becomes gas at or above this.</summary>
    public float BoilingPoint { get; init; } = float.PositiveInfinity;
    /// <summary>K. Ignites in the presence of oxygen at or above this.</summary>
    public float IgnitionPoint { get; init; } = float.PositiveInfinity;
    /// <summary>J/kg absorbed melting, released freezing.</summary>
    public float LatentHeatFusion { get; init; }
    /// <summary>J/kg absorbed boiling, released condensing.</summary>
    public float LatentHeatVaporization { get; init; }

    // Phase links by key, resolved by the table. Null means no transition.
    public string? MeltsInto { get; init; }
    public string? FreezesInto { get; init; }
    public string? BoilsInto { get; init; }
    public string? CondensesInto { get; init; }

    // Arcane physics
    /// <summary>Max etheric charge (Ψ) a tile holds before it ruptures.</summary>
    public float EthericCapacity { get; init; }
    /// <summary>0..1, how freely charge flows to neighbours per second.</summary>
    public float ArcaneConductivity { get; init; }
    /// <summary>0..1, fraction of passing charge a tile blocks.</summary>
    public float AntiMagicInsulation { get; init; }

    /// <summary>Mass of one tile filled exactly to this material's density, kg.</summary>
    public double FullTileMass => Density * Core.WorldGrid.TileVolumeM3;

    /// <summary>Heat capacity of one full tile, J/K.</summary>
    public double TileHeatCapacity => FullTileMass * SpecificHeat;
}
