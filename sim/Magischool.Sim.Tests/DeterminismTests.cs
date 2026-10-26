using System.IO;
using Magischool.Sim.Core;
using Magischool.Sim.Determinism;
using Magischool.Sim.Materials;
using Magischool.Sim.Persistence;
using Magischool.Sim.WorldGen;
using Xunit;

namespace Magischool.Sim.Tests;

/// <summary>
/// Stand-in for a physics layer until real ones exist: every tick it uses
/// the sim's RNG to nudge temperatures and swap materials, so any hidden
/// nondeterminism in the tick loop, RNG or save path shows up as a hash
/// mismatch.
/// </summary>
internal sealed class ChurnSystem : ISimSystem
{
    public string Name => "test-churn";

    public void Update(Simulation sim)
    {
        var grid = sim.Grid;
        for (int i = 0; i < 8; i++)
        {
            int index = (int)sim.Rng.NextUInt((uint)grid.Count);
            grid.Temperature[index] += sim.Rng.NextFloat() - 0.5f;
            if (sim.Rng.Chance(0.1f))
                grid.Material[index] = (ushort)sim.Rng.NextUInt((uint)sim.Materials.Count);
        }
    }
}

public class DeterminismTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();

    private static Simulation NewSim(string seed) => TestRoomGenerator.Create(seed, Table, systems: new ChurnSystem());

    /// <summary>Scripted "player input" so runs exercise the command path too.</summary>
    private static void Drive(Simulation sim, int ticks)
    {
        var steam = Table.Id(BuiltInMaterials.Steam);
        for (int i = 0; i < ticks; i++)
        {
            if (sim.Tick % 97 == 0)
                sim.Enqueue(new SetMaterial((int)(sim.Tick % sim.Grid.Width), 5, 1, steam));
            if (sim.Tick % 131 == 0)
                sim.Enqueue(new SetTemperature(3, (int)(sim.Tick % sim.Grid.Height), 1, 500f));
            sim.Step();
        }
    }

    [Fact]
    public void SameSeedAndInputsGiveIdenticalStateAfter10000Ticks()
    {
        var a = NewSim("moonhollow");
        var b = NewSim("moonhollow");
        Drive(a, 10_000);
        Drive(b, 10_000);

        Assert.Equal(10_000, a.Tick);
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void DifferentSeedsGiveDifferentWorlds()
    {
        Assert.NotEqual(NewSim("moonhollow").StateHash(), NewSim("moonhollow2").StateHash());
    }

    [Fact]
    public void WorldGenIsStablePerSeed()
    {
        var a = TestRoomGenerator.Generate("stable", Table);
        var b = TestRoomGenerator.Generate("stable", Table);
        Assert.Equal(a.Material, b.Material);
        Assert.Equal(a.Temperature, b.Temperature);
    }

    [Fact]
    public void SnapshotRoundTripContinuesIdentically()
    {
        var live = NewSim("snapshot");
        Drive(live, 500);

        var restored = Snapshot.FromBytes(Snapshot.ToBytes(live), Table, new[] { new ChurnSystem() });
        Assert.Equal(live.StateHash(), restored.StateHash());

        Drive(live, 500);
        Drive(restored, 500);
        Assert.Equal(live.StateHash(), restored.StateHash());
    }

    [Fact]
    public void ReplayRebuildsTheSameWorld()
    {
        var live = NewSim("replay");
        Drive(live, 200);
        var log = ReplayLog.Begin(live);
        Drive(live, 2_000);

        Assert.NotEmpty(log.Entries);

        var replayed = log.Play(live.Tick, Table, new[] { new ChurnSystem() });
        Assert.Equal(live.Tick, replayed.Tick);
        Assert.Equal(live.StateHash(), replayed.StateHash());
    }

    [Fact]
    public void ReplaySurvivesSavingToBytes()
    {
        var live = NewSim("replay-file");
        var log = ReplayLog.Begin(live);
        Drive(live, 1_000);

        using var stream = new MemoryStream();
        log.Save(stream);
        stream.Position = 0;
        var loaded = ReplayLog.Load(stream);

        Assert.Equal(log.Entries.Count, loaded.Entries.Count);
        Assert.Equal(live.StateHash(), loaded.Play(live.Tick, Table, new[] { new ChurnSystem() }).StateHash());
    }

    [Fact]
    public void SnapshotRejectsAMismatchedMaterialTable()
    {
        var bytes = Snapshot.ToBytes(NewSim("mismatch"));
        var defs = (MaterialDef[])BuiltInMaterials.All.Clone();
        (defs[1], defs[2]) = (defs[2], defs[1]);

        Assert.Throws<InvalidDataException>(() => Snapshot.FromBytes(bytes, new MaterialTable(defs)));
    }

    [Fact]
    public void SeedHashIsStableAcrossRuns()
    {
        // FNV-1a 64 reference values. If these change, every saved seed changes.
        Assert.Equal(0xCBF29CE484222325UL, SeedHash.Hash(""));
        Assert.Equal(0xAF63DC4C8601EC8CUL, SeedHash.Hash("a"));
        Assert.NotEqual(SeedHash.Derive("x", "worldgen"), SeedHash.Derive("x", "weather"));
    }

    [Fact]
    public void Pcg32MatchesTheReferenceImplementation()
    {
        // First outputs of the reference pcg32-demo with seed 42, sequence 54.
        var rng = new Pcg32(42, 54);
        Assert.Equal(0xA15C02B7u, rng.NextUInt());
        Assert.Equal(0x7B47F409u, rng.NextUInt());
        Assert.Equal(0xBA1D3330u, rng.NextUInt());
    }

    [Fact]
    public void Pcg32BoundedValuesStayInRange()
    {
        var rng = new Pcg32(7);
        for (int i = 0; i < 10_000; i++)
        {
            int v = rng.NextInt(-3, 4);
            Assert.InRange(v, -3, 3);
            float f = rng.NextFloat();
            Assert.True(f >= 0f && f < 1f);
        }
    }
}
