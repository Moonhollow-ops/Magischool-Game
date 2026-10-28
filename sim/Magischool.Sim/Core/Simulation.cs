using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Magischool.Sim.Determinism;
using Magischool.Sim.Magic;
using Magischool.Sim.Materials;
using Magischool.Sim.Persistence;

namespace Magischool.Sim.Core;

/// <summary>
/// One physics layer (heat, fluids, fire, ...). Systems run once per tick
/// in a fixed order and may only touch the world through the simulation.
/// Anything random must come from <see cref="Simulation.Rng"/>.
/// </summary>
public interface ISimSystem
{
    string Name { get; }
    void Update(Simulation sim);
}

/// <summary>
/// The deterministic world clock. Real time on screen, but the world itself
/// only ever advances in fixed ticks of <see cref="SecondsPerTick"/>:
/// same starting state + same commands at the same ticks = identical world.
/// </summary>
public sealed class Simulation
{
    public const int TicksPerSecond = 10;
    public const float SecondsPerTick = 1f / TicksPerSecond;

    private readonly ISimSystem[] _systems;
    private readonly Queue<WorldCommand> _pending = new();

    public Simulation(string seed, WorldGrid grid, MaterialTable materials, IEnumerable<ISimSystem>? systems = null)
    {
        if (!ReferenceEquals(grid.Materials, materials))
            throw new ArgumentException("The grid was built with a different material table.", nameof(materials));
        Seed = seed;
        Grid = grid;
        Materials = materials;
        Activity = new ActivityMap(grid.Width, grid.Height, grid.Depth);
        _systems = systems == null ? Array.Empty<ISimSystem>() : new List<ISimSystem>(systems).ToArray();
        Rng = new Pcg32(SeedHash.Derive(seed, "sim"));
    }

    public string Seed { get; }
    public WorldGrid Grid { get; }
    public MaterialTable Materials { get; }
    public IReadOnlyList<ISimSystem> Systems => _systems;

    /// <summary>Which chunks are awake. Part of the world state.</summary>
    public ActivityMap Activity { get; }

    /// <summary>Spells in flight, in the order they were cast. Part of the world state.</summary>
    public List<Projectile> Projectiles { get; } = new();

    /// <summary>Bodies in the world, in the order they came into it. Part of the world state.</summary>
    public List<Bodies.Body> Bodies { get; } = new();

    /// <summary>Id the next body brought into the world gets.</summary>
    public int NextBodyId { get; internal set; } = 1;

    /// <summary>
    /// Net matter crystallised out of the ether since this simulation was created, kg: conjured
    /// matter less etherium burned. The world's mass less this never changes.
    /// </summary>
    public double EtherMatterKg { get; internal set; }

    /// <summary>
    /// How many threads physics may use. Results are identical for any value:
    /// every parallel loop writes only its own tiles from the start-of-tick state.
    /// </summary>
    public ParallelOptions Parallelism { get; set; } = new();

    /// <summary>Number of ticks completed. Tick N's commands apply before tick N's systems run.</summary>
    public long Tick { get; internal set; }

    /// <summary>The sim's only randomness source. A field so systems mutate it in place.</summary>
    public Pcg32 Rng;

    /// <summary>When set, every command applied is recorded with the tick it applied on.</summary>
    public ReplayLog? Recorder { get; set; }

    public double ElapsedSeconds => Tick * (double)SecondsPerTick;

    /// <summary>Queue a change from outside the sim (player, spell, debug tool). It applies at the start of the next tick.</summary>
    public void Enqueue(WorldCommand command) => _pending.Enqueue(command);

    public int PendingCommands => _pending.Count;

    public void Step()
    {
        while (_pending.Count > 0)
        {
            var command = _pending.Dequeue();
            Recorder?.Add(Tick, command);
            command.Apply(this);
        }

        foreach (var system in _systems)
            system.Update(this);

        Tick++;
    }

    public void Step(int ticks)
    {
        for (int i = 0; i < ticks; i++)
            Step();
    }

    /// <summary>A fingerprint of the whole world state, for determinism checks.</summary>
    public ulong StateHash() => StateHasher.Hash(this);
}
