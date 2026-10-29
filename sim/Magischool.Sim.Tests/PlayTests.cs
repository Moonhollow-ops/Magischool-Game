using System;
using System.Linq;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Magic;
using Magischool.Sim.Materials;
using Magischool.Sim.Perception;
using Magischool.Sim.Persistence;
using Magischool.Sim.Structure;
using Magischool.Sim.Thermal;
using Magischool.Sim.WorldGen;
using Xunit;

namespace Magischool.Sim.Tests;

/// <summary>What a player's student can sense, reach and walk to.</summary>
public class PlayTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private static readonly ushort Iron = Table.Id(BuiltInMaterials.Iron);
    private const double Room = BuiltInMaterials.RoomTemperatureK;

    private static WorldGrid Ground(int width, int height, int depth)
    {
        var grid = new WorldGrid(width, height, depth, Table);
        grid.Fill(Air, Room);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            grid.Set(x, y, 0, Stone, Room);
        Array.Fill(grid.EtherAmbient, 1e6);
        return grid;
    }

    private static Simulation Start(WorldGrid grid, double skyLux = LightField.OvercastDayLux, bool openSky = true)
    {
        Atmosphere.Balance(grid, Air, Room);
        EtherSystem.Balance(grid);
        var sim = new Simulation("play-test", grid, Table, StandardSystems.Create(openSky));
        sim.SkyIlluminanceLux = skyLux;
        return sim;
    }

    private static Body Spawn(Simulation sim, int x, int y, int z = 1)
    {
        sim.Enqueue(new SpawnBody(x, y, z, BodyPlans.Human.Key));
        sim.Step();
        return sim.Bodies.Last();
    }

    private static T System<T>(Simulation sim) => sim.Systems.OfType<T>().Single();

    private static double Ledger(Simulation sim) =>
        EnergyAudit.TotalJoules(sim) + sim.EtherMatterKg * Spellcasting.EtherPerKg
        - System<EtherSystem>(sim).ReservoirInJ - System<StructureSystem>(sim).ReleasedPotentialJ
        - System<Ecology.EcologySystem>(sim).LightJ - System<BodySystem>(sim).ReleasedPotentialJ;

    // ---------------------------------------------------------------- sense

    [Fact]
    public void ASenseSpendsABreathOfEtherAsHeatInItsTargetAndSaysSo()
    {
        var grid = Ground(10, 6, 4);
        grid.Set(6, 3, 1, Iron, Room);
        // The sky closed, so nothing radiates away while it is counted.
        var sim = Start(grid, openSky: false);
        double before = Ledger(sim);
        int target = grid.Index(6, 3, 1);
        double was = grid.Temperature[target];

        var sense = new CastSense(2, 3, 2, 6, 3, 1, CastSense.DefaultJoules);
        sim.Enqueue(sense);
        sim.Step();

        Assert.Equal(CastSense.DefaultJoules, sense.Gathered, 1e-6);
        // A kilojoule in a tile of iron: a few thousandths of a degree.
        Assert.True(grid.Temperature[target] > was && grid.Temperature[target] - was < 0.01);
        sim.Step(50);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-12);
    }

    [Fact]
    public void ASenseIntoNothingGathersNothing()
    {
        var sim = Start(Ground(10, 6, 4));
        var sense = new CastSense(2, 3, 2, 40, 3, 1, CastSense.DefaultJoules);
        sim.Enqueue(sense);
        sim.Step();
        Assert.Equal(0.0, sense.Gathered);
    }

    [Fact]
    public void ASenseSurvivesAReplay()
    {
        var live = Start(Ground(10, 6, 4));
        var log = ReplayLog.Begin(live);
        live.Enqueue(new CastSense(2, 3, 2, 6, 3, 0, CastSense.DefaultJoules));
        live.Step(5);
        using var stream = new System.IO.MemoryStream();
        log.Save(stream);
        stream.Position = 0;
        var replayed = ReplayLog.Load(stream).Play(live.Tick, Table, StandardSystems.Create(openSky: true));
        Assert.Equal(live.StateHash(), replayed.StateHash());
    }

    // ---------------------------------------------------------------- sight

    [Fact]
    public void AStudentSeesTheOpenGroundAroundThemButNotBehindAWall()
    {
        var grid = Ground(16, 9, 5);
        // A stone wall three tiles high, from z 1 to 3, between the student and the far side.
        for (int y = 0; y < 9; y++)
        for (int z = 1; z <= 3; z++)
            grid.Set(8, y, z, Stone, Room);
        var sim = Start(grid);
        var body = Spawn(sim, 3, 4);
        var lights = LightField.AllSources(sim);

        Assert.True(Sight.Sees(sim, lights, body, 5, 4, 1), "open ground nearby should be in sight");
        Assert.True(Sight.Sees(sim, lights, body, 8, 4, 1), "the face of the wall should be in sight");
        Assert.False(Sight.Sees(sim, lights, body, 12, 4, 1), "behind the wall should be hidden");
    }

    [Fact]
    public void InTheDarkOrAsleepAStudentSeesNothing()
    {
        var dark = Start(Ground(10, 6, 6), skyLux: 0.0);
        var body = Spawn(dark, 3, 3);
        Assert.False(Sight.Sees(dark, LightField.AllSources(dark), body, 4, 3, 1));

        var day = Start(Ground(10, 6, 6));
        var sleeper = Spawn(day, 3, 3);
        Assert.True(Sight.Sees(day, LightField.AllSources(day), sleeper, 4, 3, 1));
        sleeper.Asleep = true;
        Assert.False(Sight.Sees(day, LightField.AllSources(day), sleeper, 4, 3, 1));
    }

    // ---------------------------------------------------------------- walking

    [Fact]
    public void AStudentFindsTheWayRoundAWallAndWalksIt()
    {
        var grid = Ground(16, 9, 5);
        // A wall with a gap at the bottom row.
        for (int y = 0; y < 8; y++)
        for (int z = 1; z <= 4; z++)
            grid.Set(8, y, z, Stone, Room);
        var sim = Start(grid);
        var body = Spawn(sim, 3, 2);

        var path = Pathfinder.Find(sim, body, 12, 2);
        Assert.NotNull(path);
        Assert.Contains(path!, p => p.X == 8 && p.Y == 8);
        foreach (var (x, y, z) in path!)
        {
            sim.Enqueue(new MoveBody(body.Id, x - body.X, y - body.Y));
            sim.Step(5);
            Assert.Equal((x, y, z), (body.X, body.Y, body.Z));
        }
    }

    [Fact]
    public void ThereIsNoWayOutOfASealedCell()
    {
        var grid = Ground(12, 9, 5);
        for (int y = 2; y <= 6; y++)
        for (int x = 2; x <= 6; x++)
        for (int z = 1; z <= 4; z++)
            if (x == 2 || x == 6 || y == 2 || y == 6) grid.Set(x, y, z, Stone, Room);
        var sim = Start(grid);
        var body = Spawn(sim, 4, 4);
        Assert.Null(Pathfinder.Find(sim, body, 10, 4));
        Assert.NotNull(Pathfinder.Find(sim, body, 5, 5));
    }

    [Fact]
    public void TheTestRoomHasAPlayerStandingOutside()
    {
        var sim = TestRoomGenerator.CreateForPlay("moonhollow", Table, StandardSystems.Create());
        TestRoomGenerator.Populate(sim);
        int id = TestRoomGenerator.PlacePlayer(sim);
        Assert.True(id > 0);
        var player = sim.Bodies.Single(b => b.Id == id);
        Assert.False(player.Plan.Instinctive);
        Assert.Equal(TestRoomGenerator.FloorZ, player.Z);

        // The room's door is open above its sill, so the student can walk in.
        var grid = sim.Grid;
        int cx = grid.Width / 2, cy = grid.Height / 2;
        Assert.NotNull(Pathfinder.Find(sim, player, cx, cy));
    }
}
