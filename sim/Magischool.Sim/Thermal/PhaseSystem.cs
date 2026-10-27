using System;
using Magischool.Sim.Core;

namespace Magischool.Sim.Thermal;

/// <summary>
/// Layer 3: melting, freezing, boiling and condensing with latent heat.
///
/// A tile that reaches a transition temperature stops changing temperature.
/// Extra energy goes into <see cref="WorldGrid.LatentEnergy"/> instead, and
/// only when the full latent heat (J/kg × the tile's mass) has been absorbed
/// does the material change. Cooling works the same way in reverse, and a
/// half-melted tile that starts cooling gives its stored energy back first.
///
/// Mass is kept through every change, so a tile of ice becomes the same mass
/// of water (a tile 92% full), and a tile of water freezes into ice that
/// needs 109% of a tile. Spreading the extra volume is the fluids layer's job,
/// and the squeeze is the structure layer's.
///
/// Runs after <see cref="ThermalSystem"/> each tick, on awake chunks only, in
/// parallel (each tile is independent). Energy is conserved exactly;
/// <see cref="EnergyAudit"/> counts it.
/// </summary>
public sealed class PhaseSystem : ISimSystem
{
    // A tile that receives enormous energy in one tick can pass through several transitions (ice to steam).
    private const int MaxTransitionsPerTick = 4;

    public string Name => "phase";

    public void Update(Simulation sim)
    {
        var grid = sim.Grid;
        var rules = PhaseRules.For(sim.Materials);
        var activity = sim.Activity;
        var chunks = activity.AwakeChunks();

        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                ProcessTile(grid, rules, grid.Index(x, y, z));
        });
    }

    private static void ProcessTile(WorldGrid grid, PhaseRules rules, int i)
    {
        ushort[] material = grid.Material;
        double[] temperature = grid.Temperature;
        double[] mass = grid.Mass;
        double[] latent = grid.LatentEnergy;
        var table = grid.Materials;

        for (int pass = 0; pass < MaxTransitionsPerTick; pass++)
        {
            ushort m = material[i];
            var up = rules.Upper[m];
            var down = rules.Lower[m];
            double t = temperature[i];
            double s = latent[i];

            bool heating = s > 0.0 || (s == 0.0 && up.Exists && t > up.TemperatureK);
            bool cooling = !heating && (s < 0.0 || (s == 0.0 && down.Exists && t < down.TemperatureK));
            if (!heating && !cooling) break;

            double capacity = mass[i] * table[m].SpecificHeat;
            if (capacity <= 0.0) break;

            var transition = heating ? up : down;
            double energy = s + (t - transition.TemperatureK) * capacity; // signed: + toward up, - toward down
            double needed = transition.LatentPerKg * mass[i];

            if (heating ? energy <= 0.0 : energy >= 0.0)
            {
                // Turned back before finishing: return the stored energy as temperature.
                latent[i] = 0.0;
                temperature[i] = transition.TemperatureK + energy / capacity;
                break;
            }

            if (Math.Abs(energy) < needed)
            {
                latent[i] = energy;
                temperature[i] = transition.TemperatureK;
                break;
            }

            double leftover = heating ? energy - needed : energy + needed;
            material[i] = transition.Into;
            latent[i] = 0.0;
            temperature[i] = transition.TemperatureK + leftover / (mass[i] * table[transition.Into].SpecificHeat);
        }

        // Nothing is colder than absolute zero. Only an outside command can push a tile here;
        // flooring it means that command took out less energy than it asked for.
        if (temperature[i] < 0.0) temperature[i] = 0.0;
    }
}
