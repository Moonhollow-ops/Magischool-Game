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
    public const ushort FormatVersion = 10;

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
        writer.Write(MemoryMarshal.AsBytes(grid.GasTemperature.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(grid.Mass.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(grid.LatentEnergy.AsSpan()));
        // Gas arrays are tagged with their material id, so a table that later gains gases still loads.
        writer.Write(grid.GasMass.Length);
        for (int s = 0; s < grid.GasMass.Length; s++)
        {
            writer.Write(sim.Materials.GasIds[s]);
            writer.Write(MemoryMarshal.AsBytes(grid.GasMass[s].AsSpan()));
        }
        foreach (var velocity in grid.AirVelocity)
            writer.Write(MemoryMarshal.AsBytes(velocity.AsSpan()));
        writer.Write(grid.Flame);
        writer.Write(MemoryMarshal.AsBytes(grid.Skin.AsSpan()));
        writer.Write(grid.Structure);
        writer.Write(MemoryMarshal.AsBytes(grid.FallSpeed.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(grid.FallTravel.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(grid.Ether.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(grid.EtherAmbient.AsSpan()));
        writer.Write(MemoryMarshal.AsBytes(grid.EtherRefillRate.AsSpan()));
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
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.GasTemperature.AsSpan()));
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.Mass.AsSpan()));
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.LatentEnergy.AsSpan()));
        int gasCount = reader.ReadInt32();
        for (int g = 0; g < gasCount; g++)
        {
            ushort gasId = reader.ReadUInt16();
            int species = materials.GasIndexOf(gasId);
            if (species < 0) throw new InvalidDataException($"Snapshot gas id {gasId} is not a gas in the table.");
            ReadExactly(reader, MemoryMarshal.AsBytes(grid.GasMass[species].AsSpan()));
        }

        foreach (var velocity in grid.AirVelocity)
            ReadExactly(reader, MemoryMarshal.AsBytes(velocity.AsSpan()));
        ReadExactly(reader, grid.Flame);
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.Skin.AsSpan()));
        ReadExactly(reader, grid.Structure);
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.FallSpeed.AsSpan()));
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.FallTravel.AsSpan()));
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.Ether.AsSpan()));
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.EtherAmbient.AsSpan()));
        ReadExactly(reader, MemoryMarshal.AsBytes(grid.EtherRefillRate.AsSpan()));

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
