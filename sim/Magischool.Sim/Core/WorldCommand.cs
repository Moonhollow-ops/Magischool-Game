using System;
using System.IO;

namespace Magischool.Sim.Core;

/// <summary>
/// A change requested from outside the sim. Commands are the only way input
/// enters the world, which is what makes replays exact. Each command has a
/// stable tag for the replay format: append new tags, never reuse one.
/// </summary>
public abstract record WorldCommand
{
    internal abstract byte Tag { get; }
    public abstract void Apply(Simulation sim);
    internal abstract void Write(BinaryWriter writer);

    internal static WorldCommand Read(BinaryReader reader)
    {
        byte tag = reader.ReadByte();
        return tag switch
        {
            SetMaterial.TagValue => new SetMaterial(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt16()),
            SetTemperature.TagValue => new SetTemperature(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadSingle()),
            AddHeat.TagValue => new AddHeat(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadDouble()),
            Magic.CastHeat.TagValue => Magic.CastHeat.ReadFrom(reader),
            Magic.CastFreeze.TagValue => Magic.CastFreeze.ReadFrom(reader),
            Magic.CastCharge.TagValue => Magic.CastCharge.ReadFrom(reader),
            Magic.CastFireball.TagValue => Magic.CastFireball.ReadFrom(reader),
            Magic.CastConjure.TagValue => Magic.CastConjure.ReadFrom(reader),
            Bodies.SpawnBody.TagValue => Bodies.SpawnBody.ReadFrom(reader),
            Bodies.MoveBody.TagValue => Bodies.MoveBody.ReadFrom(reader),
            Bodies.EatFood.TagValue => Bodies.EatFood.ReadFrom(reader),
            Bodies.DrinkWater.TagValue => Bodies.DrinkWater.ReadFrom(reader),
            Bodies.SetAsleep.TagValue => Bodies.SetAsleep.ReadFrom(reader),
            Magic.CastGraft.TagValue => Magic.CastGraft.ReadFrom(reader),
            Perception.FaceBody.TagValue => Perception.FaceBody.ReadFrom(reader),
            Perception.SetSkyLight.TagValue => Perception.SetSkyLight.ReadFrom(reader),
            Ecology.PlantSeedling.TagValue => Ecology.PlantSeedling.ReadFrom(reader),
            _ => throw new InvalidDataException($"Unknown command tag {tag}."),
        };
    }
}

/// <summary>
/// Fill one tile with a material, replacing everything in it (a debug and building
/// tool: it creates and destroys mass). Temperature is kept, so a placed block
/// takes on its surroundings' heat.
/// </summary>
public sealed record SetMaterial(int X, int Y, int Z, ushort Material) : WorldCommand
{
    internal const byte TagValue = 1;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        if (!sim.Grid.InBounds(X, Y, Z)) return;
        if (Material >= sim.Materials.Count)
            throw new ArgumentOutOfRangeException(nameof(Material), $"No material with id {Material}.");
        int i = sim.Grid.Index(X, Y, Z);
        sim.Activity.WakeAround(X, Y, Z);
        sim.Grid.Place(i, Material, sim.Grid.Temperature[i]);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Material);
    }
}

/// <summary>
/// Set one tile's temperature in kelvin. A debug tool: it creates or destroys
/// energy, so prefer <see cref="AddHeat"/> for anything that should obey physics.
/// </summary>
public sealed record SetTemperature(int X, int Y, int Z, float Kelvin) : WorldCommand
{
    internal const byte TagValue = 2;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        if (!sim.Grid.InBounds(X, Y, Z)) return;
        sim.Activity.WakeAround(X, Y, Z);
        sim.Grid.SetTemperature(sim.Grid.Index(X, Y, Z), Math.Max(0.0, Kelvin));
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Kelvin);
    }
}

/// <summary>
/// Put energy into (or, if negative, take it out of) one tile. This is the
/// physically honest way for an outside source (a spell, a torch stand-in)
/// to heat the world: the joules are exact, and the temperature change
/// follows from the tile's heat capacity. The temperature may pass a phase
/// change point, or even dip below 0 K, for the rest of this tick: the phase
/// layer then turns the energy into latent heat, or floors it at 0 K.
/// </summary>
public sealed record AddHeat(int X, int Y, int Z, double Joules) : WorldCommand
{
    internal const byte TagValue = 3;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        if (!sim.Grid.InBounds(X, Y, Z)) return;
        int i = sim.Grid.Index(X, Y, Z);
        double capacity = sim.Grid.HeatCapacity(i);
        if (capacity <= 0.0) return;
        sim.Activity.WakeAround(X, Y, Z);
        // A source held to a solid or liquid heats that first; the air around it warms from it.
        var grid = sim.Grid;
        if (grid.HasTwoTemperatures(i))
            grid.Temperature[i] += Joules / grid.CondensedHeatCapacity(i);
        else
        {
            double dT = Joules / capacity;
            grid.Temperature[i] += dT;
            grid.GasTemperature[i] += dT;
        }
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Joules);
    }
}
