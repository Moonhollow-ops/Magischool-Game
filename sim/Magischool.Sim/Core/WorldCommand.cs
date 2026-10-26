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
            _ => throw new InvalidDataException($"Unknown command tag {tag}."),
        };
    }
}

/// <summary>Fill one tile with a material. Temperature is kept, so a placed block takes on its surroundings' heat.</summary>
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
        sim.Grid.SetAt(i, Material, sim.Grid.Temperature[i], sim.Materials[Material].FullTileMass);
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
        sim.Grid.Temperature[sim.Grid.Index(X, Y, Z)] = Math.Max(0.0, Kelvin);
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
        sim.Grid.Temperature[i] += Joules / capacity;
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Joules);
    }
}
