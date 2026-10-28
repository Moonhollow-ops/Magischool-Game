using System;
using System.Linq;
using System.Threading.Tasks;
using Magischool.Sim.Core;
using Magischool.Sim.Structure;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Persistence;
using Magischool.Sim.Thermal;
using Magischool.Sim.WorldGen;
using Xunit;

namespace Magischool.Sim.Tests;

public class ActivityTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private const double Room = BuiltInMaterials.RoomTemperatureK;

    /// <summary>A 4 x 4 x 1 chunk world: stone floor, air above, all at room temperature.</summary>
    private static Simulation QuietWorld(params ISimSystem[] systems)
    {
        var grid = new WorldGrid(64, 64, 8, Table);
        grid.Fill(Air, Room);
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
            grid.Set(x, y, 0, Stone, Room);
        Atmosphere.Balance(grid, Air, Room);
        return new Simulation("quiet", grid, Table, systems.Length > 0 ? systems : StandardSystems.Create());
    }

    [Fact]
    public void AQuietWorldFallsAsleep()
    {
        var sim = QuietWorld();
        Assert.Equal(16, sim.Activity.AwakeCount);
        sim.Step();
        Assert.Equal(0, sim.Activity.AwakeCount);
    }

    [Fact]
    public void HeatWakesOnlyWhereItGoesAndCrossesChunkBorders()
    {
        var sim = QuietWorld();
        sim.Step();
        sim.Enqueue(new AddHeat(15, 8, 1, 200_000)); // air in the last column of chunk 0
        sim.Step(3_000);

        var grid = sim.Grid;
        double warmest = 0;
        for (int z = 1; z < 8; z++)
        for (int y = 0; y < 16; y++)
        for (int x = 16; x < 32; x++)
            warmest = Math.Max(warmest, grid.Temperature[grid.Index(x, y, z)]);
        Assert.True(warmest > Room + 0.1, "heat did not cross into the next chunk");
        Assert.False(sim.Activity.IsAwakeAt(60, 60, 1), "a far corner woke up for no reason");
    }

    [Fact]
    public void SleepingMatchesTheFullSimulationClosely()
    {
        // Same world and inputs, once with sleeping and once fully awake every tick.
        long sleptChunkTicks = 0;
        Simulation Run(bool sleeping)
        {
            ISimSystem[] systems = sleeping
                ? StandardSystems.Create()
                : new ISimSystem[] { new EtherSystem(), new ThermalSystem(), new PhaseSystem(), new StructureSystem(), new FluidSystem() };
            var sim = TestRoomGenerator.Create("fidelity", Table, 64, 48, 8, systems);
            for (int i = 0; i < 3_000; i++)
            {
                if (i % 300 == 0) sim.Enqueue(new AddHeat(10 + i / 100, 20, 0, 5_000_000)); // into the ground
                sim.Step();
                if (sleeping) sleptChunkTicks += sim.Activity.Count - sim.Activity.AwakeCount;
            }
            return sim;
        }

        var asleep = Run(sleeping: true);
        var awake = Run(sleeping: false);
        double worst = 0;
        for (int i = 0; i < awake.Grid.Count; i++)
        {
            Assert.Equal(awake.Grid.Material[i], asleep.Grid.Material[i]);
            worst = Math.Max(worst, Math.Abs(awake.Grid.Temperature[i] - asleep.Grid.Temperature[i]));
        }
        // Air currents amplify tiny differences a little (a current that is just below the
        // threshold in one run and just above it in the other), so allow a twentieth of a degree.
        Assert.True(worst < 0.05, $"worst difference {worst} K");
        Assert.True(sleptChunkTicks > 0, "nothing ever slept, so this test proves nothing");
    }

    [Fact]
    public void EnergyIsConservedWhileChunksSleepAndWake()
    {
        var systems = StandardSystems.Create();
        var fluid = systems.OfType<FluidSystem>().Single();
        var sim = QuietWorld(systems);
        sim.Step();
        double before = EnergyAudit.TotalJoules(sim.Grid) + fluid.SkyEnergyOutJ;
        double added = 0;
        for (int i = 0; i < 4_000; i++)
        {
            if (i % 500 == 0)
            {
                sim.Enqueue(new AddHeat(5 + i / 100, 40, 1, 1_000_000));
                added += 1_000_000;
            }
            sim.Step();
        }
        // Whatever warm air rose out through the open sky still counts.
        Assert.Equal(before + added, EnergyAudit.TotalJoules(sim.Grid) + fluid.SkyEnergyOutJ, (before + added) * 1e-12);
    }

    [Fact]
    public void ThreadCountNeverChangesTheResult()
    {
        ulong Run(int threads)
        {
            var sim = TestRoomGenerator.Create("threads", Table, 64, 48, 8, StandardSystems.Create());
            sim.Parallelism = new ParallelOptions { MaxDegreeOfParallelism = threads };
            for (int i = 0; i < 2_000; i++)
            {
                if (i % 250 == 0) sim.Enqueue(new AddHeat(20, 10 + i / 250, 1, 3_000_000));
                sim.Step();
            }
            return sim.StateHash();
        }

        ulong single = Run(1);
        Assert.Equal(single, Run(4));
        Assert.Equal(single, Run(-1));
    }

    [Fact]
    public void SleepStateSurvivesSaveAndLoad()
    {
        var live = QuietWorld();
        live.Step();
        live.Enqueue(new AddHeat(30, 30, 0, 1_000_000)); // into the stone floor
        live.Step(200);
        Assert.InRange(live.Activity.AwakeCount, 1, live.Activity.Count - 1);

        var loaded = Snapshot.FromBytes(Snapshot.ToBytes(live), Table, StandardSystems.Create());
        Assert.Equal(live.Activity.Awake, loaded.Activity.Awake);
        live.Step(500);
        loaded.Step(500);
        Assert.Equal(live.StateHash(), loaded.StateHash());
    }
}
