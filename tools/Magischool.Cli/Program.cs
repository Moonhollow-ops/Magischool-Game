using System;
using System.Diagnostics;
using System.Text;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.WorldGen;

// Headless runner: build a seeded test room, run it, print one z-slice as text.
//   dotnet run --project tools/Magischool.Cli -- --seed moon --ticks 100 --z 1
//   --size 400x400x12 sets the world size; --heat prints temperatures in °C; --bench times 1,000 ticks;
//   --no-sleep keeps every chunk awake (the worst case for performance).
string seed = "moonhollow";
int width = 48, height = 32, depth = 4;
bool heat = false;
bool noSleep = false;
int ticks = 0;
int z = TestRoomGenerator.FloorZ;
bool bench = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed": seed = args[++i]; break;
        case "--ticks": ticks = int.Parse(args[++i]); break;
        case "--z": z = int.Parse(args[++i]); break;
        case "--bench": bench = true; break;
        case "--heat": heat = true; break;
        case "--no-sleep": noSleep = true; break;
        case "--size":
            var parts = args[++i].Split('x');
            (width, height, depth) = (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. Use --seed, --ticks, --z, --size, --heat, --no-sleep, --bench.");
            return 1;
    }
}

var materials = BuiltInMaterials.CreateTable();
var systems = noSleep
    ? Array.FindAll(StandardSystems.Create(), s => s is not ActivitySystem) // every chunk stays awake: worst case
    : StandardSystems.Create();
var sim = TestRoomGenerator.Create(seed, materials, width, height, depth, systems);
if (z < 0 || z >= sim.Grid.Depth)
{
    Console.Error.WriteLine($"z must be between 0 and {sim.Grid.Depth - 1}.");
    return 1;
}

sim.Step(ticks);

var grid = sim.Grid;
var text = new StringBuilder();
for (int y = 0; y < grid.Height; y++)
{
    for (int x = 0; x < grid.Width; x++)
    {
        int i = grid.Index(x, y, z);
        text.Append(heat ? HeatGlyph(grid.Temperature[i]) : materials[grid.Material[i]].Glyph);
    }
    text.AppendLine();
}

Console.WriteLine($"seed '{seed}'  tick {sim.Tick} ({sim.ElapsedSeconds:0.0} s)  z {z}  {grid.Width}x{grid.Height}x{grid.Depth}");
Console.Write(text);
Console.WriteLine("Legend: " + string.Join("  ", Array.ConvertAll(BuiltInMaterials.All, m => $"{(m.Glyph == ' ' ? '.' : m.Glyph)} {m.Name}")));
Console.WriteLine($"state hash {sim.StateHash():X16}");

if (bench)
{
    const int benchTicks = 1_000;
    var timer = Stopwatch.StartNew();
    sim.Step(benchTicks);
    timer.Stop();
    double perTick = timer.Elapsed.TotalMilliseconds / benchTicks;
    Console.WriteLine($"bench: {benchTicks} ticks in {timer.Elapsed.TotalMilliseconds:0} ms ({perTick:0.000} ms/tick, {grid.Count:N0} tiles, {sim.Activity.AwakeCount}/{sim.Activity.Count} chunks awake)");
}
return 0;

// One character per 100 °C band: . below 0 °C, 0-9 for 0-999 °C, + above.
static char HeatGlyph(double kelvin)
{
    double celsius = kelvin - 273.15;
    if (celsius < 0) return '.';
    if (celsius >= 1000) return '+';
    return (char)('0' + (int)(celsius / 100));
}
