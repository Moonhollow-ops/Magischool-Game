using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Persistence;

/// <summary>
/// A starting snapshot plus every command and the tick it applied on.
/// Playing it back rebuilds the exact same world, which makes any bug or
/// emergent moment reproducible.
/// </summary>
public sealed class ReplayLog
{
    private const uint Magic = 0x5250474D; // "MGPR"
    public const ushort FormatVersion = 1;

    private readonly List<(long Tick, WorldCommand Command)> _entries = new();

    private ReplayLog(byte[] initialSnapshot)
    {
        InitialSnapshot = initialSnapshot;
    }

    public byte[] InitialSnapshot { get; }
    public IReadOnlyList<(long Tick, WorldCommand Command)> Entries => _entries;

    /// <summary>Snapshot the sim now and record every command it applies from here on.</summary>
    public static ReplayLog Begin(Simulation sim)
    {
        var log = new ReplayLog(Snapshot.ToBytes(sim));
        sim.Recorder = log;
        return log;
    }

    internal void Add(long tick, WorldCommand command)
    {
        if (_entries.Count > 0 && tick < _entries[^1].Tick)
            throw new InvalidOperationException("Replay entries must be added in tick order.");
        _entries.Add((tick, command));
    }

    /// <summary>Rebuild the world from the start and run it to <paramref name="untilTick"/>.</summary>
    public Simulation Play(long untilTick, MaterialTable materials, IEnumerable<ISimSystem>? systems = null)
    {
        var sim = Snapshot.FromBytes(InitialSnapshot, materials, systems);
        int next = 0;
        while (sim.Tick < untilTick)
        {
            while (next < _entries.Count && _entries[next].Tick == sim.Tick)
                sim.Enqueue(_entries[next++].Command);
            if (next < _entries.Count && _entries[next].Tick < sim.Tick)
                throw new InvalidDataException($"Replay entry for tick {_entries[next].Tick} is before the snapshot's tick.");
            sim.Step();
        }
        return sim;
    }

    public void Save(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(InitialSnapshot.Length);
        writer.Write(InitialSnapshot);
        writer.Write(_entries.Count);
        foreach (var (tick, command) in _entries)
        {
            writer.Write(tick);
            command.Write(writer);
        }
    }

    public static ReplayLog Load(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a Magischool replay.");
        ushort version = reader.ReadUInt16();
        if (version != FormatVersion)
            throw new InvalidDataException($"Replay format {version} is not supported (expected {FormatVersion}).");

        int snapshotLength = reader.ReadInt32();
        var snapshot = reader.ReadBytes(snapshotLength);
        if (snapshot.Length != snapshotLength)
            throw new EndOfStreamException("Replay is truncated.");

        var log = new ReplayLog(snapshot);
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            long tick = reader.ReadInt64();
            log.Add(tick, WorldCommand.Read(reader));
        }
        return log;
    }
}
