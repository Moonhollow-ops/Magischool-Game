using System;
using System.Linq;
using Magischool.Sim.Combustion;
using Magischool.Sim.Core;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Persistence;
using Magischool.Sim.Thermal;
using Xunit;

namespace Magischool.Sim.Tests;

public class CombustionTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private static readonly ushort Wood = Table.Id(BuiltInMaterials.Wood);
    private static readonly int AirGas = Table.GasIndexOf(Air);
    private static readonly int SmokeGas = Table.GasIndexOf(Table.Id(BuiltInMaterials.Smoke));
    private const double Room = BuiltInMaterials.RoomTemperatureK;

    /// <summary>A log hot enough to be smouldering: its surface is past wood's ignition point.</summary>
    private const double Ember = 650.0;

    /// <summary>Still air over a stone floor.</summary>
    private static WorldGrid Ground(int width, int height, int depth)
    {
        var grid = new WorldGrid(width, height, depth, Table);
        grid.Fill(Air, Room);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            grid.Set(x, y, 0, Stone, Room);
        return grid;
    }

    private static Simulation Start(WorldGrid grid, bool openSky)
    {
        Atmosphere.Balance(grid, Air, Room);
        return new Simulation("fire-test", grid, Table, StandardSystems.Create(openSky));
    }

    private static CombustionSystem Fire(Simulation sim) => sim.Systems.OfType<CombustionSystem>().Single();

    private static double TotalMass(WorldGrid grid)
    {
        double total = 0.0;
        for (int i = 0; i < grid.Count; i++) total += grid.Mass[i] + grid.TotalGasMass(i);
        return total;
    }

    private static int BurningTiles(WorldGrid grid) => grid.Flame.Count(f => f != 0);

    /// <summary>Oxygen's share of all the gas in the world, by mass.</summary>
    private static double OxygenShare(WorldGrid grid)
    {
        double air = 0.0, gas = 0.0;
        for (int i = 0; i < grid.Count; i++)
        {
            air += grid.GasMass[AirGas][i];
            gas += grid.TotalGasMass(i);
        }
        return air * BuiltInMaterials.AirOxygenFraction / gas;
    }

    [Fact]
    public void FireSpreadsThroughAHutAndDiesWhenTheAirRunsOut()
    {
        // A 6x6 wooden hut, walls and roof, with a doorway, in a world sealed on every side. A
        // smouldering log lies in the corner against two walls, with a cold log beside it.
        var grid = Ground(10, 10, 7);
        for (int z = 1; z <= 5; z++)
        for (int y = 2; y < 8; y++)
        for (int x = 2; x < 8; x++)
            if (x == 2 || x == 7 || y == 2 || y == 7 || z == 5) grid.Set(x, y, z, Wood, Room);
        grid.Set(4, 2, 1, Air, Room);
        grid.Set(4, 2, 2, Air, Room);
        grid.Set(3, 3, 1, Wood, Ember);
        grid.Set(3, 4, 1, Wood, Room);
        var sim = Start(grid, openSky: false);
        double mass0 = TotalMass(grid), energy0 = EnergyAudit.TotalJoules(grid);

        int mostBurning = 0;
        for (int t = 0; t < 200; t++)
        {
            sim.Step(100);
            mostBurning = Math.Max(mostBurning, BurningTiles(grid));
        }

        // The walls in the corner caught from the log's flame.
        Assert.True(mostBurning >= 3, $"at most {mostBurning} tiles burned at once");
        // Then the flames used up the oxygen they could reach and went out.
        Assert.Equal(0, BurningTiles(grid));
        Assert.InRange(OxygenShare(grid), 0.0, 0.18);
        Assert.True(Fire(sim).BurnedKg > 2.0, $"only {Fire(sim).BurnedKg} kg burned");

        // Nothing made or lost: the fuel and the oxygen it took are all in the gas now.
        Assert.Equal(mass0, TotalMass(grid), mass0 * 1e-12);
        Assert.Equal(energy0, EnergyAudit.TotalJoules(grid), energy0 * 1e-11);
    }

    [Fact]
    public void ColdWoodNeverCatches()
    {
        var grid = Ground(6, 6, 6);
        grid.Set(2, 2, 1, Wood, Room);
        grid.Set(3, 2, 1, Wood, Room);
        var sim = Start(grid, openSky: true);

        sim.Step(2000);

        Assert.Equal(0.0, Fire(sim).BurnedKg);
        Assert.Equal(0, BurningTiles(grid));
    }

    [Fact]
    public void ALogBurnsOnInTheOpenAndItsSmokeRises()
    {
        var grid = Ground(6, 6, 8);
        grid.Set(2, 2, 1, Wood, Ember);
        var sim = Start(grid, openSky: true);

        sim.Step(3000);

        Assert.NotEqual(0, grid.Flame[grid.Index(2, 2, 1)]);
        Assert.True(Fire(sim).BurnedKg > 0.5, $"only {Fire(sim).BurnedKg} kg burned");
        double low = 0.0, high = 0.0;
        for (int i = 0; i < grid.Count; i++)
        {
            int z = grid.Coordinates(i).Z;
            if (z <= 1) low += grid.GasMass[SmokeGas][i];
            else if (z >= 4) high += grid.GasMass[SmokeGas][i];
        }
        Assert.True(high > low, $"smoke {high} kg high, {low} kg low");
    }

    [Fact]
    public void AFireRestoredFromASnapshotBurnsOnIdentically()
    {
        var grid = Ground(6, 6, 6);
        grid.Set(2, 2, 1, Wood, Ember);
        grid.Set(3, 2, 1, Wood, Room);
        var live = Start(grid, openSky: true);
        live.Step(300);
        Assert.NotEqual(0, BurningTiles(grid));

        var restored = Snapshot.FromBytes(Snapshot.ToBytes(live), Table, StandardSystems.Create(openSky: true));
        Assert.Equal(live.StateHash(), restored.StateHash());

        live.Step(300);
        restored.Step(300);
        Assert.Equal(live.StateHash(), restored.StateHash());
    }
}
