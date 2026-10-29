using System;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Core;

/// <summary>
/// The world as flat per-property arrays (structure of arrays), indexed by
/// <c>(z * Height + y) * Width + x</c>. One tile is a cube
/// <see cref="TileSizeM"/> on each side (half a metre). z = 0 is the lowest layer.
///
/// <para>
/// A tile has two kinds of contents, each with its own temperature when both are present:
/// a <b>condensed slot</b> holding at most one solid or liquid with an exact
/// mass (<see cref="Material"/>, <see cref="Mass"/>; <see cref="MaterialTable.Empty"/>
/// when there is none), and a <b>gas mixture</b> filling whatever volume the
/// condensed contents leave free (<see cref="GasMass"/>, one array per gas).
/// </para>
/// <para>
/// A condensed slot filled exactly to its material's density has
/// <see cref="FillFraction"/> 1. Less means room for gas (a puddle); more means
/// the material is squeezed in under pressure (freezing water expands), which
/// the structure layer turns into stress.
/// </para>
///
/// Each physics layer adds its own channel here. Keep channels as flat
/// primitive arrays: no per-tile objects, no dictionaries.
/// </summary>
public sealed class WorldGrid
{
    /// <summary>Edge length of one tile in metres. A person is about 4 tiles tall.</summary>
    public const double TileSizeM = 0.5;
    public const double TileFaceAreaM2 = TileSizeM * TileSizeM;
    public const double TileVolumeM3 = TileSizeM * TileSizeM * TileSizeM;

    /// <summary>Smallest free volume gas is considered to have, so pressure stays finite in a packed tile.</summary>
    public const double MinFreeVolumeM3 = 1e-6;

    public readonly int Width;
    public readonly int Height;
    public readonly int Depth;
    public readonly int Count;
    public readonly MaterialTable Materials;

    /// <summary>Condensed (solid or liquid) material id per tile; <see cref="MaterialTable.Empty"/> when none.</summary>
    public readonly ushort[] Material;

    /// <summary>
    /// Temperature per tile in kelvin: of its solid or liquid, or of its gas when it holds
    /// nothing else. Double, not float: a float can't resolve the tiny per-tick changes of
    /// slow conduction at room temperature, and the rounding silently destroys energy.
    /// </summary>
    public readonly double[] Temperature;

    /// <summary>
    /// Temperature of the gas in a tile that also holds a solid or liquid, K (see
    /// <see cref="HasTwoTemperatures"/>): a thin post in a plume, or a puddle under warm air,
    /// is not the temperature of the air around it, and the two trade heat through the
    /// surface between them. Read it through <see cref="GasTemperatureAt"/>; in any other
    /// tile the gas is simply at <see cref="Temperature"/>.
    /// </summary>
    public readonly double[] GasTemperature;

    /// <summary>Gas below this in a tile with a solid or liquid is too little to keep a temperature of its own, kg.</summary>
    public const double SeparateGasKg = 1e-7;

    /// <summary>Mass of the condensed material in kg (0 when the slot is empty).</summary>
    public readonly double[] Mass;

    /// <summary>
    /// Energy (J) the condensed material is holding part-way through melting
    /// (positive) or freezing (negative), while the temperature is pinned at
    /// the melting point.
    /// </summary>
    public readonly double[] LatentEnergy;

    /// <summary>Gas mass in kg per species per tile: <c>GasMass[species][tile]</c>, species as in <see cref="MaterialTable.GasIds"/>.</summary>
    public readonly double[][] GasMass;

    /// <summary>
    /// Air velocity in m/s through each tile's +x, +y and +z face (a staggered grid: the face
    /// between tile i and its +x neighbour is <c>AirVelocity[0][i]</c>). Positive is toward +x, +y
    /// or up. A top-layer tile's +z face opens onto the sky.
    /// </summary>
    public readonly double[][] AirVelocity;

    /// <summary>
    /// Which sites of a fuel tile have a flame: bit 0 is gas inside the tile itself, bits 1 to 6
    /// the faces toward -x, +x, -y, +y, -z and +z. A flame lives in the gas beside the face it
    /// burns on, and heats every fuel surface around that gas.
    /// </summary>
    public readonly byte[] Flame;

    /// <summary>
    /// Temperature of the thin surface layer on each face of a fuel tile, K, at
    /// <c>index * 6 + face</c> with faces ordered -x, +x, -y, +y, -z, +z; 0 where the surface is
    /// at the tile's own temperature. The layer's heat is part of the tile's: this only says how
    /// that heat is spread through it, which decides when a face catches fire.
    /// </summary>
    public readonly double[] Skin;

    /// <summary>
    /// How each solid tile holds together: bits 0 to 2 mark its bond to the +x, +y and +z
    /// neighbour as broken (a tile's bonds toward -x, -y and -z are kept by those neighbours),
    /// and <see cref="LooseBit"/> marks rubble that holds to nothing and only rests on what is
    /// below it. Zero means fully bonded.
    /// </summary>
    public readonly byte[] Structure;

    public const byte LooseBit = 8;

    /// <summary>Downward speed of a falling loose tile, m/s, and how far it has fallen since it last moved a whole tile, m.</summary>
    public readonly double[] FallSpeed;
    public readonly double[] FallTravel;

    /// <summary>
    /// Ether held in each tile, J: energy a caster can draw on and turn into heat, force or
    /// matter. The ether lives in space, not in the matter there, so it stays put when the
    /// matter moves; the matter only sets how much the tile holds at a given potential
    /// (see <see cref="EtherCapacity"/>).
    /// </summary>
    public readonly double[] Ether;

    /// <summary>
    /// The ether potential each place settles to, J/m³: set by its surroundings (rock, water,
    /// ley lines), so some places are rich in magic and others poor. The deep reservoir under
    /// the world refills or drains each tile toward it.
    /// </summary>
    public readonly double[] EtherAmbient;

    /// <summary>
    /// How fast the deep reservoir pulls each tile toward its ambient potential, per second:
    /// the inverse of the time it takes to close most of the gap. Ordinary ground refills over
    /// minutes (<see cref="DefaultEtherRefillPerSecond"/>); at a ley node the ether wells up
    /// within seconds.
    /// </summary>
    public readonly double[] EtherRefillRate;

    /// <summary>Ten minutes to close most of the gap: a drained place is mostly back within half an hour.</summary>
    public const double DefaultEtherRefillPerSecond = 1.0 / 600.0;

    /// <summary>
    /// Water held in the pores of a porous solid (soil), kg, and the energy it holds, J, counted as
    /// the audit counts water (and ice, less its latent heat, for any of it frozen). It is apart
    /// from the solid in the tile's condensed slot and keeps its own temperature, which the
    /// ecology layer brings to the solid's. Zero in any tile without pores.
    /// </summary>
    public readonly double[] PoreWater, PoreWaterJ;

    /// <summary>
    /// How near a plant in the tile is to dying, 0 (thriving) to 1 (dead): drought, frost and heat
    /// raise it, good days bring it back down. Meaningless where nothing grows.
    /// </summary>
    public readonly double[] PlantStress;

    public WorldGrid(int width, int height, int depth, MaterialTable materials)
    {
        if (width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Grid dimensions must be positive.");
        long count = (long)width * height * depth;
        if (count > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "Grid is too large.");

        Width = width;
        Height = height;
        Depth = depth;
        Count = (int)count;
        Materials = materials;
        Material = new ushort[Count];
        Temperature = new double[Count];
        GasTemperature = new double[Count];
        Mass = new double[Count];
        LatentEnergy = new double[Count];
        GasMass = new double[materials.GasCount][];
        for (int s = 0; s < GasMass.Length; s++)
            GasMass[s] = new double[Count];
        AirVelocity = new[] { new double[Count], new double[Count], new double[Count] };
        Flame = new byte[Count];
        Skin = new double[Count * 6];
        Structure = new byte[Count];
        FallSpeed = new double[Count];
        FallTravel = new double[Count];
        Ether = new double[Count];
        EtherAmbient = new double[Count];
        EtherRefillRate = new double[Count];
        Array.Fill(EtherRefillRate, DefaultEtherRefillPerSecond);
        PoreWater = new double[Count];
        PoreWaterJ = new double[Count];
        PlantStress = new double[Count];
    }

    public int Index(int x, int y, int z) => (z * Height + y) * Width + x;

    public (int X, int Y, int Z) Coordinates(int index)
    {
        int x = index % Width;
        int rest = index / Width;
        return (x, rest % Height, rest / Height);
    }

    public bool InBounds(int x, int y, int z) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height && (uint)z < (uint)Depth;

    /// <summary>Fill every tile completely with one material (a gas fills it at its reference density).</summary>
    public void Fill(ushort material, double temperatureK)
    {
        for (int i = 0; i < Count; i++)
            Place(i, material, temperatureK);
    }

    /// <summary>Fill one tile completely with a material, replacing everything in it.</summary>
    public void Set(int x, int y, int z, ushort material, double temperatureK) =>
        Place(Index(x, y, z), material, temperatureK);

    /// <summary>Fill one tile completely with a material, replacing everything in it.</summary>
    public void Place(int index, ushort material, double temperatureK)
    {
        foreach (var gas in GasMass) gas[index] = 0.0;
        int species = Materials.GasIndexOf(material);
        if (species >= 0)
        {
            SetCondensed(index, MaterialTable.Empty, 0.0);
            GasMass[species][index] = Materials[material].FullTileMass;
        }
        else
        {
            SetCondensed(index, material, Materials[material].FullTileMass);
        }
        Temperature[index] = temperatureK;
        GasTemperature[index] = temperatureK;
        Flame[index] = 0;
        Array.Clear(Skin, index * 6, 6);
        FallSpeed[index] = 0.0;
        FallTravel[index] = 0.0;
        PoreWater[index] = 0.0;
        PoreWaterJ[index] = 0.0;
        PlantStress[index] = 0.0;
        ResetBonds(index);
    }

    /// <summary>Makes a tile whole again: bonded to every solid neighbour and not loose.</summary>
    public void ResetBonds(int index)
    {
        Structure[index] = 0;
        int plane = Width * Height;
        if (index % Width > 0) Structure[index - 1] &= unchecked((byte)~1);
        if (index / Width % Height > 0) Structure[index - Width] &= unchecked((byte)~2);
        if (index >= plane) Structure[index - plane] &= unchecked((byte)~4);
    }

    /// <summary>Put an exact mass of a solid or liquid in a tile's condensed slot. Leaves its gas alone.</summary>
    public void SetCondensed(int index, ushort material, double massKg)
    {
        if (Materials.GasIndexOf(material) >= 0 && material != MaterialTable.Empty)
            throw new ArgumentException("A gas can't go in the condensed slot.", nameof(material));
        Material[index] = massKg > 0.0 ? material : MaterialTable.Empty;
        Mass[index] = massKg > 0.0 ? massKg : 0.0;
        LatentEnergy[index] = 0.0;
    }

    public bool HasCondensed(int index) => Material[index] != MaterialTable.Empty;

    /// <summary>Volume the condensed material occupies, m³ (may exceed the tile when squeezed).</summary>
    public double CondensedVolume(int index) =>
        HasCondensed(index) ? Mass[index] / Materials[Material[index]].Density : 0.0;

    /// <summary>Volume left for gas, m³.</summary>
    public double FreeVolume(int index) => Math.Max(0.0, TileVolumeM3 - CondensedVolume(index));

    public double TotalGasMass(int index)
    {
        double total = 0.0;
        foreach (var gas in GasMass) total += gas[index];
        return total;
    }

    /// <summary>Heat capacity of everything in a tile, J/K.</summary>
    public double HeatCapacity(int index) => CondensedHeatCapacity(index) + GasHeatCapacity(index);

    /// <summary>Heat capacity of a tile's solid or liquid, J/K.</summary>
    public double CondensedHeatCapacity(int index) => Mass[index] * Materials[Material[index]].SpecificHeat;

    /// <summary>Heat capacity of a tile's gas, J/K.</summary>
    public double GasHeatCapacity(int index)
    {
        double c = 0.0;
        var gasIds = Materials.GasIds;
        for (int s = 0; s < gasIds.Count; s++)
            c += GasMass[s][index] * Materials[gasIds[s]].SpecificHeat;
        return c;
    }

    /// <summary>Whether a tile holds both a solid or liquid and enough gas for each to have its own temperature.</summary>
    public bool HasTwoTemperatures(int index) => HasCondensed(index) && TotalGasMass(index) > SeparateGasKg;

    /// <summary>Temperature of the gas in a tile, K.</summary>
    public double GasTemperatureAt(int index) => HasTwoTemperatures(index) ? GasTemperature[index] : Temperature[index];

    /// <summary>Sets a tile to one temperature throughout, gas included.</summary>
    public void SetTemperature(int index, double temperatureK)
    {
        Temperature[index] = temperatureK;
        GasTemperature[index] = temperatureK;
    }

    /// <summary>How full the condensed slot is relative to its material's natural density (1 = exactly full).</summary>
    public double FillFraction(int index) =>
        HasCondensed(index) ? Mass[index] / Materials[Material[index]].FullTileMass : 0.0;

    /// <summary>
    /// How much ether a tile holds per unit of potential, m³: its open space counts as itself,
    /// its solid or liquid as its volume times the material's permittivity.
    /// </summary>
    public double EtherCapacity(int index)
    {
        double condensed = Math.Min(CondensedVolume(index), TileVolumeM3);
        double permittivity = HasCondensed(index) ? Materials[Material[index]].EtherPermittivity : 0.0;
        return condensed * permittivity + (TileVolumeM3 - condensed);
    }

    /// <summary>Ether potential of a tile, J/m³: its charge over its capacity.</summary>
    public double EtherPotential(int index) => Ether[index] / EtherCapacity(index);

    /// <summary>Breaks every bond a tile has to its neighbours, so it holds to nothing.</summary>
    public void BreakBonds(int index)
    {
        Structure[index] |= 7;
        int plane = Width * Height;
        if (index % Width > 0) Structure[index - 1] |= 1;
        if (index / Width % Height > 0) Structure[index - Width] |= 2;
        if (index >= plane) Structure[index - plane] |= 4;
    }

    /// <summary>
    /// The material a viewer should draw: the condensed material if it fills at
    /// least <paramref name="minFill"/> of the tile, otherwise the most massive gas.
    /// </summary>
    public ushort DisplayMaterial(int index, double minFill = 0.1)
    {
        if (HasCondensed(index) && FillFraction(index) >= minFill) return Material[index];
        int best = 0;
        for (int s = 1; s < GasMass.Length; s++)
            if (GasMass[s][index] > GasMass[best][index]) best = s;
        return GasMass[best][index] > 0.0 ? Materials.GasIds[best] : Material[index];
    }

    public WorldGrid Clone()
    {
        var copy = new WorldGrid(Width, Height, Depth, Materials);
        Array.Copy(Material, copy.Material, Count);
        Array.Copy(Temperature, copy.Temperature, Count);
        Array.Copy(GasTemperature, copy.GasTemperature, Count);
        Array.Copy(Mass, copy.Mass, Count);
        Array.Copy(LatentEnergy, copy.LatentEnergy, Count);
        for (int s = 0; s < GasMass.Length; s++)
            Array.Copy(GasMass[s], copy.GasMass[s], Count);
        for (int a = 0; a < 3; a++)
            Array.Copy(AirVelocity[a], copy.AirVelocity[a], Count);
        Array.Copy(Flame, copy.Flame, Count);
        Array.Copy(Skin, copy.Skin, Skin.Length);
        Array.Copy(Structure, copy.Structure, Count);
        Array.Copy(FallSpeed, copy.FallSpeed, Count);
        Array.Copy(FallTravel, copy.FallTravel, Count);
        Array.Copy(Ether, copy.Ether, Count);
        Array.Copy(EtherAmbient, copy.EtherAmbient, Count);
        Array.Copy(EtherRefillRate, copy.EtherRefillRate, Count);
        Array.Copy(PoreWater, copy.PoreWater, Count);
        Array.Copy(PoreWaterJ, copy.PoreWaterJ, Count);
        Array.Copy(PlantStress, copy.PlantStress, Count);
        return copy;
    }
}
