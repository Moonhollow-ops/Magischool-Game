using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Persistence;

/// <summary>
/// Binary save of the complete world state. Systems are code, not state, so
/// the caller supplies them on load. Arrays are written in the machine's byte
/// order, which is little-endian on every platform Godot targets.
/// </summary>
public static class Snapshot
{
    private const uint Magic = 0x5753474D; // "MGSW"
    public const ushort FormatVersion = 4;

    public static void Save(Simulation sim, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var grid = sim.Grid;

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(sim.Seed);
        writer.Write(sim.Tick);
        writer.Write(sim.Rng.State);
        writer.Write(sim.Rng.Increment);

        // Material keys in id order, so a load can refuse a mismatched table.
        writer.Write(sim.Materials.Count);
        foreach (var def in sim.Materials.All)
            writer.Write(def.Key);

        writer.Write(grid.Width);
        writer.Write(grid.Height);
        writer.Write(grid.Depth);
        writer.Write(MemoryMarshal.AsBytes(grid.Material.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(grid.Temperature.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(grid.Mass.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(grid.LatentEnergy.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(sim.Activity.Awake.AsSpan()));
    }

    public static Simulation Load(Stream stream, MaterialTable materials, IEnumerable<ISimSystem>? systems = null)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a Magischool world snapshot.");
        ushort version = reader.ReadUInt16();
        if (version != FormatVersion)
            throw new InvalidDataException($"Snapshot format {version} is not supported (expected {FormatVersion}).");

        string seed = reader.ReadString();
        long tick = reader.ReadInt64();
        ulong rngState = reader.ReadUInt64();
        ulong rngIncrement = reader.ReadUInt64();

        int materialCount = reader.ReadInt32();
        if (materialCount > materials.Count)
            throw new InvalidDataException($"Snapshot uses {materialCount} materials but the table has {materials.Count}.");
        for (ushort id = 0; id < materialCount; id++)
        {
            string key = reader.ReadString();
            if (materials[id].Key != key)
                throw new InvalidDataException($"Material id {id} is '{key}' in the snapshot but '{materials[id].Key}' in the table.");
        }

        var grid = new WorldGrid(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), materials);
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.Material.AsSpan()));
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.Temperature.AsSpan()));
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.Mass.AsSpan()));
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.LatentEnergy.AsSpan()));

        var sim = new Simulation(seed, grid, materials, systems) { Tick = tick };
        ReadExactly(reader, MemoryMarshal.AsBytes(sim.Activity.Awake.AsSpan()));
        sim.Rng.State = rngState;
        sim.Rng.Increment = rngIncrement;
        return sim;
    }

    public static byte[] ToBytes(Simulation sim)
    {
        using var stream = new MemoryStream();
        Save(sim, stream);
        return stream.ToArray();
    }

    public static Simulation FromBytes(byte[] bytes, MaterialTable materials, IEnumerable<ISimSystem>? systems = null)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return Load(stream, materials, systems);
    }

    private static void ReadExactly(BinaryReader reader, Span<byte> destination)
    {
        while (destination.Length > 0)
        {
            int read = reader.Read(destination);
            if (read == 0) throw new EndOfStreamException("Snapshot is truncated.");
            destination = destination[read..];
        }
    }
}
