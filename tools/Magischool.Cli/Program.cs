using System;
using System.Diagnostics;
using System.Text;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.WorldGen;

// Headless runner: build a seeded test room, run it, print one z-slice as text.
//   dotnet run --project tools/Magischool.Cli -- --seed moon --ticks 100 --z 1
string seed = "moonhollow";
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
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. Use --seed, --ticks, --z, --bench.");
            return 1;
    }
}

var materials = BuiltInMaterials.CreateTable();
var sim = TestRoomGenerator.Create(seed, materials);
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
        text.Append(materials[grid.Material[grid.Index(x, y, z)]].Glyph);
    text.AppendLine();
}

Console.WriteLine($"seed '{seed}'  tick {sim.Tick} ({sim.ElapsedSeconds:0.0} s)  z {z}  {grid.Width}x{grid.Height}x{grid.Depth}");
Console.Write(text);
Console.WriteLine("Legend: " + string.Join("  ", Array.ConvertAll(BuiltInMaterials.All, m => $"{(m.Glyph == ' ' ? '.' : m.Glyph)} {m.Name}")));
Console.WriteLine($"state hash {sim.StateHash():X16}");

if (bench)
{
    const int benchTicks = 10_000;
    var timer = Stopwatch.StartNew();
    sim.Step(benchTicks);
    timer.Stop();
    Console.WriteLine($"bench: {benchTicks} ticks in {timer.Elapsed.TotalMilliseconds:0} ms");
}
return 0;
