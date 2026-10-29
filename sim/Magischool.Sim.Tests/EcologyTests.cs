using System;
using System.Linq;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Ecology;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Perception;
using Magischool.Sim.Persistence;
using Magischool.Sim.Thermal;
using Magischool.Sim.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Magischool.Sim.Tests;

public class EcologyTests
{
    private readonly ITestOutputHelper _out;
    public EcologyTests(ITestOutputHelper output) => _out = output;

    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private static readonly ushort Soil = Table.Id(BuiltInMaterials.Soil);
    private static readonly ushort Water = Table.Id(BuiltInMaterials.Water);
    private static readonly ushort Grass = Table.Id(BuiltInMaterials.Grass);
    private static readonly ushort DeadGrass = Table.Id(BuiltInMaterials.DeadGrass);
    private static readonly int CO2 = Table.GasIndexOf(Table.Id(BuiltInMaterials.CarbonDioxide));
    private static readonly int AirGas = Table.GasIndexOf(Air);
    private const double Room = BuiltInMaterials.RoomTemperatureK;
    private const int TicksPerDay = 864_000;
    private static readonly MaterialDef SoilDef = Table[Soil];

    /// <summary>A column of soil <paramref name="soilTiles"/> deep under open-air gases, its pores at θ.</summary>
    private static WorldGrid Column(int soilTiles, int depth, double theta, int width = 1, int height = 1)
    {
        var grid = new WorldGrid(width, height, depth, Table);
        grid.Fill(Air, Room);
        for (int z = 0; z < soilTiles; z++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            grid.Set(x, y, z, Soil, Room);
            Wet(grid, grid.Index(x, y, z), theta);
        }
        Atmosphere.Mix(grid, Atmosphere.Outdoor(Room));
        Array.Fill(grid.EtherAmbient, 1e6);
        return grid;
    }

    private static void Wet(WorldGrid grid, int i, double theta)
    {
        grid.PoreWater[i] = theta * EcologySystem.SoilVolume(grid, i) * EcologySystem.WaterDensity;
        grid.PoreWaterJ[i] = grid.PoreWater[i] * JPerKg(Water, Room);
    }

    private static double JPerKg(ushort m, double k) => Table[m].SpecificHeat * k + EnergyAudit.OffsetsFor(Table)[m];

    private static void Sow(WorldGrid grid, int i, double kg) => EcologySystem.Plant(grid, i, Grass, kg, kg * JPerKg(Grass, Room));

    /// <summary>A world where only the slow ecology runs: nothing moves the gas, so it can be stepped for weeks.</summary>
    private static (Simulation Sim, EcologySystem Eco) EcologyOnly(WorldGrid grid, double lux)
    {
        var eco = new EcologySystem();
        var sim = new Simulation("ecology-test", grid, Table, new ISimSystem[] { eco });
        sim.Enqueue(new SetSkyLight(lux));
        return (sim, eco);
    }

    private static double TotalKg(WorldGrid grid)
    {
        double kg = 0.0;
        for (int i = 0; i < grid.Count; i++)
        {
            kg += grid.Mass[i] + grid.PoreWater[i];
            foreach (var species in grid.GasMass) kg += species[i];
        }
        return kg;
    }

    private static double Theta(WorldGrid grid, int i) => EcologySystem.Moisture(grid, i);

    private static double Dry(WorldGrid grid, int i) => grid.Mass[i] * EcologySystem.DryShare(Table[grid.Material[i]]);

    // ---------------------------------------------------------------- soil water

    [Fact]
    public void SoakedSoilDrainsTowardFieldCapacity()
    {
        var grid = Column(soilTiles: 6, depth: 7, theta: SoilDef.FieldCapacity);
        int top = grid.Index(0, 0, 5);
        Wet(grid, top, SoilDef.Porosity);
        var (sim, eco) = EcologyOnly(grid, 0.0);
        double kg0 = TotalKg(grid), e0 = EnergyAudit.TotalJoules(grid);

        sim.Step(3 * TicksPerDay);

        _out.WriteLine(string.Join(" ", Enumerable.Range(0, 6).Select(z => $"{Theta(grid, grid.Index(0, 0, z)):F3}")));
        // Two days on, it holds about what field capacity means: wetter than that drains, slowly.
        Assert.InRange(Theta(grid, top), SoilDef.FieldCapacity - 0.02, SoilDef.FieldCapacity + 0.04);
        Assert.True(Theta(grid, grid.Index(0, 0, 4)) > SoilDef.FieldCapacity, "the water went nowhere below");
        Assert.Equal(kg0, TotalKg(grid), kg0 * 1e-12);
        Assert.Equal(e0, EnergyAudit.TotalJoules(grid) - eco.LightJ, Math.Abs(e0) * 1e-12);
    }

    [Fact]
    public void WaterLyingOnSoilSoaksIn()
    {
        var grid = Column(soilTiles: 2, depth: 4, theta: 0.15);
        int pond = grid.Index(0, 0, 2);
        grid.Set(0, 0, 2, Water, Room);
        grid.Mass[pond] = 10.0;
        var (sim, eco) = EcologyOnly(grid, 0.0);
        double pore0 = grid.PoreWater[1], kg0 = TotalKg(grid), e0 = EnergyAudit.TotalJoules(grid);

        sim.Step(36_000); // an hour

        double soaked = grid.PoreWater[1] + grid.PoreWater[0] - pore0 - 0.15 * 0.125 * 1000;
        _out.WriteLine($"pond {(grid.HasCondensed(pond) ? grid.Mass[pond] : 0):F3} kg, soaked {soaked:F3} kg");
        Assert.True(soaked > 1.0, $"only {soaked:F3} kg soaked in");
        Assert.Equal(kg0, TotalKg(grid), kg0 * 1e-12);
        Assert.Equal(e0, EnergyAudit.TotalJoules(grid) - eco.LightJ, Math.Abs(e0) * 1e-12);
    }

    // ---------------------------------------------------------------- plants

    /// <summary>A grass sward on damp soil with carbon dioxide rich enough to last the run in its one tile of air.</summary>
    private static (WorldGrid Grid, int Sward) Meadow(double theta, double c = 0.01)
    {
        var grid = Column(soilTiles: 2, depth: 4, theta: theta);
        int sward = grid.Index(0, 0, 2);
        double nitrogen = 2.415 * c;
        Atmosphere.Mix(grid, new[] { (BuiltInMaterials.Air, 1.0 - c - nitrogen), (BuiltInMaterials.Nitrogen, nitrogen), (BuiltInMaterials.CarbonDioxide, c) });
        Sow(grid, sward, 0.1);
        return (grid, sward);
    }

    [Fact]
    public void GrassGrowsInLightDrawingDownCarbonDioxideAndMakingOxygen()
    {
        var (grid, sward) = Meadow(SoilDef.FieldCapacity, c: 0.05);
        var (sim, eco) = EcologyOnly(grid, LightField.OvercastDayLux * 5);
        double dry0 = Dry(grid, sward), co2 = grid.GasMass[CO2][sward], air = grid.GasMass[AirGas][sward];
        double kg0 = TotalKg(grid), e0 = EnergyAudit.TotalJoules(grid);

        sim.Step(TicksPerDay / 8);

        double grown = Dry(grid, sward) - dry0;
        _out.WriteLine($"grew {grown * 1000:F2} g dry from {dry0 * 1000:F1} g; CO2 {co2 * 1000:F3} -> {grid.GasMass[CO2][sward] * 1000:F3} g; light {eco.LightJ / 1e3:F0} kJ");
        Assert.Equal(Grass, grid.Material[sward]);
        Assert.True(grown > 0.03 * dry0, $"grew only {grown * 1000:F2} g");
        Assert.True(grid.GasMass[CO2][sward] < co2 - 0.5 * grown, "carbon dioxide was not drawn down");
        Assert.True(grid.GasMass[AirGas][sward] > air, "no oxygen was given back");
        Assert.True(eco.LightJ > grown * 15e6, "the energy it stored did not come from the light");
        Assert.Equal(kg0, TotalKg(grid), kg0 * 1e-12);
        Assert.Equal(e0, EnergyAudit.TotalJoules(grid) - eco.LightJ, Math.Abs(e0) * 1e-12);
    }

    [Fact]
    public void GrassInTheDarkLivesOnItselfAndBreathesOutCarbonDioxide()
    {
        var (grid, sward) = Meadow(SoilDef.FieldCapacity);
        var (sim, eco) = EcologyOnly(grid, 0.0);
        double dry0 = Dry(grid, sward), co2 = grid.GasMass[CO2][sward], e0 = EnergyAudit.TotalJoules(grid);

        sim.Step(2 * TicksPerDay);

        double lost = dry0 - Dry(grid, sward);
        _out.WriteLine($"lost {lost / dry0:P2} of its dry mass in two dark days");
        // 1.5% a day at 20 °C.
        Assert.InRange(lost / dry0, 0.02, 0.04);
        Assert.True(grid.GasMass[CO2][sward] > co2);
        Assert.Equal(0.0, eco.LightJ);
        Assert.Equal(e0, EnergyAudit.TotalJoules(grid), Math.Abs(e0) * 1e-12);
    }

    [Fact]
    public void DroughtKillsGrassAndTheDeadGrassRotsOnceItIsDamp()
    {
        var (grid, sward) = Meadow(SoilDef.WiltingPoint);
        var (sim, eco) = EcologyOnly(grid, LightField.OvercastDayLux);
        double kg0 = TotalKg(grid), e0 = EnergyAudit.TotalJoules(grid);

        sim.Step(15 * TicksPerDay);
        Assert.Equal(DeadGrass, grid.Material[sward]);

        for (int z = 0; z < 2; z++) Wet(grid, grid.Index(0, 0, z), SoilDef.FieldCapacity);
        kg0 = TotalKg(grid);
        e0 = EnergyAudit.TotalJoules(grid) - eco.LightJ;
        double dead = grid.Mass[sward], co2 = grid.GasMass[CO2][sward];
        sim.Step(30 * TicksPerDay);

        double rotted = 1.0 - (grid.HasCondensed(sward) ? grid.Mass[sward] : 0.0) / dead;
        _out.WriteLine($"{rotted:P1} of the dead grass rotted in a damp month");
        Assert.InRange(rotted, 0.1, 0.6);
        Assert.True(grid.GasMass[CO2][sward] > co2);
        Assert.Equal(kg0, TotalKg(grid), kg0 * 1e-12);
        Assert.Equal(e0, EnergyAudit.TotalJoules(grid) - eco.LightJ, Math.Abs(e0) * 1e-12);
    }

    [Fact]
    public void SeedlingsArePlantedOnlyInOpenAirOverSoil()
    {
        var grid = Column(soilTiles: 1, depth: 3, theta: SoilDef.FieldCapacity, width: 2);
        grid.Set(1, 0, 0, Stone, Room);
        var (sim, _) = EcologyOnly(grid, 0.0);
        sim.Enqueue(new PlantSeedling(0, 0, 1, BuiltInMaterials.Grass));
        sim.Enqueue(new PlantSeedling(1, 0, 1, BuiltInMaterials.Grass));
        sim.Enqueue(new PlantSeedling(0, 0, 0, BuiltInMaterials.Grass));
        sim.Step();
        Assert.Equal(Grass, grid.Material[grid.Index(0, 0, 1)]);
        // Less the little it has respired in its first ten seconds.
        Assert.InRange(Dry(grid, grid.Index(0, 0, 1)), 0.999 * EcologySystem.SeedlingDryKg, EcologySystem.SeedlingDryKg);
        Assert.False(grid.HasCondensed(grid.Index(1, 0, 1)));
        Assert.Equal(Soil, grid.Material[grid.Index(0, 0, 0)]);
    }

    // ---------------------------------------------------------------- creatures

    /// <summary>A meadow of grass on damp soil, open air above, in daylight, with every layer running.</summary>
    private static Simulation Field(int width, int height, bool grass, int depth = 3)
    {
        var grid = Column(soilTiles: 1, depth, theta: SoilDef.FieldCapacity, width, height);
        if (grass)
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Sow(grid, grid.Index(x, y, 1), 0.2);
        Atmosphere.Balance(grid, Air, Room);
        EtherSystem.Balance(grid);
        var sim = new Simulation("field", grid, Table, StandardSystems.Create(openSky: false));
        sim.Enqueue(new SetSkyLight(LightField.OvercastDayLux));
        return sim;
    }

    private static Body Spawn(Simulation sim, int x, int y, string plan)
    {
        sim.Enqueue(new SpawnBody(x, y, 1, plan, BodyPlans.DefaultClo(plan)));
        sim.Step();
        return sim.Bodies.Last();
    }

    [Fact]
    public void AHungryRabbitGrazes()
    {
        var sim = Field(5, 5, grass: true);
        var rabbit = Spawn(sim, 2, 2, BodyPlans.Rabbit.Key);
        rabbit.GlycogenKg *= 0.25;
        double grass0 = Enumerable.Range(0, sim.Grid.Count).Where(i => sim.Grid.Material[i] == Grass).Sum(i => sim.Grid.Mass[i]);
        double gut0 = rabbit.GutCarbKg;

        sim.Step(600);

        double grass = Enumerable.Range(0, sim.Grid.Count).Where(i => sim.Grid.Material[i] == Grass).Sum(i => sim.Grid.Mass[i]);
        _out.WriteLine($"ate {(grass0 - grass) * 1000:F1} g of grass; gut carbohydrate {gut0 * 1000:F1} -> {rabbit.GutCarbKg * 1000:F1} g");
        Assert.True(rabbit.Alive && rabbit.Conscious);
        Assert.True(grass0 - grass > 0.005, "it hardly ate");
        Assert.True(rabbit.GutCarbKg > gut0 + 0.001);
    }

    [Fact]
    public void ARabbitBoltsFromAPersonItSees()
    {
        var sim = Field(20, 3, grass: false, depth: 6);
        var rabbit = Spawn(sim, 8, 1, BodyPlans.Rabbit.Key);
        // Behind it and a little aside: a person's eyes see forward, but a rabbit's see nearly all
        // round, all but straight behind.
        rabbit.FacingX = 1; rabbit.FacingY = 0;
        Spawn(sim, 5, 0, BodyPlans.Human.Key);

        sim.Step(30);

        _out.WriteLine($"rabbit at x={rabbit.X}; bodies {sim.Bodies.Count}; saw " + string.Join(", ", rabbit.Percepts.Select(p => $"{p.Kind} {p.Subject} {p.Strength:F1} @{p.LastTick}")));
        Assert.True(rabbit.X >= 12, $"it only got to x={rabbit.X}");
        Assert.True(InstinctSystem.Fright(sim, rabbit, out double fx, out _) && fx < rabbit.X);
    }

    [Fact]
    public void ARabbitDoesNotFearRabbits()
    {
        var sim = Field(8, 8, grass: true);
        var a = Spawn(sim, 3, 3, BodyPlans.Rabbit.Key);
        Spawn(sim, 4, 3, BodyPlans.Rabbit.Key);
        sim.Step(50);
        Assert.False(InstinctSystem.Fright(sim, a, out _, out _));
    }

    [Fact]
    public void AFedRabbitWandersAndATiredOneSleeps()
    {
        var sim = Field(12, 12, grass: false);
        var rabbit = Spawn(sim, 6, 6, BodyPlans.Rabbit.Key);
        var seen = new System.Collections.Generic.HashSet<(int, int)>();
        for (int t = 0; t < 3000; t++)
        {
            sim.Step();
            seen.Add((rabbit.X, rabbit.Y));
        }
        _out.WriteLine($"visited {seen.Count} tiles");
        Assert.True(seen.Count >= 4, $"visited only {seen.Count} tiles");
        Assert.True(rabbit.Alive && rabbit.Conscious);

        rabbit.SleepPressure = 1.1;
        sim.Step(2);
        Assert.True(rabbit.Asleep);
    }

    [Fact]
    public void ARabbitWontStepIntoWater()
    {
        Assert.False(InstinctSystem.Safe(PondEdge(), 1, 0, 1));
        Assert.True(InstinctSystem.Safe(PondEdge(), 0, 0, 1));

        static WorldGrid PondEdge()
        {
            var grid = Column(soilTiles: 1, depth: 3, theta: 0.2, width: 2);
            grid.Set(1, 0, 1, Water, Room);
            return grid;
        }
    }

    // ---------------------------------------------------------------- the living test room

    private static Simulation Living(string seed)
    {
        var sim = TestRoomGenerator.Create(seed, Table, systems: StandardSystems.Create());
        TestRoomGenerator.Populate(sim);
        return sim;
    }

    [Fact]
    public void ThePopulatedRoomIsDeterministicAndSurvivesASaveAndLoad()
    {
        var a = Living("meadow");
        var b = Living("meadow");
        Assert.Equal(TestRoomGenerator.Rabbits, a.Bodies.Count);
        Assert.Contains(Enumerable.Range(0, a.Grid.Count), i => a.Grid.Material[i] == Grass);
        a.Step(250);
        b.Step(250);
        Assert.Equal(a.StateHash(), b.StateHash());

        var restored = Snapshot.FromBytes(Snapshot.ToBytes(a), Table, StandardSystems.Create());
        Assert.Equal(a.StateHash(), restored.StateHash());
        a.Step(250);
        restored.Step(250);
        Assert.Equal(a.StateHash(), restored.StateHash());
    }
}
