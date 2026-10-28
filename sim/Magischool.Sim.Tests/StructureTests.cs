using System;
using System.Linq;
using Magischool.Sim.Core;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Persistence;
using Magischool.Sim.Structure;
using Magischool.Sim.Thermal;
using Xunit;

namespace Magischool.Sim.Tests;

public class StructureTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private static readonly ushort Wood = Table.Id(BuiltInMaterials.Wood);
    private static readonly ushort Iron = Table.Id(BuiltInMaterials.Iron);
    private static readonly ushort Ice = Table.Id(BuiltInMaterials.Ice);
    private static readonly ushort Water = Table.Id(BuiltInMaterials.Water);
    private const double Room = BuiltInMaterials.RoomTemperatureK;

    /// <summary>Still air over a stone floor of bedrock.</summary>
    private static WorldGrid Ground(int width, int height, int depth, double temperatureK = Room)
    {
        var grid = new WorldGrid(width, height, depth, Table);
        grid.Fill(Air, temperatureK);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            grid.Set(x, y, 0, Stone, temperatureK);
        return grid;
    }

    private static Simulation Start(WorldGrid grid, bool openSky = false, double temperatureK = Room)
    {
        Atmosphere.Balance(grid, Air, temperatureK);
        return new Simulation("structure-test", grid, Table, StandardSystems.Create(openSky));
    }

    private static StructureSystem Structure(Simulation sim) => sim.Systems.OfType<StructureSystem>().Single();

    private static bool IsLoose(WorldGrid grid, int i) => (grid.Structure[i] & WorldGrid.LooseBit) != 0;

    private static double TotalMass(WorldGrid grid)
    {
        double total = 0.0;
        for (int i = 0; i < grid.Count; i++) total += grid.Mass[i] + grid.TotalGasMass(i);
        return total;
    }

    /// <summary>A stone column with a one-tile-thick stone beam of <paramref name="length"/> tiles sticking out from its top.</summary>
    private static WorldGrid Cantilever(int length)
    {
        var grid = Ground(length + 4, 3, 5);
        for (int z = 1; z <= 3; z++) grid.Set(1, 1, z, Stone, Room);
        for (int x = 2; x < 2 + length; x++) grid.Set(x, 1, 3, Stone, Room);
        return grid;
    }

    [Fact]
    public void AStoneCantileverIsStressedAsBeamTheorySays()
    {
        var grid = Cantilever(12);
        var sim = Start(grid);
        sim.Step(1);

        // Root moment w·L²/2 over the section modulus b·h²/6 of a half-metre square.
        double length = 12 * WorldGrid.TileSizeM;
        double perMetre = Table[Stone].FullTileMass * FluidSystem.Gravity / WorldGrid.TileSizeM;
        double stress = perMetre * length * length / 2.0 / (Math.Pow(WorldGrid.TileSizeM, 3) / 6.0);
        double expected = stress / (Table[Stone].TensileStrength * 1e6);
        // The lattice measures from tile centres, so its lever is a little longer.
        Assert.InRange(Structure(sim).UtilisationAt(grid.Index(2, 1, 3)), expected, expected * 1.15);

        sim.Step(60);
        Assert.Equal(0, Structure(sim).BondsBroken);
    }

    [Fact]
    public void ACantileverTooLongForStoneSnapsAndFalls()
    {
        var grid = Cantilever(20);
        var sim = Start(grid);
        sim.Step(60);

        Assert.True(Structure(sim).BondsBroken >= 1);
        // The beam came away whole at its root and dropped to the floor as rubble.
        for (int x = 2; x < 22; x++)
        {
            Assert.Equal(MaterialTable.Empty, grid.Material[grid.Index(x, 1, 3)]);
            Assert.Equal(Stone, grid.Material[grid.Index(x, 1, 1)]);
            Assert.True(IsLoose(grid, grid.Index(x, 1, 1)));
        }
        Assert.Equal(Stone, grid.Material[grid.Index(1, 1, 3)]);
    }

    [Fact]
    public void AFallingBlockTurnsItsPotentialEnergyIntoHeat()
    {
        var grid = Ground(3, 3, 10);
        grid.Set(1, 1, 9, Stone, Room);
        var sim = Start(grid);
        double mass0 = TotalMass(grid), energy0 = EnergyAudit.TotalJoules(grid);

        // Free fall through 4 m takes about 0.9 s; give it 3.
        sim.Step(30);

        int landed = grid.Index(1, 1, 1);
        Assert.Equal(Stone, grid.Material[landed]);
        Assert.True(IsLoose(grid, landed));
        Assert.Equal(0.0, grid.FallSpeed[landed]);

        // It dropped 8 tiles, trading places with air: its net weight times the drop, all now heat.
        double air = FluidSystem.TotalMass(grid, Air) / (grid.Count - 9 - 1);
        double expected = (Table[Stone].FullTileMass - air) * FluidSystem.Gravity * 8 * WorldGrid.TileSizeM;
        double released = Structure(sim).ReleasedPotentialJ;
        Assert.Equal(expected, released, expected * 0.01);
        Assert.Equal(energy0 + released, EnergyAudit.TotalJoules(grid), energy0 * 1e-12);
        Assert.Equal(mass0, TotalMass(grid), mass0 * 1e-12);
    }

    /// <summary>
    /// A stone slab on a wooden beam on two posts about 11 cm square and 1.5 m tall. With
    /// <paramref name="fire"/>, four smouldering logs are heaped round the foot of one post.
    /// </summary>
    private static WorldGrid Shelter(bool fire)
    {
        var grid = Ground(8, 5, 8);
        foreach (int x in new[] { 2, 5 })
        for (int z = 1; z <= 3; z++)
        {
            grid.Set(x, 2, z, Wood, Room);
            grid.SetCondensed(grid.Index(x, 2, z), Wood, 0.05 * Table[Wood].FullTileMass);
        }
        for (int x = 2; x <= 5; x++) grid.Set(x, 2, 4, Wood, Room);
        for (int x = 2; x <= 5; x++)
        for (int y = 1; y <= 3; y++)
            grid.Set(x, y, 5, Stone, Room);
        if (fire)
            foreach (var (x, y) in new[] { (1, 2), (3, 2), (2, 1), (2, 3) })
                grid.Set(x, y, 1, Wood, 650.0);
        return grid;
    }

    [Fact]
    public void AShelterOnPostsStandsUntilAFireBurnsThroughOne()
    {
        var cold = Shelter(fire: false);
        var standing = Start(cold);
        standing.Step(600);
        Assert.Equal(0, Structure(standing).BondsBroken);
        Assert.Equal(Stone, cold.Material[cold.Index(3, 2, 5)]);

        var grid = Shelter(fire: true);
        var sim = Start(grid);
        double mass0 = TotalMass(grid), energy0 = EnergyAudit.TotalJoules(grid);
        sim.Step(3000);

        // The post weakened as it heated, gave way, and the roof came down in pieces.
        Assert.Equal(MaterialTable.Empty, grid.Material[grid.Index(3, 2, 5)]);
        int rubble = 0;
        for (int i = 0; i < grid.Count; i++)
            if (grid.Material[i] == Stone && IsLoose(grid, i)) rubble++;
        Assert.Equal(12, rubble);

        Assert.Equal(mass0, TotalMass(grid), mass0 * 1e-12);
        Assert.Equal(energy0 + Structure(sim).ReleasedPotentialJ, EnergyAudit.TotalJoules(grid), energy0 * 1e-11);
    }

    /// <summary>A 3x3x3 iron box with walls <paramref name="wallFill"/> full, holding a tile of water frozen solid.</summary>
    private static Simulation FrozenBox(double wallFill, double temperatureK)
    {
        var grid = Ground(5, 5, 5, temperatureK);
        for (int z = 1; z <= 3; z++)
        for (int y = 1; y <= 3; y++)
        for (int x = 1; x <= 3; x++)
        {
            grid.Set(x, y, z, Iron, temperatureK);
            grid.SetCondensed(grid.Index(x, y, z), Iron, wallFill * Table[Iron].FullTileMass);
        }
        // A full tile's worth of water, now ice: 9% more volume than the tile has.
        grid.Set(2, 2, 2, Ice, temperatureK);
        grid.SetCondensed(grid.Index(2, 2, 2), Ice, Table[Water].FullTileMass);
        var sim = Start(grid, temperatureK: temperatureK);
        sim.Step(1);
        return sim;
    }

    [Fact]
    public void IceFrozenInAThinIronBoxBurstsIt()
    {
        var sim = FrozenBox(0.1, 263.0);
        var grid = sim.Grid;
        Assert.True(Structure(sim).BondsBroken > 0);
        // Once the box cracked the ice had room, so it pushes no longer.
        Assert.True(IsLoose(grid, grid.Index(2, 2, 2)));
    }

    [Fact]
    public void AThickBoxHoldsTheIceAndNearMeltingTheIcePushesLess()
    {
        var thick = FrozenBox(1.0, 263.0);
        var grid = thick.Grid;
        Assert.False(IsLoose(grid, grid.Index(2, 2, 2)));
        for (int z = 1; z <= 3; z++)
        for (int y = 1; y <= 3; y++)
        for (int x = 1; x <= 3; x++)
            Assert.Equal(0, grid.Structure[grid.Index(x, y, z)] & 7);

        // Pressure melts ice, so just below 0 °C it can't build up enough to break even thin walls.
        var thin = FrozenBox(0.1, 272.9);
        Assert.Equal(0, Structure(thin).BondsBroken);
    }

    [Fact]
    public void ACollapseRestoredFromASnapshotFallsIdentically()
    {
        var live = Start(Shelter(fire: true));
        live.Step(1250);

        var restored = Snapshot.FromBytes(Snapshot.ToBytes(live), Table, StandardSystems.Create(openSky: false));
        Assert.Equal(live.StateHash(), restored.StateHash());

        live.Step(400);
        restored.Step(400);
        Assert.Equal(live.StateHash(), restored.StateHash());
        Assert.True(Structure(live).BondsBroken > 0);
    }
}
