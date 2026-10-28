using System;
using System.IO;
using System.Linq;
using Magischool.Sim.Combustion;
using Magischool.Sim.Core;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Magic;
using Magischool.Sim.Materials;
using Magischool.Sim.Persistence;
using Magischool.Sim.Structure;
using Magischool.Sim.Thermal;
using Xunit;

namespace Magischool.Sim.Tests;

public class MagicTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private static readonly ushort Wood = Table.Id(BuiltInMaterials.Wood);
    private static readonly ushort Lead = Table.Id(BuiltInMaterials.Lead);
    private static readonly ushort Water = Table.Id(BuiltInMaterials.Water);
    private static readonly ushort Ice = Table.Id(BuiltInMaterials.Ice);
    private static readonly ushort Crystal = Table.Id(BuiltInMaterials.Crystal);
    private const double Room = BuiltInMaterials.RoomTemperatureK;
    private const double Ordinary = 1e6;

    /// <summary>Still air over a stone floor in an ordinary ether field.</summary>
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

    private static Simulation Start(WorldGrid grid, bool openSky = false)
    {
        Atmosphere.Balance(grid, Air, Room);
        EtherSystem.Balance(grid);
        return new Simulation("magic-test", grid, Table, StandardSystems.Create(openSky));
    }

    private static T System<T>(Simulation sim) => sim.Systems.OfType<T>().Single();

    /// <summary>Everything in the world and in flight, less what came up from the reservoir and what falling released.</summary>
    private static double Ledger(Simulation sim) =>
        EnergyAudit.TotalJoules(sim) - System<EtherSystem>(sim).ReservoirInJ - System<StructureSystem>(sim).ReleasedPotentialJ;

    /// <summary>A wall of wood three tiles high across a sealed room, 4 m from where the caster stands.</summary>
    private static Simulation WallScene()
    {
        var grid = Ground(12, 5, 6);
        for (int y = 0; y < 5; y++)
        for (int z = 1; z <= 3; z++)
            grid.Set(9, y, z, Wood, Room);
        return Start(grid);
    }

    [Fact]
    public void AFireballSetsAWoodenWallAlightAndEveryJouleIsCounted()
    {
        var sim = WallScene();
        var grid = sim.Grid;
        double ether = grid.Ether.Sum(), before = Ledger(sim);

        sim.Enqueue(new CastFireball(1, 2, 2, 1, 0, 0, 2_000_000));
        sim.Step();
        // The two megajoules came out of the field around the caster and are in flight.
        Assert.Single(sim.Projectiles);
        Assert.Equal(2_000_000, sim.Projectiles[0].Joules, 1e-6);
        Assert.Equal(ether - 2_000_000, grid.Ether.Sum() - System<EtherSystem>(sim).ReservoirInJ, ether * 1e-12);

        // At 30 m/s it covers the 4 m in a tick or two and strikes the wall's near face.
        sim.Step(2);
        Assert.Empty(sim.Projectiles);
        int hit = grid.Index(9, 2, 2);
        Assert.True(grid.Skin[hit * 6] > Table[Wood].IgnitionPoint, "the struck face never passed its ignition point");

        // No damage number: the face is past wood's ignition point, so it burns, and keeps burning.
        sim.Step(300);
        Assert.NotEqual(0, grid.Flame[hit]);
        Assert.True(System<CombustionSystem>(sim).BurnedKg > 0.01);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-12);
    }

    [Fact]
    public void ASmallFireballOnlyWarmsTheWall()
    {
        var sim = WallScene();
        sim.Enqueue(new CastFireball(1, 2, 2, 1, 0, 0, 200_000));
        sim.Step(300);
        Assert.Equal(0.0, System<CombustionSystem>(sim).BurnedKg);
        Assert.True(sim.Grid.Temperature[sim.Grid.Index(9, 2, 2)] > Room);
    }

    [Fact]
    public void FreezingTheWaterInASealedLeadPipeBurstsIt()
    {
        // A lead pipe, walls a tile thick all round, full of water. The caster stands above it.
        var grid = Ground(10, 6, 5);
        for (int x = 1; x <= 8; x++)
        for (int y = 1; y <= 3; y++)
        for (int z = 1; z <= 3; z++)
            grid.Set(x, y, z, Lead, Room);
        for (int x = 2; x <= 7; x++) grid.Set(x, 2, 2, Water, Room);
        var sim = Start(grid);
        double before = Ledger(sim);

        // Each cast only pulls heat out of the water; freezing, swelling and the pipe giving way
        // are the phase and structure layers doing what they always do.
        for (int cast = 0; cast < 2; cast++)
        {
            for (int x = 2; x <= 7; x++) sim.Enqueue(new CastFreeze(5, 5, 4, x, 2, 2, 30_000_000));
            sim.Step(50);
        }

        for (int x = 2; x <= 7; x++) Assert.Equal(Ice, grid.Material[grid.Index(x, 2, 2)]);
        int splitLead = 0;
        int plane = grid.Width * grid.Height;
        for (int i = 0; i < grid.Count; i++)
        {
            if (grid.Material[i] != Lead) continue;
            if ((grid.Structure[i] & 1) != 0 && grid.Material[i + 1] == Lead) splitLead++;
            if ((grid.Structure[i] & 2) != 0 && grid.Material[i + grid.Width] == Lead) splitLead++;
            if ((grid.Structure[i] & 4) != 0 && i + plane < grid.Count && grid.Material[i + plane] == Lead) splitLead++;
        }
        Assert.True(splitLead > 0, "the pipe held");

        // The heat went into the ether around the caster, not out of the world.
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-12);
    }

    [Fact]
    public void ChargePouredIntoACrystalFromAfarRupturesIt()
    {
        var grid = Ground(18, 13, 8);
        grid.Set(14, 6, 1, Crystal, Room);
        var sim = Start(grid);
        double before = Ledger(sim);

        // Two casts a moment apart, from 4 m away: more than a crystal holds before it breaks down.
        sim.Enqueue(new CastCharge(6, 6, 3, 14, 6, 1, 50_000_000));
        sim.Step();
        sim.Enqueue(new CastCharge(6, 6, 3, 14, 6, 1, 50_000_000));
        sim.Step(StructureSystem.CheckIntervalTicks + 1);

        Assert.True(System<EtherSystem>(sim).Ruptures > 0, "the crystal held");
        Assert.True(grid.Temperature[grid.Index(14, 6, 1)] > Room + 100);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-12);
    }

    [Fact]
    public void AnOrdinarySpellBarelyTouchesThePlaceButAPoorPlaceCannotPowerIt()
    {
        // In an ordinary place a 1 MJ fireball takes about a hundredth of what is within reach.
        var rich = Start(Ground(16, 16, 8));
        double near = rich.Grid.EtherPotential(rich.Grid.Index(2, 8, 3));
        rich.Enqueue(new CastFireball(2, 8, 3, 1, 0, 0, 1_000_000));
        rich.Step();
        Assert.Equal(1_000_000, rich.Projectiles[0].Joules, 1e-6);
        Assert.InRange(rich.Grid.EtherPotential(rich.Grid.Index(2, 8, 3)), 0.98 * near, near);

        // Where the field is a hundred times thinner, the same spell comes out weaker.
        var poor = Start(Ground(16, 16, 8, Ordinary / 100));
        poor.Enqueue(new CastFireball(2, 8, 3, 1, 0, 0, 1_000_000));
        poor.Step();
        Assert.InRange(poor.Projectiles[0].Joules, 1.0, 1_000_000 * 0.9);
    }

    [Fact]
    public void ASpellInFlightSurvivesASnapshotAndAReplay()
    {
        var live = WallScene();
        var log = ReplayLog.Begin(live);
        live.Enqueue(new CastFireball(0, 2, 2, 1, 0, 0, 2_000_000));
        live.Enqueue(new CastHeat(0, 2, 2, 9, 1, 1, 500_000));
        live.Enqueue(new CastFreeze(0, 2, 2, 9, 3, 1, 500_000));
        live.Enqueue(new CastCharge(0, 2, 2, 5, 2, 1, 500_000));
        live.Step();
        Assert.Single(live.Projectiles);

        var restored = Snapshot.FromBytes(Snapshot.ToBytes(live), Table, StandardSystems.Create(openSky: false));
        Assert.Equal(live.StateHash(), restored.StateHash());
        live.Step(100);
        restored.Step(100);
        Assert.Equal(live.StateHash(), restored.StateHash());

        using var stream = new MemoryStream();
        log.Save(stream);
        stream.Position = 0;
        var replayed = ReplayLog.Load(stream).Play(live.Tick, Table, StandardSystems.Create(openSky: false));
        Assert.Equal(live.StateHash(), replayed.StateHash());
    }
}
