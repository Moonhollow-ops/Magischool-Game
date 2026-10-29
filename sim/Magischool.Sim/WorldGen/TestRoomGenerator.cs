using System;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Ecology;
using Magischool.Sim.Determinism;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.WorldGen;

/// <summary>
/// A small seeded sandbox for testing physics layers: solid ground, a walled
/// stone room with a wooden door, a capped lead water pipe, scattered props,
/// a pond, still air, an ordinary ether field, and outside the room a ley node
/// in the ground with an aether crystal on it, slowly charging toward rupture.
/// Layout varies with the seed; the same seed always gives the same room.
/// </summary>
public static class TestRoomGenerator
{
    public const int GroundZ = 0;
    public const int FloorZ = 1;

    /// <summary>Ether potential of an ordinary place, J/m³.</summary>
    public const double OrdinaryEther = 1e6;

    /// <summary>Ether potential of the ley node, J/m³: fifty times richer, welling up within a second.</summary>
    public const double LeyNodeEther = 5e7;

    public static Simulation Create(string seed, MaterialTable materials, int width = 48, int height = 32, int depth = 4,
        params ISimSystem[] systems)
    {
        var grid = Generate(seed, materials, width, height, depth);
        return new Simulation(seed, grid, materials, systems);
    }

    /// <summary>How deep the world to play in is, tiles: room to stand a student up with sky over their head.</summary>
    public const int PlayDepth = 7;

    /// <summary>How high the room's walls are in the world to play in, tiles: two metres, over a student's eyes.</summary>
    public const int PlayWallTiles = 4;

    /// <summary>
    /// The test room deep enough to stand in, with walls a student can't see over and a doorway with a
    /// wooden sill in place of the door.
    /// </summary>
    public static Simulation CreateForPlay(string seed, MaterialTable materials, params ISimSystem[] systems) =>
        new(seed, Generate(seed, materials, 48, 32, PlayDepth, PlayWallTiles), materials, systems);

    public static WorldGrid Generate(string seed, MaterialTable materials, int width = 48, int height = 32, int depth = 4, int wallTiles = 1)
    {
        if (width < 16 || height < 12 || depth < 2)
            throw new ArgumentOutOfRangeException(nameof(width), "The test room needs at least 16 x 12 x 2 tiles.");

        var rng = new Pcg32(SeedHash.Derive(seed, "worldgen"));
        var grid = new WorldGrid(width, height, depth, materials);
        float room = BuiltInMaterials.RoomTemperatureK;

        ushort air = materials.Id(BuiltInMaterials.Air);
        ushort stone = materials.Id(BuiltInMaterials.Stone);
        ushort soil = materials.Id(BuiltInMaterials.Soil);
        ushort wood = materials.Id(BuiltInMaterials.Wood);
        ushort iron = materials.Id(BuiltInMaterials.Iron);
        ushort lead = materials.Id(BuiltInMaterials.Lead);
        ushort water = materials.Id(BuiltInMaterials.Water);
        ushort ice = materials.Id(BuiltInMaterials.Ice);
        ushort crystal = materials.Id(BuiltInMaterials.Crystal);

        grid.Fill(air, room);

        // Ground: stone with soil patches.
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            grid.Set(x, y, GroundZ, rng.Chance(0.25f) ? soil : stone, room);

        // Room walls, inset from the map edge so there is outside space to test spreading.
        int x0 = rng.NextInt(2, 5), y0 = rng.NextInt(2, 4);
        int x1 = width - rng.NextInt(3, 6), y1 = height - rng.NextInt(3, 5);
        int wallTop = Math.Min(depth - 1, FloorZ + Math.Max(1, wallTiles) - 1);
        for (int z = FloorZ; z <= wallTop; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                grid.Set(x, y0, z, stone, room);
                grid.Set(x, y1, z, stone, room);
            }
            for (int y = y0; y <= y1; y++)
            {
                grid.Set(x0, y, z, stone, room);
                grid.Set(x1, y, z, stone, room);
            }
        }

        // Wooden door in the bottom wall: a sill, open above it, where the walls are tall.
        int doorX = rng.NextInt(x0 + 2, x1 - 1);
        grid.Set(doorX, y1, FloorZ, wood, room);
        for (int z = FloorZ + 1; z <= wallTop; z++) grid.Set(doorX, y1, z, air, room);

        // A capped lead pipe full of water along one inner wall: lead, water, lead.
        int pipeY = y0 + 2;
        for (int x = x0 + 1; x <= x1 - 1; x++)
        {
            bool cap = x == x0 + 1 || x == x1 - 1;
            grid.Set(x, pipeY - 1, FloorZ, lead, room);
            grid.Set(x, pipeY, FloorZ, cap ? lead : water, room);
            grid.Set(x, pipeY + 1, FloorZ, lead, room);
            // Laid on stone, so the water doesn't soak away into soil.
            grid.Set(x, pipeY, GroundZ, stone, room);
        }

        // Props inside the room, never on the pipe rows.
        int propYMin = pipeY + 3;
        for (int i = 0; i < 6; i++)
            PlaceBlock(ref rng, grid, x0 + 1, propYMin, x1 - 1, y1 - 1, wood, room, 1, 3);
        PlaceBlock(ref rng, grid, x0 + 1, propYMin, x1 - 1, y1 - 1, iron, room, 1, 2);
        PlaceBlock(ref rng, grid, x0 + 1, propYMin, x1 - 1, y1 - 1, ice, 263.15f, 2, 3);

        // A pond outside the room, sunk into the ground.
        int pondX = rng.NextInt(1, width - 4), pondY = rng.NextInt(0, 2) == 0 ? 0 : height - 2;
        for (int y = pondY; y < Math.Min(height, pondY + 2); y++)
        for (int x = pondX; x < pondX + 4; x++)
            grid.Set(x, y, GroundZ, water, 283.15f);

        // A ley node in the ground in the corner beyond the room's top wall, with an aether crystal
        // two tiles along from it on a stone kerb. The crystal fills past its breakdown in a minute or two.
        Array.Fill(grid.EtherAmbient, OrdinaryEther);
        int nodeX = width - 2, nodeY = 0;
        int node = grid.Index(nodeX, nodeY, GroundZ);
        grid.Set(nodeX, nodeY, GroundZ, stone, room);
        grid.EtherAmbient[node] = LeyNodeEther;
        grid.EtherRefillRate[node] = 1.0;
        grid.Set(nodeX, nodeY, FloorZ, stone, room);
        grid.Set(nodeX - 1, nodeY, FloorZ, stone, room);
        grid.Set(nodeX - 2, nodeY, FloorZ, crystal, room);

        Atmosphere.Balance(grid, air, room);
        EtherSystem.Balance(grid);
        return grid;
    }

    /// <summary>Rabbits <see cref="Populate"/> lets loose.</summary>
    public const int Rabbits = 3;

    /// <summary>
    /// Brings a test room to life: open-air gases (carbon dioxide and some humidity) in the world
    /// and its sky, the soil damp to field capacity, grass on most of the soil open to the air, and
    /// a few rabbits outside. World generation: the mass and energy it adds are not counted.
    /// </summary>
    public static void Populate(Simulation sim)
    {
        var grid = sim.Grid;
        var table = sim.Materials;
        var rng = new Pcg32(SeedHash.Derive(sim.Seed, "life"));
        float room = BuiltInMaterials.RoomTemperatureK;
        var outdoor = Atmosphere.Outdoor(room);
        Atmosphere.Mix(grid, outdoor);
        Atmosphere.Balance(grid, table.Id(BuiltInMaterials.Air), room);
        sim.SkyGas = outdoor;

        ushort water = table.Id(BuiltInMaterials.Water), grass = table.Id(BuiltInMaterials.Grass);
        double waterJPerKg = table[water].SpecificHeat * room + EnergyAudit.OffsetsFor(table)[water];
        double grassJPerKg = table[grass].SpecificHeat * room + EnergyAudit.OffsetsFor(table)[grass];
        int plane = grid.Width * grid.Height;
        for (int i = 0; i < grid.Count; i++)
        {
            double v = EcologySystem.SoilVolume(grid, i);
            if (v <= 0.0) continue;
            grid.PoreWater[i] = table[grid.Material[i]].FieldCapacity * v * EcologySystem.WaterDensity;
            grid.PoreWaterJ[i] = grid.PoreWater[i] * waterJPerKg;
            int above = i + plane;
            if (above >= grid.Count || grid.HasCondensed(above) || !rng.Chance(0.8f)) continue;
            // A sward of 25 to 100 g of dry grass on the tile's quarter square metre.
            double kg = (0.025 + 0.075 * rng.NextFloat()) / EcologySystem.DryShare(table[grass]);
            EcologySystem.Plant(grid, above, grass, kg, kg * grassJPerKg);
        }

        for (int n = 0, tries = 0; n < Rabbits && tries < 200; tries++)
        {
            int x = rng.NextInt(0, grid.Width), y = rng.NextInt(0, grid.Height);
            int i = grid.Index(x, y, FloorZ);
            if (grid.HasCondensed(i) && grid.Materials[grid.Material[i]].Phase != Phase.Solid) continue;
            if (grid.HasCondensed(i) && grid.FillFraction(i) >= 0.5) continue;
            int ground = grid.Index(x, y, GroundZ);
            if (grid.Materials[grid.Material[ground]].Phase != Phase.Solid) continue;
            int count = sim.Bodies.Count;
            new SpawnBody(x, y, FloorZ, BodyPlans.Rabbit.Key, BodyPlans.DefaultClo(BodyPlans.Rabbit.Key)).Apply(sim);
            if (sim.Bodies.Count > count) n++;
        }
    }

    /// <summary>
    /// Stands a student outside the room, on firm ground as near the middle of the bottom edge as
    /// there is room, for a player to play. Returns their body id, or -1 if nowhere would do.
    /// </summary>
    public static int PlacePlayer(Simulation sim)
    {
        var grid = sim.Grid;
        int cx = grid.Width / 2, cy = grid.Height - 2;
        for (int r = 0; r < Math.Max(grid.Width, grid.Height); r++)
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            int x = cx + dx, y = cy + dy;
            if (!grid.InBounds(x, y, FloorZ)) continue;
            int feet = grid.Index(x, y, FloorZ);
            if (grid.HasCondensed(feet) && !grid.Materials[grid.Material[feet]].IsPlant) continue;
            if (grid.Materials[grid.Material[grid.Index(x, y, GroundZ)]].Phase != Phase.Solid) continue;
            int id = sim.NextBodyId;
            new SpawnBody(x, y, FloorZ, BodyPlans.Human.Key).Apply(sim);
            if (sim.NextBodyId > id) return id;
        }
        return -1;
    }

    private static void PlaceBlock(ref Pcg32 rng, WorldGrid grid, int minX, int minY, int maxX, int maxY,
        ushort material, double temperatureK, int minSize, int maxSize)
    {
        if (maxX <= minX || maxY <= minY) return;
        int w = rng.NextInt(minSize, maxSize + 1);
        int h = rng.NextInt(minSize, maxSize + 1);
        int x = rng.NextInt(minX, Math.Max(minX + 1, maxX - w + 1));
        int y = rng.NextInt(minY, Math.Max(minY + 1, maxY - h + 1));
        for (int dy = 0; dy < h && y + dy <= maxY; dy++)
        for (int dx = 0; dx < w && x + dx <= maxX; dx++)
            grid.Set(x + dx, y + dy, FloorZ, material, temperatureK);
    }
}
