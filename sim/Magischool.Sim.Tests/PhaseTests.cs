using System;
using System.Collections.Generic;
using Magischool.Sim.Core;
using Magischool.Sim.Structure;
using Magischool.Sim.Ether;
using System.Linq;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;
using Magischool.Sim.WorldGen;
using Xunit;

namespace Magischool.Sim.Tests;

public class PhaseTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Ice = Table.Id(BuiltInMaterials.Ice);
    private static readonly ushort Water = Table.Id(BuiltInMaterials.Water);
    private static readonly ushort Steam = Table.Id(BuiltInMaterials.Steam);
    private static readonly ushort Iron = Table.Id(BuiltInMaterials.Iron);
    private static readonly ushort Lead = Table.Id(BuiltInMaterials.Lead);
    private static readonly ushort MoltenLead = Table.Id(BuiltInMaterials.MoltenLead);
    // Transition points exactly as the table stores them (floats), so tests sit precisely on them.
    private static readonly double Melt = Table[Ice].MeltingPoint, Boil = Table[Water].BoilingPoint;

    /// <summary>One isolated tile: no neighbours, so only the energy we add moves it.</summary>
    private static Simulation SingleTile(ushort material, double temperatureK)
    {
        var grid = new WorldGrid(1, 1, 1, Table);
        grid.Fill(material, temperatureK);
        return new Simulation("phase-test", grid, Table, StandardSystems.Create(openSky: false));
    }

    [Fact]
    public void IceAtTheMeltingPointNeedsTheFullLatentHeatToMelt()
    {
        var sim = SingleTile(Ice, Melt);
        double latent = 334_000.0 * sim.Grid.Mass[0];

        sim.Enqueue(new AddHeat(0, 0, 0, latent - 1.0));
        sim.Step();
        Assert.Equal(Ice, sim.Grid.Material[0]);
        Assert.Equal(Melt, sim.Grid.Temperature[0], 9);
        Assert.Equal(latent - 1.0, sim.Grid.LatentEnergy[0], 3);

        sim.Enqueue(new AddHeat(0, 0, 0, 2.0));
        sim.Step();
        Assert.Equal(Water, sim.Grid.Material[0]);
        Assert.Equal(0.0, sim.Grid.LatentEnergy[0]);
        // The 1 J left over warms the new water: 1 J / (mass × 4186 J/(kg·K)).
        Assert.Equal(Melt + 1.0 / (sim.Grid.Mass[0] * 4186.0), sim.Grid.Temperature[0], 9);
    }

    [Fact]
    public void HalfMeltedIceThatCoolsGivesItsEnergyBackFirst()
    {
        var sim = SingleTile(Ice, Melt);
        sim.Enqueue(new AddHeat(0, 0, 0, 1_000_000));
        sim.Step();
        Assert.Equal(Melt, sim.Grid.Temperature[0], 9);

        sim.Enqueue(new AddHeat(0, 0, 0, -1_500_000));
        sim.Step();
        Assert.Equal(Ice, sim.Grid.Material[0]);
        Assert.Equal(0.0, sim.Grid.LatentEnergy[0]);
        double expected = Melt - 500_000 / (sim.Grid.Mass[0] * 2100.0);
        Assert.Equal(expected, sim.Grid.Temperature[0], 6);
    }

    [Fact]
    public void SteadyHeatTakesIceThroughWaterToSteamWithRealPlateaus()
    {
        // The roadmap's test scene: an ice block melts into water, then boils into steam.
        var sim = SingleTile(Ice, 263.15);
        var grid = sim.Grid;
        double m = grid.Mass[0];
        int steam = Table.GasIndexOf(Steam);
        const double joulesPerTick = 1_000_000; // a steady 10 MW

        long firstWater = -1, firstAllSteam = -1;
        long meltPlateauTicks = 0, boilPlateauTicks = 0;
        while (grid.HasCondensed(0) || grid.Temperature[0] < Boil + 10)
        {
            sim.Enqueue(new AddHeat(0, 0, 0, joulesPerTick));
            sim.Step();
            if (firstWater < 0 && grid.Material[0] == Water) firstWater = sim.Tick;
            if (firstAllSteam < 0 && !grid.HasCondensed(0)) firstAllSteam = sim.Tick;
            if (grid.LatentEnergy[0] > 0 && grid.Material[0] == Ice) meltPlateauTicks++;
            // At the boiling point to rounding: the water and its steam keep separate energy books.
            if (grid.Material[0] == Water && Math.Abs(grid.Temperature[0] - Boil) < 1e-9) boilPlateauTicks++;
            Assert.True(sim.Tick < 10_000, "never finished boiling");
        }

        Assert.True(firstWater > 0 && firstWater < firstAllSteam);
        Assert.Equal(m, grid.GasMass[steam][0], 9);
        // Plateau lengths follow from latent heat × mass ÷ power, to within a tick.
        Assert.InRange(meltPlateauTicks, 334_000 * m / joulesPerTick - 1, 334_000 * m / joulesPerTick + 1);
        Assert.InRange(boilPlateauTicks, 2_257_000 * m / joulesPerTick - 1, 2_257_000 * m / joulesPerTick + 1);
    }

    [Fact]
    public void EnergyRoundTripsFromIceToSteamAndBack()
    {
        var sim = SingleTile(Ice, 263.15);
        double m = sim.Grid.Mass[0];
        int steam = Table.GasIndexOf(Steam);
        double before = EnergyAudit.TotalJoules(sim.Grid);
        const double joules = 3e9;

        sim.Enqueue(new AddHeat(0, 0, 0, joules));
        sim.Step();
        Assert.False(sim.Grid.HasCondensed(0));
        Assert.Equal(m, sim.Grid.GasMass[steam][0], 9);
        Assert.Equal(before + joules, EnergyAudit.TotalJoules(sim.Grid), 1e-3);

        sim.Enqueue(new AddHeat(0, 0, 0, -joules));
        sim.Step();
        Assert.Equal(Ice, sim.Grid.Material[0]);
        Assert.Equal(m, sim.Grid.Mass[0], 9);
        Assert.Equal(0.0, sim.Grid.GasMass[steam][0]);
        Assert.Equal(263.15, sim.Grid.Temperature[0], 6);
        Assert.Equal(before, EnergyAudit.TotalJoules(sim.Grid), 1e-3);
    }

    [Fact]
    public void MassIsKeptSoMeltedIceDoesNotFillItsTileAndFrozenWaterOverfillsIt()
    {
        var ice = SingleTile(Ice, Melt);
        ice.Enqueue(new AddHeat(0, 0, 0, 334_000.0 * ice.Grid.Mass[0] + 1));
        ice.Step();
        Assert.Equal(Water, ice.Grid.Material[0]);
        Assert.Equal(917.0 * WorldGrid.TileVolumeM3, ice.Grid.Mass[0], 6);
        Assert.Equal(0.917, ice.Grid.FillFraction(0), 6);

        var water = SingleTile(Water, Melt);
        water.Enqueue(new AddHeat(0, 0, 0, -(334_000.0 * water.Grid.Mass[0] + 1)));
        water.Step();
        Assert.Equal(Ice, water.Grid.Material[0]);
        Assert.Equal(1000.0 / 917.0, water.Grid.FillFraction(0), 6); // 9% too big: this is what bursts pipes
    }

    [Fact]
    public void ALeadPipeNextToRedHotIronMeltsAndEnergyIsConserved()
    {
        var grid = new WorldGrid(3, 1, 1, Table);
        grid.Fill(Air, BuiltInMaterials.RoomTemperatureK);
        grid.Set(0, 0, 0, Iron, 1700);
        grid.Set(1, 0, 0, Lead, 590);
        var sim = new Simulation("lead", grid, Table, StandardSystems.Create(openSky: false));

        double before = EnergyAudit.TotalJoules(grid);
        bool melted = false;
        for (int i = 0; i < 200_000 && !melted; i++)
        {
            sim.Step();
            melted = grid.Material[1] == MoltenLead;
        }

        Assert.True(melted, "lead never melted");
        Assert.Equal(before, EnergyAudit.TotalJoules(grid), before * 1e-12);
    }

    [Fact]
    public void TestRoomConservesEnergyWithPhaseChanges()
    {
        var systems = StandardSystems.Create(openSky: false);
        var sim = TestRoomGenerator.Create("phase-room", Table, systems: systems);
        // The room's ley node draws on the reservoir under the world, and its crystal may shatter and fall.
        double Outside() => systems.OfType<EtherSystem>().Single().ReservoirInJ + systems.OfType<StructureSystem>().Single().ReleasedPotentialJ
            + systems.OfType<Magischool.Sim.Ecology.EcologySystem>().Single().LightJ;
        var grid = sim.Grid;
        // Melting a full cubic metre of ice in a warm room takes hours, so start the ice
        // 99.9% of the way through melting and the floor air hot, and watch it finish.
        for (int i = 0; i < grid.Count; i++)
        {
            if (grid.Material[i] == Ice)
            {
                grid.Temperature[i] = Melt;
                grid.LatentEnergy[i] = 0.999 * 334_000.0 * grid.Mass[i];
            }
            else if (grid.Material[i] == Air && grid.Coordinates(i).Z == TestRoomGenerator.FloorZ)
                grid.Temperature[i] = 900;
        }

        double before = EnergyAudit.TotalJoules(grid);
        int iceBefore = Count(grid, Ice);
        sim.Step(20_000);

        Assert.True(Count(grid, Ice) < iceBefore, "no ice melted");
        Assert.InRange(Math.Abs(EnergyAudit.TotalJoules(grid) - Outside() - before) / before, 0.0, 1e-12);
    }

    [Fact]
    public void TableRejectsPhasePairsThatDisagree()
    {
        var defs = (MaterialDef[])BuiltInMaterials.All.Clone();
        int ice = Array.FindIndex(defs, d => d.Key == BuiltInMaterials.Ice);
        defs[ice] = defs[ice] with { MeltingPoint = 274f };
        Assert.Throws<ArgumentException>(() => new MaterialTable(defs));
    }

    private static int Count(WorldGrid grid, ushort material)
    {
        int n = 0;
        foreach (ushort m in grid.Material)
            if (m == material) n++;
        return n;
    }
}
