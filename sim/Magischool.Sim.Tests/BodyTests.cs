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
        EnergyAudit.TotalJoules(sim) + sim.EtherMatterKg * Magic.Spellcasting.EtherPerKg - Sys<EtherSystem>(sim).ReservoirInJ
        - Sys<StructureSystem>(sim).ReleasedPotentialJ - Sys<BodySystem>(sim).ReleasedPotentialJ - Sys<Magischool.Sim.Ecology.EcologySystem>(sim).LightJ;

    private static int Part(Body body, string name)
    {
        int p = body.Anatomy.IndexOf(name);
        Assert.True(p >= 0, $"no part '{name}'");
        return p;
    }

    /// <summary>Kills a part outright: every cell in it dead, as if burned through.</summary>
    private static void Destroy(Body body, string name)
    {
        int p = Part(body, name);
        for (int n = body.Anatomy.LayerStart[p]; n < body.Anatomy.LayerStart[p + 1]; n++) body.Burn[n] = 50.0;
    }

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

    /// <summary>A closed room, 1.5 m square, sealed from the sky: nothing comes or goes, so every joule can be counted.</summary>
    private static Simulation Closed3() => Start(Ground(3, 3, 7), Room);

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
        int chestSkin = body.Anatomy.LayerStart[Part(body, "chest")];
        _out.WriteLine($"chest skin {body.Temperature[chestSkin] - 273.15:F0} C burn {body.Burn[chestSkin]:G3} necrosis {body.Necrosis(chestSkin):F2} dead skin {body.DeadSkinShare:P1}");
        Assert.True(body.Necrosis(chestSkin) > 0.5);
        Assert.True(body.Health(Part(body, "heart")) > 0.99, "the burn went to the heart");
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
        Assert.Single(sim.Bodies);
        Assert.Equal(1, body.Z);
        double legs = body.Trauma[Part(body, "left shin")];
        _out.WriteLine($"from {tiles * 0.5} m: shins {legs:F2} thighs {body.Trauma[Part(body, "left thigh")]:F2} abdomen {body.Trauma[Part(body, "abdomen")]:F2} " +
                       $"liver {body.Trauma[Part(body, "liver")]:F2} neck {body.Trauma[Part(body, "neck")]:F2} head {body.Trauma[Part(body, "head")]:F2} brain {body.Trauma[Part(body, "brain")]:F2}");
        Assert.Equal(breaks, legs >= 1.0);
        Assert.Equal(!breaks, BodySystem.CanWalk(body));
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
        _out.WriteLine($"head trauma {body.Trauma[Part(body, "head")]:F2} brain {body.Trauma[Part(body, "brain")]:F2} alive {body.Alive} cause {body.Cause}");
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
        int belly = body.Anatomy.Inner(Part(body, "abdomen")), liver = body.Anatomy.Inner(Part(body, "liver"));
        int skin = body.Anatomy.LayerStart[Part(body, "chest")];
        double start = body.Temperature[belly], liverStart = body.Temperature[liver], skinStart = body.Temperature[skin];
        sim.Step(36_000);
        double drop = start - body.Temperature[belly], liverDrop = liverStart - body.Temperature[liver];
        _out.WriteLine($"in an hour the belly cooled {drop:F2} K, the liver {liverDrop:F2} K, the chest's skin {skinStart - body.Temperature[skin]:F2} K");
        // The core holds its heat for the first hours (the post-mortem plateau), the deepest organs longest.
        Assert.InRange(drop, 0.02, 3.0);
        Assert.True(liverDrop < drop + 0.05);
        Assert.True(skinStart - body.Temperature[skin] > drop);
    }

    [Fact]
    public void AStudentSeesWithEitherEyeAndNotWithNeither()
    {
        var sim = Room3();
        var body = Spawn(sim, 1, 1, 1);
        Assert.Equal(1.0, body.Vision, 3);
        Destroy(body, "left eye");
        Assert.Equal(1.0, body.Vision, 3);
        Destroy(body, "right eye");
        Assert.Equal(0.0, body.Vision, 3);
        Assert.True(body.Conscious);
    }

    [Fact]
    public void OneLungIsEnoughButNoneIsNot()
    {
        var one = Room3();
        var body = Spawn(one, 1, 1, 1);
        one.Bodies[0].Trauma[Part(body, "left lung")] = 1.0;
        one.Step(3000);
        Report(one, body, "one lung");
        Assert.True(body.Conscious);
        Assert.Equal(0.5, body.FunctionLevel(PartFunction.Lung), 3);

        var none = Room3();
        var other = Spawn(none, 1, 1, 1);
        other.Trauma[Part(other, "left lung")] = 1.0;
        other.Trauma[Part(other, "right lung")] = 1.0;
        double unconscious = -1;
        while (other.Alive && none.ElapsedSeconds < 1200)
        {
            none.Step(10);
            if (unconscious < 0 && !other.Conscious) unconscious = none.ElapsedSeconds;
        }
        _out.WriteLine($"no lungs: unconscious at {unconscious} s, dead at {none.ElapsedSeconds} s");
        Assert.InRange(unconscious, 20, 240);
        Assert.False(other.Alive);
        Assert.Equal(CauseOfDeath.Hypoxia, other.Cause);
    }

    [Fact]
    public void ABrokenNeckStopsTheBreathAndTheLegs()
    {
        var sim = Room3();
        var body = Spawn(sim, 1, 1, 1);
        body.Trauma[Part(body, "neck")] = 1.0;
        Assert.False(BodySystem.CanWalk(body));
        while (body.Alive && sim.ElapsedSeconds < 1200) sim.Step(50);
        _out.WriteLine($"dead at {sim.ElapsedSeconds} s of {body.Cause}");
        Assert.False(body.Alive);
        Assert.Equal(CauseOfDeath.Hypoxia, body.Cause);
    }

    [Fact]
    public void AHandCutOffIsRemainsAndEveryGramIsKept()
    {
        var sim = Closed3();
        var body = Spawn(sim, 1, 1, 1);
        sim.Step(100);
        double before = Ledger(sim), mass = MassOf(sim);
        int parts = body.Anatomy.Parts.Length;
        var hand = BodySystem.Sever(sim, body, Part(body, "left hand"));
        Assert.Equal(parts - 1, body.Anatomy.Parts.Length);
        Assert.Equal(-1, body.Anatomy.IndexOf("left hand"));
        Assert.Single(hand.Anatomy.Parts);
        Assert.True(hand.IsRemains);
        Assert.False(hand.Alive);
        Assert.Equal(body.Z + 2, hand.Z);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-12);
        Assert.Equal(mass, MassOf(sim), mass * 1e-12);
        Assert.True(body.Bleed[Part(body, "left forearm")] > 0.0);
        Assert.Equal(0.5, body.FunctionLevel(PartFunction.Hand), 3);

        double handStart = hand.Temperature[hand.Anatomy.Inner(0)];
        sim.Step(3000);
        Report(sim, body, "one-handed");
        double handK = hand.Temperature[hand.Anatomy.Inner(0)];
        _out.WriteLine($"hand fell to z={hand.Z}, its bone at {handK - 273.15:F1} C; blood {body.BloodKg:F2} kg");
        Assert.Equal(1, hand.Z);
        Assert.True(handK < handStart - 0.2, "the cut-off hand cools");
        Assert.True(body.Alive && body.Conscious);
        Assert.True(body.BloodKg < body.Plan.BloodKg - 0.05, "the stump bled");
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-11);
        Assert.Equal(mass, MassOf(sim), mass * 1e-12);

        var restored = Persistence.Snapshot.FromBytes(Persistence.Snapshot.ToBytes(sim), Table, StandardSystems.Create(openSky: false));
        Assert.Equal(sim.StateHash(), restored.StateHash());
        sim.Step(100);
        restored.Step(100);
        Assert.Equal(sim.StateHash(), restored.StateHash());
    }

    [Fact]
    public void ACrushingBlowTearsALimbOff()
    {
        var sim = Room3();
        var body = Spawn(sim, 1, 1, 1);
        BodySystem.Impact(body, Part(body, "right forearm"), 1.0e5);
        sim.Step(1);
        Assert.Equal(-1, body.Anatomy.IndexOf("right forearm"));
        Assert.Equal(-1, body.Anatomy.IndexOf("right hand"));
        var arm = sim.Bodies.Last();
        Assert.Equal(2, arm.Anatomy.Parts.Length);
        Assert.True(arm.IsRemains);
    }

    [Fact]
    public void AHeadCutOffTakesTheLifeWithIt()
    {
        var sim = Room3();
        var body = Spawn(sim, 1, 1, 1);
        var head = BodySystem.Sever(sim, body, Part(body, "head"));
        sim.Step(1);
        Assert.True(body.IsRemains);
        Assert.False(body.Alive);
        Assert.Equal(CauseOfDeath.Dismembered, body.Cause);
        Assert.False(head.IsRemains);
        Assert.False(head.Conscious);
        while (head.Alive && sim.ElapsedSeconds < 1200) sim.Step(50);
        Assert.False(head.Alive);
        Assert.Equal(CauseOfDeath.Hypoxia, head.Cause);
    }

    [Fact]
    public void AThirdEyeGrownFromEtherSees()
    {
        var sim = Closed3();
        var body = Spawn(sim, 1, 1, 1);
        double before = Ledger(sim), mass = MassOf(sim) - sim.EtherMatterKg;
        sim.Enqueue(new Magic.CastGraft(0, 0, 2, body.Id, "left eye", "head", "third eye"));
        sim.Step(1);
        int eye = Part(body, "third eye");
        Assert.Equal(PartFunction.Eye, body.Anatomy.Parts[eye].Function);
        Assert.Equal(0.0075, sim.EtherMatterKg, 12);
        Assert.InRange(body.Temperature[body.Anatomy.LayerStart[eye]], 305.0, 312.0);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-11);
        Assert.Equal(mass, MassOf(sim) - sim.EtherMatterKg, mass * 1e-12);

        Destroy(body, "left eye");
        Destroy(body, "right eye");
        Assert.Equal(1.0, body.Vision, 2);
        Assert.Equal(0.5, body.FunctionLevel(PartFunction.Eye), 2);

        sim.Step(600);
        var restored = Persistence.Snapshot.FromBytes(Persistence.Snapshot.ToBytes(sim), Table, StandardSystems.Create(openSky: false));
        Assert.Equal(sim.StateHash(), restored.StateHash());
        Assert.Equal(eye, restored.Bodies[0].Anatomy.IndexOf("third eye"));
        sim.Step(100);
        restored.Step(100);
        Assert.Equal(sim.StateHash(), restored.StateHash());
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-11);
    }

    [Fact]
    public void ATransmutedLayerKeepsItsEnergy()
    {
        var sim = Closed3();
        var body = Spawn(sim, 1, 1, 1);
        double before = Ledger(sim);
        int hand = Part(body, "right hand");
        BodySystem.ChangeTissue(sim, body, hand, 2, Tissue.Bone);
        Assert.Equal(Tissue.Bone, body.Anatomy.NodeTissue[body.Anatomy.LayerStart[hand] + 2]);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-12);
        sim.Step(300);
        Assert.Equal(before, Ledger(sim), Math.Abs(before) * 1e-11);
    }

    [Fact]
    public void FailedKidneysLetPoisonBuild()
    {
        var sim = Room3();
        var body = Spawn(sim, 1, 1, 1);
        body.Trauma[Part(body, "left kidney")] = 1.0;
        body.Trauma[Part(body, "right kidney")] = 1.0;
        double bladder = body.BladderKg;
        sim.Step(6000);
        _out.WriteLine($"toxins {body.Toxins:G3} after 10 min, bladder {body.BladderKg - bladder:G3} kg");
        Assert.Equal(600.0 / BodySystem.ToxinSeconds, body.Toxins, 1e-6);
        Assert.Equal(bladder, body.BladderKg, 12);
        body.Toxins = 0.9999;
        sim.Step(1000);
        Assert.True(body.HeartStopped);
    }

    [Fact]
    public void WithoutTeethItEatsSlowly()
    {
        var grid = Ground(4, 3, 7);
        var bread = Table.Id(BuiltInMaterials.Bread);
        grid.SetCondensed(grid.Index(2, 1, 1), bread, 0.5);
        var sim = Start(grid, Room);
        var body = Spawn(sim, 1, 1, 1);
        Destroy(body, "teeth");
        sim.Enqueue(new EatFood(body.Id, 2, 1, 1, 0.25));
        sim.Step(1);
        Assert.Equal(0.05, body.GutCarbKg + body.GutWaterKg, 1e-3);
    }
}
