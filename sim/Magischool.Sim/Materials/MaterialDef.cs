using System;
using System.Collections.Generic;

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
    /// <summary>kg/mol. Gases only: sets their pressure through the ideal gas law.</summary>
    public float MolarMass { get; init; }
    /// <summary>Pa·s. Liquids only: how readily they flow (water 0.001, magma ~100).</summary>
    public float Viscosity { get; init; }
    /// <summary>MPa. Zero for fluids.</summary>
    public float TensileStrength { get; init; }
    /// <summary>MPa. Solids only: the stress at which the material crushes.</summary>
    public float CompressiveStrength { get; init; }
    /// <summary>
    /// GPa. Solids only: stiffness, how little the material gives under load. A solid with
    /// none can't bond to its neighbours and lies loose, like gravel.
    /// </summary>
    public float YoungsModulus { get; init; }
    /// <summary>
    /// K. A solid keeps its full strength up to <see cref="StrengthLossStartK"/> and loses it
    /// evenly until none is left at <see cref="StrengthLossEndK"/> (wood chars, iron softens).
    /// </summary>
    public float StrengthLossStartK { get; init; } = float.PositiveInfinity;
    public float StrengthLossEndK { get; init; } = float.PositiveInfinity;
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

    // Combustion
    /// <summary>
    /// J released per kg burned in air, with the water in the products left as vapour
    /// (the lower heating value). Zero for anything that doesn't burn. Burning needs
    /// <see cref="IgnitionPoint"/> and oxygen.
    /// </summary>
    public float HeatOfCombustion { get; init; }
    /// <summary>kg of oxygen used per kg burned.</summary>
    public float OxygenPerKg { get; init; }
    /// <summary>
    /// What one kg of this material plus <see cref="OxygenPerKg"/> of oxygen becomes, by key, in
    /// kg. The masses must add up to 1 + <see cref="OxygenPerKg"/>. Products are gases.
    /// </summary>
    public IReadOnlyList<CombustionProduct> CombustionProducts { get; init; } = Array.Empty<CombustionProduct>();
    /// <summary>
    /// For a gas that is a fixed mixture containing oxygen (air): the mass fraction that is
    /// oxygen, and the key of the gas left when that oxygen is used up.
    /// </summary>
    public float OxygenFraction { get; init; }
    public string? OxygenRemainder { get; init; }

    // Arcane physics (layer 7). The ether is a field of energy that pervades space; matter only
    // changes how much of it a place holds and how freely it moves.
    /// <summary>
    /// How much ether a cubic metre of this material holds per unit of potential, relative to
    /// open space (1). Charge in a tile is its potential (J/m³) times its capacity.
    /// </summary>
    public float EtherPermittivity { get; init; } = 1f;
    /// <summary>
    /// m²/s. How freely ether flows through the material: the flow across a face is this times
    /// the face's area times the drop in potential per metre. Cold iron and lead nearly stop it.
    /// </summary>
    public float EtherConductivity { get; init; }
    /// <summary>
    /// J/m³. Solids only: the ether potential at which the material ruptures, its whole charge
    /// discharging into it as heat and shattering it.
    /// </summary>
    public float EtherBreakdown { get; init; } = float.PositiveInfinity;
    /// <summary>
    /// J/m³. For crystallised ether (etherium): a tile holding it is tied to the etheric plane and
    /// settles to this potential, the plane refilling it faster the more of it there is (a full
    /// tile as fast as a ley node). Zero for ordinary matter.
    /// </summary>
    public float EtherWellPotential { get; init; }

    /// <summary>Mass of one tile filled exactly to this material's density, kg.</summary>
    public double FullTileMass => Density * Core.WorldGrid.TileVolumeM3;

    /// <summary>Heat capacity of one full tile, J/K.</summary>
    public double TileHeatCapacity => FullTileMass * SpecificHeat;
}

/// <summary>One product of burning: a gas, by key, and how many kg of it per kg of fuel.</summary>
public readonly record struct CombustionProduct(string Key, float KgPerKgFuel);
