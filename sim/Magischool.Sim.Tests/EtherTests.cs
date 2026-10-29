using System;
using System.Linq;
using Magischool.Sim.Core;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Persistence;
using Magischool.Sim.Structure;
using Magischool.Sim.Thermal;
using Xunit;

namespace Magischool.Sim.Tests;

public class EtherTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private static readonly ushort Iron = Table.Id(BuiltInMaterials.Iron);
    private static readonly ushort Crystal = Table.Id(BuiltInMaterials.Crystal);
    private const double Room = BuiltInMaterials.RoomTemperatureK;

    /// <summary>An ordinary place: a million joules of ether per cubic metre of open space.</summary>
    private const double Ordinary = 1e6;

    /// <summary>A ley node: fifty times richer, and the ether wells up there within a second.</summary>
    private const double NodePotential = 5e7;

    /// <summary>Still air over a stone floor, everywhere at <paramref name="ambient"/> potential.</summary>
    private static WorldGrid Ground(int width, int height, int depth, double ambient = Ordinary)
    {
        var grid = new WorldGrid(width, height, depth, Table);
        grid.Fill(Air, Room);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            grid.Set(x, y, 0, Stone, Room);
        Array.Fill(grid.EtherAmbient, ambient);
        return grid;
    }

    private static Simulation Start(WorldGrid grid, bool balanced = true)
    {
        Atmosphere.Balance(grid, Air, Room);
        if (balanced) EtherSystem.Balance(grid);
        return new Simulation("ether-test", grid, Table, StandardSystems.Create(openSky: false));
    }

    private static EtherSystem Field(Simulation sim) => sim.Systems.OfType<EtherSystem>().Single();

    /// <summary>Energy of the world less what came up from the reservoir and what falling released: never changes.</summary>
    private static double Ledger(Simulation sim) =>
        EnergyAudit.TotalJoules(sim.Grid) - Field(sim).ReservoirInJ - sim.Systems.OfType<StructureSystem>().Single().ReleasedPotentialJ - sim.Systems.OfType<Magischool.Sim.Ecology.EcologySystem>().Single().LightJ;

    [Fact]
    public void AnEmptyPlaceFillsFromTheReservoirAndEveryJouleIsCounted()
    {
        var grid = Ground(6, 6, 4);
        var sim = Start(grid, balanced: false);
        double before = Ledger(sim);

        // Ten minutes is one refill time: each tile closes 1 - 1/e of the gap to its potential.
        sim.Step(6000);

        double expected = Ordinary * (1.0 - Math.Exp(-600.0 * WorldGrid.DefaultEtherRefillPerSecond));
        for (int i = 0; i < grid.Count; i++)
            Assert.Equal(expected, grid.EtherPotential(i), expected * 1e-3);
        Assert.Equal(grid.Ether.Sum(), Field(sim).ReservoirInJ, Field(sim).ReservoirInJ * 1e-12);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-12);
    }

    [Fact]
    public void ARitualDrainsAPlaceAndItRecoversFromAroundItAndBelow()
    {
        var grid = Ground(16, 16, 8);
        var sim = Start(grid);
        double full = grid.Ether.Sum();

        // A great ritual draws a 4 x 4 x 4 block dry in an instant (spells themselves come with layer 8).
        double drawn = 0.0;
        for (int z = 2; z < 6; z++)
        for (int y = 6; y < 10; y++)
        for (int x = 6; x < 10; x++)
        {
            int i = grid.Index(x, y, z);
            drawn += grid.Ether[i];
            grid.Ether[i] = 0.0;
        }
        sim.Activity.WakeAll();

        // Ether flows in from the surroundings within seconds, so the hollow closes up while the
        // whole region stays short of what was taken.
        sim.Step(100);
        int centre = grid.Index(8, 8, 4);
        Assert.True(grid.EtherPotential(centre) > 0.3 * Ordinary);
        Assert.True(grid.Ether.Sum() < full - 0.9 * drawn);

        // The reservoir makes the shortfall good over the refill time: after 30 minutes 95% is back.
        sim.Step(18_000);
        Assert.True(grid.Ether.Sum() > full - 0.06 * drawn);
        Assert.Equal(drawn, Field(sim).ReservoirInJ, drawn * 0.06);
    }

    /// <summary>
    /// A stone kerb runs along the floor from a ley node to an aether crystal 3 m away, in a
    /// sealed corridor. With <paramref name="ironWall"/>, a wall of cold iron cuts the corridor,
    /// floor and all, just before the crystal.
    /// </summary>
    private static Simulation LeyLine(bool ironWall)
    {
        var grid = Ground(14, 3, 4);
        for (int x = 1; x < 7; x++) grid.Set(x, 1, 1, Stone, Room);
        grid.Set(7, 1, 1, Crystal, Room);
        if (ironWall)
            for (int y = 0; y < 3; y++)
            for (int z = 0; z < 4; z++)
                grid.Set(6, y, z, Iron, Room);
        int node = grid.Index(1, 1, 1);
        grid.EtherAmbient[node] = NodePotential;
        grid.EtherRefillRate[node] = 1.0;
        return Start(grid);
    }

    [Fact]
    public void ArcaneChargeFlowsThroughStoneAndRupturesAnOverloadedCrystal()
    {
        var sim = LeyLine(ironWall: false);
        var grid = sim.Grid;
        int crystal = grid.Index(7, 1, 1);
        double heatCapacity = grid.CondensedHeatCapacity(crystal);
        double before = Ledger(sim);

        // The crystal drinks in charge from the node until it passes its breakdown potential.
        while (Field(sim).Ruptures == 0)
        {
            sim.Step();
            Assert.True(sim.Tick < 3000, "the crystal never ruptured");
            Assert.True(grid.EtherPotential(crystal) <= Table[Crystal].EtherBreakdown || Field(sim).Ruptures > 0);
        }
        Assert.True(grid.EtherPotential(grid.Index(4, 1, 1)) > 3 * Ordinary, "no charge in the stone between");

        // The charge it held went into it as heat: a crystal-full of breakdown potential, tens of megajoules.
        double discharged = Field(sim).DischargedJ;
        Assert.True(discharged >= Table[Crystal].EtherBreakdown * Table[Crystal].EtherPermittivity * WorldGrid.TileVolumeM3);
        Assert.InRange(grid.Temperature[crystal] - Room, 0.95 * discharged / heatCapacity, discharged / heatCapacity);

        // And it shattered: within a second it holds to nothing.
        sim.Step(StructureSystem.CheckIntervalTicks);
        Assert.Equal(Crystal, grid.Material[crystal]);
        Assert.NotEqual(0, grid.Structure[crystal] & WorldGrid.LooseBit);

        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-12);
    }

    [Fact]
    public void AWallOfColdIronShieldsTheCrystal()
    {
        var sim = LeyLine(ironWall: true);
        var grid = sim.Grid;
        sim.Step(6000);

        Assert.Equal(0, Field(sim).Ruptures);
        // The stone on the node's side is charged far above the crystal behind the iron.
        Assert.True(grid.EtherPotential(grid.Index(4, 1, 1)) > 10 * Ordinary);
        Assert.True(grid.EtherPotential(grid.Index(7, 1, 1)) < 3 * Ordinary);
    }

    [Fact]
    public void ASettledFieldGoesToSleep()
    {
        var sim = Start(Ground(20, 20, 8));
        sim.Step(20);
        Assert.Equal(0, sim.Activity.AwakeCount);
    }

    [Fact]
    public void ARuptureRestoredFromASnapshotPlaysOutIdentically()
    {
        var live = LeyLine(ironWall: false);
        live.Step(600);

        var restored = Snapshot.FromBytes(Snapshot.ToBytes(live), Table, StandardSystems.Create(openSky: false));
        Assert.Equal(live.StateHash(), restored.StateHash());

        live.Step(600);
        restored.Step(600);
        Assert.Equal(live.StateHash(), restored.StateHash());
        Assert.True(Field(live).Ruptures > 0);
    }
}
