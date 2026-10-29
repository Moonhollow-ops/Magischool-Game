using System;
using System.IO;
using Magischool.Sim.Core;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Ecology;

/// <summary>
/// Puts a seedling of a plant material into tile (X, Y, Z), if it is open air over soil: Kg of
/// fresh plant at the air's temperature. Like <see cref="SetMaterial"/>, this makes mass and
/// energy from nothing: it is how the world is planted, not something that happens in it.
/// </summary>
public sealed record PlantSeedling(int X, int Y, int Z, string Plant, double Kg = 0.0) : WorldCommand
{
    internal const byte TagValue = 17;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var grid = sim.Grid;
        var table = sim.Materials;
        if (!grid.InBounds(X, Y, Z) || Z == 0 || !table.TryGetId(Plant, out ushort m) || !table[m].IsPlant) return;
        int i = grid.Index(X, Y, Z);
        if (grid.HasCondensed(i) || EcologySystem.SoilVolume(grid, i - grid.Width * grid.Height) <= 0.0) return;
        double kg = Kg > 0.0 ? Kg : EcologySystem.SeedlingDryKg / EcologySystem.DryShare(table[m]);
        double t = grid.GasTemperatureAt(i);
        double joules = kg * (table[m].SpecificHeat * t + EnergyAudit.OffsetsFor(table)[m]);
        EcologySystem.Plant(grid, i, m, kg, joules);
        sim.Activity.WakeAround(X, Y, Z);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Plant);
        writer.Write(Kg);
    }

    internal static PlantSeedling ReadFrom(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadString(), r.ReadDouble());
}
