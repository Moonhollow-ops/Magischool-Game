using System;
using System.IO;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Persistence;
using Magischool.Sim.Thermal;
using Magischool.Sim.WorldGen;
using Xunit;

namespace Magischool.Sim.Tests;

public class ThermalTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Iron = Table.Id(BuiltInMaterials.Iron);
    private static readonly ushort Wood = Table.Id(BuiltInMaterials.Wood);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private const float Room = BuiltInMaterials.RoomTemperatureK;

    private static Simulation Box(int w, int h, int d, ushort fill)
    {
        var grid = new WorldGrid(w, h, d);
        grid.Fill(fill, Room);
        return new Simulation("thermal-test", grid, Table, new ISimSystem[] { new ThermalSystem() });
    }

    private static double TotalEnergy(Simulation sim)
    {
        double total = 0;
        for (int i = 0; i < sim.Grid.Count; i++)
            total += Table[sim.Grid.Material[i]].TileHeatCapacity * sim.Grid.Temperature[i];
        return total;
    }

    [Fact]
    public void EnergyIsConservedInTheTestRoom()
    {
        var sim = TestRoomGenerator.Create("conserve", Table, systems: new ThermalSystem());
        var grid = sim.Grid;
        grid.Temperature[grid.Index(10, 10, 1)] = 1500f;
        grid.Temperature[grid.Index(20, 15, 1)] = 900f;
        grid.Temperature[grid.Index(5, 5, 0)] = 150f;

        double before = TotalEnergy(sim);
        sim.Step(5_000);
        double after = TotalEnergy(sim);

        Assert.InRange(Math.Abs(after - before) / before, 0.0, 1e-12);
    }

    [Fact]
    public void AddHeatAddsExactlyThatEnergy()
    {
        var sim = Box(4, 4, 2, Stone);
        double before = TotalEnergy(sim);
        sim.Enqueue(new AddHeat(1, 1, 1, 5_000_000));
        sim.Step(1_000);

        Assert.Equal(5_000_000, TotalEnergy(sim) - before, 1e-3);
    }

    [Fact]
    public void TwoEqualTilesSettleAtTheAverage()
    {
        var sim = Box(2, 1, 1, Air);
        sim.Grid.Temperature[0] = 400f;
        sim.Grid.Temperature[1] = 300f;
        sim.Step(20_000);

        Assert.Equal(350.0, sim.Grid.Temperature[0], 0.05);
        Assert.Equal(350.0, sim.Grid.Temperature[1], 0.05);
    }

    [Fact]
    public void HeatNeverOvershoots()
    {
        // No tile may end up hotter than the hottest start or colder than the coldest,
        // even with extreme contrasts between materials with very different heat capacities.
        var sim = TestRoomGenerator.Create("overshoot", Table, systems: new ThermalSystem());
        var rng = new Determinism.Pcg32(99);
        var grid = sim.Grid;
        for (int i = 0; i < grid.Count; i++)
            grid.Temperature[i] = 100f + rng.NextFloat() * 4900f;
        double min = double.MaxValue, max = double.MinValue;
        foreach (double t in grid.Temperature) { min = Math.Min(min, t); max = Math.Max(max, t); }

        for (int tick = 0; tick < 2_000; tick++)
        {
            sim.Step();
            foreach (double t in grid.Temperature)
                Assert.InRange(t, min - 1e-6, max + 1e-6);
        }
    }

    [Fact]
    public void HotStoneWarmsTheAirBesideItWithinSeconds()
    {
        var sim = Box(5, 5, 5, Air);
        var grid = sim.Grid;
        grid.Set(2, 2, 2, Stone, 800f);
        sim.Step(100); // 10 s

        Assert.True(grid.Temperature[grid.Index(3, 2, 2)] > Room + 1f);
        Assert.True(grid.Temperature[grid.Index(2, 2, 2)] < 800f);
    }

    [Fact]
    public void IronMovesHeatAwayFromASourceFasterThanWood()
    {
        double FractionThatLeftTheSource(ushort rodMaterial)
        {
            var sim = Box(10, 1, 1, rodMaterial);
            double capacity = Table[rodMaterial].TileHeatCapacity;
            const double joulesPerTick = 100; // a steady 1 kW at one end
            for (int i = 0; i < 36_000; i++) // one hour
            {
                sim.Enqueue(new AddHeat(0, 0, 0, joulesPerTick));
                sim.Step();
            }
            double elsewhere = 0;
            for (int x = 1; x < 10; x++)
                elsewhere += capacity * (sim.Grid.Temperature[x] - Room);
            return elsewhere / (joulesPerTick * 36_000);
        }

        double iron = FractionThatLeftTheSource(Iron);
        double wood = FractionThatLeftTheSource(Wood);
        Assert.True(iron > wood * 10, $"iron {iron:P2} vs wood {wood:P2}");
    }

    [Fact]
    public void FaceConductanceIsSymmetricAndFluidsConvect()
    {
        foreach (var a in Table.All)
        foreach (var b in Table.All)
            Assert.Equal(ThermalSystem.FaceConductance(a, b), ThermalSystem.FaceConductance(b, a));

        var air = Table[Air];
        // Still air alone conducts 0.026 W/(m·K); with natural convection a face carries far more.
        Assert.True(ThermalSystem.FaceConductance(air, air) > 1.0);
    }

    [Fact]
    public void HeatedWorldIsDeterministicAndReplays()
    {
        Simulation NewSim() => TestRoomGenerator.Create("hot-replay", Table, systems: StandardSystems.Create());
        void Drive(Simulation sim, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                if (sim.Tick % 50 == 0)
                    sim.Enqueue(new AddHeat((int)(sim.Tick / 50 % sim.Grid.Width), 10, 1, 2_000_000));
                sim.Step();
            }
        }

        var a = NewSim();
        var log = ReplayLog.Begin(a);
        Drive(a, 3_000);
        var b = NewSim();
        Drive(b, 3_000);
        Assert.Equal(a.StateHash(), b.StateHash());

        using var stream = new MemoryStream();
        log.Save(stream);
        stream.Position = 0;
        var replayed = ReplayLog.Load(stream).Play(a.Tick, Table, StandardSystems.Create());
        Assert.Equal(a.StateHash(), replayed.StateHash());
    }
}
