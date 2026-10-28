using System;
using System.Linq;
using Magischool.Sim.Core;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;
using Magischool.Sim.WorldGen;
using Xunit;

namespace Magischool.Sim.Tests;

public class FluidTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private static readonly ushort Water = Table.Id(BuiltInMaterials.Water);
    private static readonly ushort Ice = Table.Id(BuiltInMaterials.Ice);
    private static readonly ushort Steam = Table.Id(BuiltInMaterials.Steam);
    private const double Room = BuiltInMaterials.RoomTemperatureK;
    private static readonly double FullWater = Table[Water].FullTileMass;

    /// <summary>Still air at room temperature in a world sealed on every side.</summary>
    private static WorldGrid Box(int width, int height, int depth, double temperatureK = Room)
    {
        var grid = new WorldGrid(width, height, depth, Table);
        grid.Fill(Air, temperatureK);
        return grid;
    }

    private static Simulation Sealed(WorldGrid grid)
    {
        Atmosphere.Balance(grid, Air, Room);
        return new Simulation("fluid-test", grid, Table, StandardSystems.Create(openSky: false));
    }

    private static double WaterIn(WorldGrid grid, Func<int, int, int, bool> where)
    {
        double total = 0;
        for (int i = 0; i < grid.Count; i++)
        {
            var (x, y, z) = grid.Coordinates(i);
            if (grid.Material[i] == Water && where(x, y, z)) total += grid.Mass[i];
        }
        return total;
    }

    private static double AllWater(WorldGrid grid) =>
        FluidSystem.TotalMass(grid, Water) + FluidSystem.TotalMass(grid, Ice) + FluidSystem.TotalMass(grid, Steam);

    [Fact]
    public void WaterFallsAndSpreadsIntoALevelPuddle()
    {
        var grid = Box(4, 4, 6);
        grid.Set(1, 1, 5, Water, Room);
        var sim = Sealed(grid);
        double air = FluidSystem.TotalMass(grid, Air);

        sim.Step(600);

        Assert.Equal(FullWater, AllWater(grid), 9);
        Assert.Equal(air, FluidSystem.TotalMass(grid, Air), 9);
        Assert.True(WaterIn(grid, (_, _, z) => z > 0) < 1e-6, "water is still hanging in the air");
        double min = double.MaxValue, max = 0;
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int i = grid.Index(x, y, 0);
            double fill = grid.Material[i] == Water ? grid.Mass[i] / FullWater : 0.0;
            min = Math.Min(min, fill);
            max = Math.Max(max, fill);
        }
        // One tile of water over a 2 m x 2 m floor is a 3 cm deep puddle, about 1/16 of a tile.
        Assert.InRange(min, 0.05, 0.075);
        Assert.True(max - min < 0.005, $"puddle is not level: {min} to {max}");
    }

    [Fact]
    public void APuddleComesToRestAndTheWorldSleeps()
    {
        var grid = new WorldGrid(16, 16, 8, Table);
        grid.Fill(Air, Room);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            grid.Set(x, y, 0, Stone, Room);
        grid.Set(8, 8, 3, Water, Room);
        Atmosphere.Balance(grid, Air, Room);
        var sim = new Simulation("puddle", grid, Table, StandardSystems.Create());

        int ticks = 0;
        while (sim.Activity.AwakeCount > 0 && ticks < 20_000)
        {
            sim.Step();
            ticks++;
        }
        Assert.True(sim.Activity.AwakeCount == 0, "the puddle never stopped moving");
        Assert.Equal(FullWater, WaterIn(grid, (_, _, z) => z == 1), 6);

        // Asleep means nothing changes any more.
        ulong hash = sim.StateHash();
        var copy = grid.Clone();
        sim.Step(100);
        Assert.Equal(copy.Mass, grid.Mass);
        Assert.Equal(copy.Temperature, grid.Temperature);
        Assert.NotEqual(hash, sim.StateHash()); // only the tick moved on
    }

    [Fact]
    public void WaterFloodsThroughAnOpenDoorAndFindsOneLevel()
    {
        // A 6 x 4 tank of water two tiles deep, a wall with a one-tile doorway, and a dry room.
        var grid = Box(13, 4, 4);
        for (int y = 0; y < 4; y++)
        for (int z = 0; z < 4; z++)
            if (!(y == 1 && z == 0)) grid.Set(6, y, z, Stone, Room);
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 6; x++)
        for (int z = 0; z < 2; z++)
            grid.Set(x, y, z, Water, Room);
        var sim = Sealed(grid);
        double water = AllWater(grid);

        sim.Step(3_000);

        Assert.Equal(water, AllWater(grid), 9);
        double left = WaterIn(grid, (x, _, _) => x < 6);
        double right = WaterIn(grid, (x, _, _) => x > 6);
        Assert.True(right > 0.4 * water, $"only {right / water:P0} of the water got through");
        // Both rooms are 6 x 4, so equal levels means equal amounts (the doorway holds a little).
        Assert.True(Math.Abs(left - right) < 0.05 * water, $"levels differ: {left:F1} kg vs {right:F1} kg");
    }

    [Fact]
    public void ConnectedColumnsSettleAtTheSameLevel()
    {
        // A U-tube: two columns joined at the bottom, one starting full and one empty. The
        // stone between them hangs from the stone walls in front and behind.
        var grid = Box(3, 3, 8);
        for (int z = 0; z < 8; z++)
        for (int x = 0; x < 3; x++)
        {
            grid.Set(x, 0, z, Stone, Room);
            grid.Set(x, 2, z, Stone, Room);
        }
        for (int z = 1; z < 8; z++) grid.Set(1, 1, z, Stone, Room);
        for (int z = 0; z < 6; z++) grid.Set(0, 1, z, Water, Room);
        grid.Set(1, 1, 0, Water, Room);
        var sim = Sealed(grid);
        double water = AllWater(grid);

        sim.Step(6_000);

        Assert.Equal(water, AllWater(grid), 9);
        double left = WaterIn(grid, (x, _, _) => x == 0) / FullWater;
        double right = WaterIn(grid, (x, _, _) => x == 2) / FullWater;
        Assert.True(Math.Abs(left - right) < 0.25, $"left column holds {left:F2} tiles, right {right:F2}");
    }

    [Fact]
    public void HotAirCarriesHeatUpFarFasterThanConduction()
    {
        double CeilingAboveHotSpot(bool fluids)
        {
            var grid = Box(5, 5, 6);
            grid.Set(2, 2, 0, Air, 400);
            Atmosphere.Balance(grid, Air, Room);
            ISimSystem[] systems = fluids
                ? StandardSystems.Create(openSky: false)
                : new ISimSystem[] { new ThermalSystem(), new PhaseSystem() };
            var sim = new Simulation("hot-air", grid, Table, systems);
            double before = EnergyAudit.TotalJoules(grid);
            sim.Step(300);
            Assert.Equal(before, EnergyAudit.TotalJoules(grid), Math.Abs(before) * 1e-12);
            return grid.Temperature[grid.Index(2, 2, 5)] - Room;
        }

        double still = CeilingAboveHotSpot(fluids: false);
        double moving = CeilingAboveHotSpot(fluids: true);
        Assert.True(moving > 0.5 && moving > 100 * still, $"ceiling warmed {moving:F3} K with moving air, {still:F5} K without");
    }

    [Fact]
    public void SteamReleasedAtTheFloorCollectsUnderTheCeiling()
    {
        // Warm enough that the steam doesn't condense. Steam (18 g/mol) is lighter than air (29).
        // It starts in one corner: an even layer over the whole floor would be a perfectly
        // balanced (if unstable) state that a deterministic world never tips out of.
        const double warm = 390;
        var grid = Box(6, 6, 6, warm);
        int steam = Table.GasIndexOf(Steam);
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
            grid.GasMass[steam][grid.Index(x, y, 0)] = 0.03;
        var sim = new Simulation("steam", grid, Table, StandardSystems.Create(openSky: false));
        double total = FluidSystem.TotalMass(grid, Steam);

        sim.Step(1_500);

        Assert.Equal(total, FluidSystem.TotalMass(grid, Steam), 12);
        double top = 0, bottom = 0;
        for (int i = 0; i < grid.Count; i++)
        {
            int z = grid.Coordinates(i).Z;
            if (z >= 3) top += grid.GasMass[steam][i];
            else bottom += grid.GasMass[steam][i];
        }
        Assert.True(top > 2 * bottom, $"steam didn't layer under the ceiling: {top:E2} kg up, {bottom:E2} kg down");
    }

    [Fact]
    public void AStoveFillsTheRoomWithWarmAirFromTheCeilingDown()
    {
        // A closed stone room with a 700 K iron stove on the floor.
        var grid = Box(12, 12, 8);
        ushort iron = Table.Id(BuiltInMaterials.Iron);
        for (int z = 0; z < 8; z++)
        for (int y = 0; y < 12; y++)
        for (int x = 0; x < 12; x++)
            if (x == 0 || y == 0 || x == 11 || y == 11 || z == 0 || z == 7) grid.Set(x, y, z, Stone, Room);
        grid.Set(6, 6, 1, iron, 700);
        var sim = Sealed(grid);
        double before = EnergyAudit.TotalJoules(grid);

        sim.Step(1_200);

        double Layer(int z)
        {
            double sum = 0;
            int n = 0;
            for (int y = 1; y < 11; y++)
            for (int x = 1; x < 11; x++)
            {
                int i = grid.Index(x, y, z);
                if (grid.HasCondensed(i)) continue;
                sum += grid.Temperature[i];
                n++;
            }
            return sum / n - Room;
        }
        for (int z = 1; z < 6; z++)
            Assert.True(Layer(z + 1) > Layer(z), $"layer {z + 1} ({Layer(z + 1):F2} K) is not warmer than layer {z} ({Layer(z):F2} K)");
        Assert.True(Layer(6) > 2 * Layer(1), $"no warm layer under the ceiling: {Layer(6):F2} K vs {Layer(1):F2} K at the floor");
        Assert.InRange(grid.AirVelocity[2][grid.Index(6, 6, 2)], 0.1, FluidSystem.MaxAirSpeed);
        Assert.Equal(before, EnergyAudit.TotalJoules(grid), before * 1e-12);
    }

    [Fact]
    public void WarmAirSpillsOutTheTopOfADoorwayAndCoolAirComesInAtTheBottom()
    {
        // Two rooms, 6 tiles high, joined by a doorway 4 tiles high. The left room starts 20 K warmer.
        var grid = Box(13, 4, 6);
        for (int y = 0; y < 4; y++)
        for (int z = 0; z < 6; z++)
            if (!(y == 1 && z < 4)) grid.Set(6, y, z, Stone, Room);
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 6; x++)
        for (int z = 0; z < 6; z++)
            grid.Temperature[grid.Index(x, y, z)] = Room + 20;
        var sim = Sealed(grid);
        double before = EnergyAudit.TotalJoules(grid);
        double air = FluidSystem.TotalMass(grid, Air);

        sim.Step(100);

        // Positive x velocity is from the warm room toward the cool one.
        Assert.True(grid.AirVelocity[0][grid.Index(6, 1, 3)] > 0.05, "warm air isn't leaving through the top of the doorway");
        Assert.True(grid.AirVelocity[0][grid.Index(5, 1, 0)] < -0.05, "cool air isn't coming in along the floor");
        Assert.Equal(before, EnergyAudit.TotalJoules(grid), before * 1e-12);
        Assert.Equal(air, FluidSystem.TotalMass(grid, Air), 9);
    }

    [Fact]
    public void BoilingAPotFillsTheRoomWithSteamThatCondensesOnColdStone()
    {
        // A sealed stone cell with a pot of water on a red-hot iron plate and a cold stone roof
        // on four corner pillars.
        var grid = Box(5, 5, 5);
        for (int y = 0; y < 5; y++)
        for (int x = 0; x < 5; x++)
        {
            grid.Set(x, y, 0, Stone, Room);
            grid.Set(x, y, 4, Stone, 280);
        }
        foreach (var (x, y) in new[] { (0, 0), (0, 4), (4, 0), (4, 4) })
            for (int z = 1; z < 4; z++) grid.Set(x, y, z, Stone, Room);
        grid.Set(2, 2, 0, Table.Id(BuiltInMaterials.Iron), 1200);
        grid.SetCondensed(grid.Index(2, 2, 1), Water, FullWater * 0.5);
        grid.Temperature[grid.Index(2, 2, 1)] = 370;
        var sim = Sealed(grid);
        double water = AllWater(grid);
        double before = EnergyAudit.TotalJoules(grid);

        double peakSteam = 0, peakHighWater = 0;
        for (int i = 0; i < 30; i++)
        {
            sim.Step(200);
            peakSteam = Math.Max(peakSteam, FluidSystem.TotalMass(grid, Steam));
            peakHighWater = Math.Max(peakHighWater, WaterIn(grid, (_, _, z) => z >= 2));
        }

        Assert.Equal(water, AllWater(grid), 9);
        Assert.Equal(before, EnergyAudit.TotalJoules(grid), before * 1e-12);
        Assert.True(peakSteam > 0.01, $"the pot barely boiled: {peakSteam:E2} kg of steam");
        // Water up near the roof can only have got there as steam.
        Assert.True(peakHighWater > 0.0, "no steam condensed under the cold roof");
    }

    [Fact]
    public void HeatAndSteamLeaveThroughTheOpenSky()
    {
        var grid = Box(4, 4, 4);
        grid.Set(1, 1, 0, Air, 600);
        Atmosphere.Balance(grid, Air, Room);
        var systems = StandardSystems.Create();
        var fluid = systems.OfType<FluidSystem>().Single();
        var sim = new Simulation("sky", grid, Table, systems);
        double before = EnergyAudit.TotalJoules(grid);

        sim.Step(1_000);

        Assert.True(fluid.SkyEnergyOutJ > 0, "no heat rose out of the world");
        Assert.Equal(before, EnergyAudit.TotalJoules(grid) + fluid.SkyEnergyOutJ, before * 1e-12);
        int air = Table.GasIndexOf(Air);
        Assert.True(fluid.SkyGasOutKg[air] != 0);
    }

    [Fact]
    public void BalancedStillAirStaysPerfectlyStill()
    {
        var grid = Box(16, 16, 8);
        var sim = Sealed(grid);
        sim.Step();
        Assert.Equal(0, sim.Activity.AwakeCount);
        Assert.True(FluidSystem.PressureAt(grid, 0) > FluidSystem.PressureAt(grid, grid.Index(0, 0, 7)),
            "air should be denser at the bottom");
    }

    [Fact]
    public void TheTestRoomPipeHoldsItsWater()
    {
        var sim = TestRoomGenerator.Create("pipe", Table, 48, 32, 4, StandardSystems.Create());
        var grid = sim.Grid;
        ushort lead = Table.Id(BuiltInMaterials.Lead);
        double InPipe() => WaterIn(grid, (x, y, z) =>
            z == TestRoomGenerator.FloorZ && y > 0 && y + 1 < grid.Height
            && grid.Material[grid.Index(x, y - 1, z)] == lead && grid.Material[grid.Index(x, y + 1, z)] == lead);

        double before = InPipe();
        Assert.True(before > 0);
        sim.Step(300);
        Assert.Equal(before, InPipe(), 9);
    }
}
