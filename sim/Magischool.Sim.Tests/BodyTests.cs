using System;
using System.Linq;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Structure;
using Magischool.Sim.Thermal;
using Xunit;
using Xunit.Abstractions;

namespace Magischool.Sim.Tests;

public class BodyTests
{
    private readonly ITestOutputHelper _out;
    public BodyTests(ITestOutputHelper output) => _out = output;

    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private static readonly ushort Water = Table.Id(BuiltInMaterials.Water);
    private const double Room = BuiltInMaterials.RoomTemperatureK;

    /// <summary>Still air at <paramref name="airK"/> over a stone floor.</summary>
    private static WorldGrid Ground(int width, int height, int depth, double airK = Room)
    {
        var grid = new WorldGrid(width, height, depth, Table);
        grid.Fill(Air, airK);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            grid.Set(x, y, 0, Stone, airK);
        Array.Fill(grid.EtherAmbient, 1e6);
        return grid;
    }

    private static Simulation Start(WorldGrid grid, double airK = Room, bool openSky = false)
    {
        Atmosphere.Balance(grid, Air, airK);
        EtherSystem.Balance(grid);
        return new Simulation("body-test", grid, Table, StandardSystems.Create(openSky));
    }

    private static Body Spawn(Simulation sim, int x, int y, int z, double clo = 1.0)
    {
        sim.Enqueue(new SpawnBody(x, y, z, BodyPlans.Human.Key, clo));
        sim.Step();
        return sim.Bodies.Last();
    }

    private void Report(Simulation sim, Body b, string label)
    {
        _out.WriteLine($"{label} t={sim.ElapsedSeconds / 60:F1} min core={b.BloodTemperature - 273.15:F2} skin={b.MeanSkinTemperature - 273.15:F2} " +
                       $"M={b.MetabolicW:F0} W shiver={b.ShiverW:F0} sweat={b.SweatKgPerS * 3.6e6:F0} g/h O2={b.OxygenFraction:F2} sat={b.OxygenSaturation:F2} " +
                       $"water-={b.WaterDeficitKg:F3} gly={b.GlycogenKg:F3} alive={b.Alive} conscious={b.Conscious} heart={(b.HeartStopped ? "stopped" : "beating")} brain={b.BrainHypoxia:F2}");
    }

    private static double Ledger(Simulation sim) =>
        EnergyAudit.TotalJoules(sim) - Sys<EtherSystem>(sim).ReservoirInJ - Sys<StructureSystem>(sim).ReleasedPotentialJ
        - Sys<BodySystem>(sim).ReleasedPotentialJ;

    private static double MassOf(Simulation sim)
    {
        double kg = 0.0;
        var grid = sim.Grid;
        for (int i = 0; i < grid.Count; i++) kg += grid.Mass[i] + grid.TotalGasMass(i);
        foreach (var b in sim.Bodies) kg += Physiology.Kg(b);
        return kg;
    }

    private static T Sys<T>(Simulation sim) => sim.Systems.OfType<T>().Single();

    /// <summary>A narrow room, 1.5 m square, open to the sky, with air at <paramref name="airK"/>.</summary>
    private static Simulation Room3(double airK = Room, int depth = 7)
    {
        var grid = Ground(3, 3, depth, airK);
        Atmosphere.Balance(grid, Air, airK);
        EtherSystem.Balance(grid);
        return new Simulation("body-test", grid, Table, StandardSystems.Create(openSky: true, skyTemperatureK: airK));
    }

    [Fact]
    public void AClothedStudentKeepsWarmInAComfortableRoom()
    {
        var sim = Room3(295.15);
        var body = Spawn(sim, 1, 1, 1);
        for (int k = 0; k < 6; k++)
        {
            sim.Step(6000);
            Report(sim, body, "22C");
        }
        Assert.InRange(body.BloodTemperature, 309.6, 310.6);
        Assert.InRange(body.MeanSkinTemperature, 305.0, 308.5);
        Assert.True(body.Conscious);
    }

    [Fact]
    public void ColdWaterChillsTheCore()
    {
        var grid = Ground(3, 3, 7, 278.15);
        for (int z = 1; z <= 3; z++)
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
            grid.Set(x, y, z, Water, 278.15);
        Atmosphere.Balance(grid, Air, 278.15);
        var sim = new Simulation("body-test", grid, Table, StandardSystems.Create(openSky: true, skyTemperatureK: 278.15));
        var body = Spawn(sim, 1, 1, 1);
        double shiver = 0.0;
        for (int k = 0; k < 12; k++)
        {
            sim.Step(3000);
            shiver = Math.Max(shiver, body.ShiverW);
            Report(sim, body, "5C water");
        }
        int i = sim.Grid.Index(1, 1, 2);
        _out.WriteLine($"water by the thighs {sim.Grid.Temperature[i] - 273.15:F2} C");
        // Shivering as hard as it can, and still losing: about a degree an hour for a well-padded adult.
        Assert.True(shiver >= BodySystem.MaxShiverW);
        Assert.True(body.BloodTemperature < 309.6);
    }

    [Fact]
    public void SweatHoldsBackTheHeatOfAHotDryRoom()
    {
        var sim = Room3(318.15);
        var body = Spawn(sim, 1, 1, 1, clo: 0.5);
        for (int k = 0; k < 12; k++)
        {
            sim.Step(3000);
            Report(sim, body, "45C");
        }
        Assert.True(body.SweatKgPerS * 3600.0 > 0.2);
        Assert.True(body.BloodTemperature < 311.45);
        Assert.True(body.WaterDeficitKg > 0.15);
    }

    [Fact]
    public void AStudentWithTheirHeadUnderWaterDrowns()
    {
        var grid = Ground(3, 3, 7);
        for (int z = 1; z <= 4; z++)
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
            grid.Set(x, y, z, Water, Room);
        var sim = Start(grid, Room, openSky: true);
        // Feet held on the floor (as if caught): the water reaches over the head.
        var body = Spawn(sim, 1, 1, 1);
        double unconscious = -1;
        while (body.Alive && sim.ElapsedSeconds < 1200)
        {
            sim.Step(50);
            if (unconscious < 0 && !body.Conscious) unconscious = sim.ElapsedSeconds;
            if (sim.Tick % 600 == 0) Report(sim, body, "drowning");
        }
        Report(sim, body, "drowned");
        _out.WriteLine($"unconscious at {unconscious} s, dead at {sim.ElapsedSeconds} s");
        Assert.InRange(unconscious, 90, 240);
        Assert.False(body.Alive);
        Assert.Equal(CauseOfDeath.Hypoxia, body.Cause);
        Assert.InRange(sim.ElapsedSeconds, 300, 900);
    }

    [Fact]
    public void NitrogenSmothersWithoutAWarning()
    {
        var grid = Ground(3, 3, 7);
        var nitrogen = Table.GasIndexOf(Table.Id(BuiltInMaterials.Nitrogen));
        int air = Table.GasIndexOf(Air);
        for (int i = grid.Width * grid.Height; i < grid.Count; i++)
        {
            grid.GasMass[nitrogen][i] = grid.GasMass[air][i];
            grid.GasMass[air][i] = 0.0;
        }
        var sim = Start(grid, Room);
        var body = Spawn(sim, 1, 1, 1);
        double unconscious = -1;
        while (body.Alive && sim.ElapsedSeconds < 1200)
        {
            sim.Step(10);
            if (unconscious < 0 && !body.Conscious) unconscious = sim.ElapsedSeconds;
        }
        _out.WriteLine($"unconscious at {unconscious} s, dead at {sim.ElapsedSeconds} s");
        // Breathing pure nitrogen washes the oxygen out of the blood: faster than holding one's breath.
        Assert.InRange(unconscious, 8, 60);
        Assert.False(body.Alive);
    }

    [Fact]
    public void AFireballBurnsTheSkinItStrikes()
    {
        var sim = Start(Ground(9, 3, 7), Room);
        var body = Spawn(sim, 7, 1, 1);
        double before = Ledger(sim);
        sim.Enqueue(new Magic.CastFireball(1, 1, 3, 1, 0, 0, 300_000));
        sim.Step(30);
        int torsoSkin = body.Plan.LayerStart[body.Plan.TorsoPart];
        _out.WriteLine($"torso skin {body.Temperature[torsoSkin] - 273.15:F0} C burn {body.Burn[torsoSkin]:G3} necrosis {body.Necrosis(torsoSkin):F2} dead skin {body.DeadSkinShare:P1}");
        Assert.True(body.Necrosis(torsoSkin) > 0.5);
        Assert.True(body.Necrosis(body.Plan.LayerStart[body.Plan.TorsoPart] + 4) < 0.01, "the burn went to the organs");
        Assert.True(body.Alive);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-11);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(20, true)]
    public void AFallBreaksLegsOnlyFromHighEnough(int tiles, bool breaks)
    {
        var sim = Start(Ground(3, 3, tiles + 6), Room);
        var body = Spawn(sim, 1, 1, 1 + tiles);
        double before = Ledger(sim), mass = MassOf(sim);
        sim.Step(40);
        Assert.Equal(1, body.Z);
        double legs = body.Trauma[6];
        _out.WriteLine($"from {tiles * 0.5} m: legs {legs:F2} thighs {body.Trauma[4]:F2} torso {body.Trauma[1]:F2} head {body.Trauma[0]:F2}");
        Assert.Equal(breaks, legs >= 1.0);
        Assert.True(body.Alive);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-11);
        Assert.Equal(mass, MassOf(sim), mass * 1e-12);
    }

    [Fact]
    public void FallingStoneCrushesTheSkull()
    {
        var sim = Start(Ground(3, 3, 14), Room);
        var body = Spawn(sim, 1, 1, 1);
        sim.Grid.Set(1, 1, 12, Stone, Room);
        sim.Grid.Structure[sim.Grid.Index(1, 1, 12)] |= WorldGrid.LooseBit;
        sim.Activity.WakeAll();
        double before = Ledger(sim);
        sim.Step(60);
        _out.WriteLine($"head trauma {body.Trauma[0]:F2} alive {body.Alive} cause {body.Cause}");
        Assert.False(body.Alive);
        Assert.Equal(CauseOfDeath.BrainInjury, body.Cause);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-11);
    }

    [Fact]
    public void EveryJouleAndGramOfALifeIsCounted()
    {
        var grid = Ground(4, 3, 7);
        var bread = Table.Id(BuiltInMaterials.Bread);
        grid.SetCondensed(grid.Index(2, 1, 1), bread, 0.5);
        grid.SetCondensed(grid.Index(0, 1, 1), Water, 2.0);
        var sim = Start(grid, Room);
        var body = Spawn(sim, 1, 1, 1);
        double before = Ledger(sim), mass = MassOf(sim);
        sim.Enqueue(new EatFood(body.Id, 2, 1, 1, 0.3));
        sim.Enqueue(new DrinkWater(body.Id, 0, 1, 1, 0.5));
        sim.Step(10);
        Assert.InRange(body.GutCarbKg + body.GutWaterKg, 0.79, 0.8);
        sim.Enqueue(new MoveBody(body.Id, 1, 0));
        sim.Step(6000);
        Report(sim, body, "fed");
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-11);
        Assert.Equal(mass, MassOf(sim), mass * 1e-12);
        // Breathing used oxygen and made carbon dioxide and steam.
        int co2 = Table.GasIndexOf(Table.Id(BuiltInMaterials.CarbonDioxide));
        Assert.True(sim.Grid.GasMass[co2].Sum() > 0.003);
        Assert.True(body.GlycogenKg > 0.4);
    }

    [Fact]
    public void ABodyRestoredFromASnapshotLivesOnIdentically()
    {
        var live = Room3();
        var body = Spawn(live, 1, 1, 1);
        live.Step(300);
        live.Enqueue(new MoveBody(body.Id, 1, 0));
        live.Step(10);
        var restored = Persistence.Snapshot.FromBytes(Persistence.Snapshot.ToBytes(live), Table, StandardSystems.Create(openSky: true));
        Assert.Equal(live.StateHash(), restored.StateHash());
        live.Step(600);
        restored.Step(600);
        Assert.Equal(live.StateHash(), restored.StateHash());
        Assert.Single(restored.Bodies);
    }

    [Fact]
    public void ACorpseCoolsToTheRoom()
    {
        var sim = Room3();
        var body = Spawn(sim, 1, 1, 1);
        body.Alive = false;
        body.HeartStopped = true;
        double start = body.Temperature[body.Plan.VisceraNode];
        sim.Step(36_000);
        double drop = start - body.Temperature[body.Plan.VisceraNode];
        _out.WriteLine($"core organs cooled {drop:F2} K in an hour, skin {body.MeanSkinTemperature - 273.15:F1}");
        Assert.InRange(drop, 0.2, 3.0);
    }
}
