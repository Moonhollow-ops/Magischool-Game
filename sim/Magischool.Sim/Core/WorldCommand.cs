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
            _ => throw new InvalidDataException($"Unknown command tag {tag}."),
        };
    }
}

/// <summary>Replace one tile's material. Temperature is kept, so a placed block takes on its surroundings' heat.</summary>
public sealed record SetMaterial(int X, int Y, int Z, ushort Material) : WorldCommand
{
    internal const byte TagValue = 1;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        if (!sim.Grid.InBounds(X, Y, Z)) return;
        if (Material >= sim.Materials.Count)
            throw new ArgumentOutOfRangeException(nameof(Material), $"No material with id {Material}.");
        sim.Grid.Material[sim.Grid.Index(X, Y, Z)] = Material;
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Material);
    }
}

/// <summary>Set one tile's temperature in kelvin (debug tool now, spells later).</summary>
public sealed record SetTemperature(int X, int Y, int Z, float Kelvin) : WorldCommand
{
    internal const byte TagValue = 2;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        if (!sim.Grid.InBounds(X, Y, Z)) return;
        sim.Grid.Temperature[sim.Grid.Index(X, Y, Z)] = MathF.Max(0f, Kelvin);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Kelvin);
    }
}
