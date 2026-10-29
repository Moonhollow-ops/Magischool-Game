using System;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Perception;

namespace Magischool.Sim.Ecology;

/// <summary>
/// What instinctive creatures (a rabbit) do each tick, from what they have seen, heard and feel:
/// bolt from what frightens them, sleep when tired, drink when thirsty, graze when hungry, and
/// otherwise amble about. It acts only through the bodies' own commands (step, eat, drink, sleep),
/// so everything it does is paid for by their bodies and kept by the world's physics. It keeps no
/// state of its own: what they remember is their percepts.
/// </summary>
public sealed class InstinctSystem : ISimSystem
{
    public string Name => "instinct";

    /// <summary>How long a fright lasts after the last sight or sound of it, ticks (2 s).</summary>
    public const int FrightTicks = 20;

    /// <summary>How far they look for food and water, tiles.</summary>
    public const int SearchTiles = 8;

    /// <summary>A sound this much over what they can just hear startles them, dB.</summary>
    public const double StartleDb = 20.0;

    /// <summary>Grass a bite takes, kg; water a sip takes, kg.</summary>
    public const double BiteKg = 5e-5, SipKg = 5e-4;

    /// <summary>Hunger past which they graze, and how full the gut may get first.</summary>
    public const double GrazeHunger = 0.1, GutFull = 0.6;

    public const double DrinkThirst = 0.3;

    /// <summary>Chance a tick that a resting creature takes an idle step.</summary>
    public const float AmbleChance = 0.02f;

    public void Update(Simulation sim)
    {
        foreach (var body in sim.Bodies)
        {
            if (!body.Plan.Instinctive || !body.Alive || body.HeartStopped || body.IsRemains) continue;
            Act(sim, body);
        }
    }

    private static void Act(Simulation sim, Body body)
    {
        bool afraid = Fright(sim, body, out double fx, out double fy);
        if (body.Asleep)
        {
            // A fright wakes it, if it has slept enough to wake by choice.
            if (afraid) new SetAsleep(body.Id, false).Apply(sim);
            if (body.Asleep) return;
        }
        if (!body.Conscious) return;

        if (afraid)
        {
            Step(sim, body, body.X + 0.5 - fx, body.Y + 0.5 - fy);
            return;
        }
        if (body.SleepPressure >= 1.0)
        {
            new SetAsleep(body.Id, true).Apply(sim);
            return;
        }

        bool ambleTick = (sim.Tick + body.Id) % body.Plan.AmbleTicks == 0;
        var grid = sim.Grid;
        if (body.Thirst >= DrinkThirst)
        {
            int water = Nearest(grid, body, potable: true, reach: true);
            if (water >= 0)
            {
                var (x, y, z) = grid.Coordinates(water);
                new DrinkWater(body.Id, x, y, z, SipKg).Apply(sim);
                return;
            }
            if (ambleTick && Toward(sim, body, Nearest(grid, body, potable: true, reach: false))) return;
        }

        double gut = (body.GutCarbKg + body.GutWaterKg) / body.Plan.GutMaxKg;
        if (body.Hunger >= GrazeHunger && gut < GutFull)
        {
            int food = Nearest(grid, body, potable: false, reach: true);
            if (food >= 0)
            {
                var (x, y, z) = grid.Coordinates(food);
                new EatFood(body.Id, x, y, z, BiteKg).Apply(sim);
                return;
            }
            if (ambleTick && Toward(sim, body, Nearest(grid, body, potable: false, reach: false))) return;
        }

        if (ambleTick && sim.Rng.Chance(AmbleChance * body.Plan.AmbleTicks))
        {
            int dir = (int)sim.Rng.NextUInt(8);
            int dx = dir switch { 0 or 6 or 7 => -1, 2 or 3 or 4 => 1, _ => 0 };
            int dy = dir switch { 0 or 1 or 2 => -1, 4 or 5 or 6 => 1, _ => 0 };
            if (Safe(grid, body.X + dx, body.Y + dy, body.Z)) new MoveBody(body.Id, dx, dy).Apply(sim);
        }
    }

    /// <summary>
    /// Whether something it noticed lately frightens it: a body not of its kind, a fire, a bolt,
    /// or a loud sound not made by its own kind. (fx, fy) is where the frights are, on average.
    /// </summary>
    public static bool Fright(Simulation sim, Body body, out double fx, out double fy)
    {
        fx = fy = 0.0;
        int n = 0;
        foreach (var p in body.Percepts)
        {
            if (sim.Tick - p.LastTick > FrightTicks) continue;
            bool scary = p.Kind switch
            {
                PerceptKind.Fire or PerceptKind.Bolt => true,
                PerceptKind.Body => Stranger(sim, body, p.Subject),
                PerceptKind.Sound => p.Strength >= StartleDb && ((SoundKind)p.Detail != SoundKind.Footstep || Stranger(sim, body, p.Subject)),
                _ => false,
            };
            if (!scary) continue;
            fx += p.X + 0.5;
            fy += p.Y + 0.5;
            n++;
        }
        if (n == 0) return false;
        fx /= n;
        fy /= n;
        return true;
    }

    /// <summary>Whether the body with this id is living and not of the same kind.</summary>
    private static bool Stranger(Simulation sim, Body body, long id)
    {
        foreach (var other in sim.Bodies)
            if (other.Id == id) return other.Alive && !other.IsRemains && other.Plan.Key != body.Plan.Key;
        return false;
    }

    /// <summary>Steps toward (dx, dy) from where it is, or the nearest safe way round.</summary>
    private static bool Step(Simulation sim, Body body, double dx, double dy)
    {
        if (dx * dx + dy * dy < 1e-9) dx = 1.0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        int bestX = 0, bestY = 0;
        double best = double.NegativeInfinity;
        for (int sx = -1; sx <= 1; sx++)
        for (int sy = -1; sy <= 1; sy++)
        {
            if (sx == 0 && sy == 0) continue;
            if (!Safe(sim.Grid, body.X + sx, body.Y + sy, body.Z)) continue;
            double score = (sx * dx + sy * dy) / (len * Math.Sqrt(sx * sx + sy * sy));
            if (score > best) { best = score; bestX = sx; bestY = sy; }
        }
        if (best == double.NegativeInfinity) return false;
        new MoveBody(body.Id, bestX, bestY).Apply(sim);
        return true;
    }

    private static bool Toward(Simulation sim, Body body, int target)
    {
        if (target < 0) return false;
        var (x, y, _) = sim.Grid.Coordinates(target);
        if (x == body.X && y == body.Y) return false;
        return Step(sim, body, x - body.X, y - body.Y);
    }

    /// <summary>Whether it would step into (x, y, z), or up onto the tile above it: not into water, fire or scalding air.</summary>
    public static bool Safe(WorldGrid grid, int x, int y, int z)
    {
        if (!grid.InBounds(x, y, z)) return false;
        int i = grid.Index(x, y, z);
        if (Solid(grid, i))
        {
            if (!grid.InBounds(x, y, z + 1)) return false;
            i = grid.Index(x, y, z + 1);
        }
        if (grid.Flame[i] != 0 || grid.GasTemperatureAt(i) > 323.15) return false;
        return !(grid.HasCondensed(i) && grid.Materials[grid.Material[i]].Phase == Phase.Liquid && grid.FillFraction(i) > 0.2);
    }

    private static bool Solid(WorldGrid grid, int i) =>
        grid.HasCondensed(i) && grid.Materials[grid.Material[i]].Phase == Phase.Solid && grid.FillFraction(i) >= 0.5;

    /// <summary>
    /// The nearest tile of water (or of living plant food) it could reach, or that is within
    /// <see cref="SearchTiles"/> if not <paramref name="reach"/>; -1 if none. Ties go to the first
    /// in the grid's order.
    /// </summary>
    private static int Nearest(WorldGrid grid, Body body, bool potable, bool reach)
    {
        int r = reach ? 1 : SearchTiles;
        int best = -1, bestD = int.MaxValue;
        for (int z = body.Z - 1; z <= body.Z + body.HeightTiles; z++)
        for (int y = body.Y - r; y <= body.Y + r; y++)
        for (int x = body.X - r; x <= body.X + r; x++)
        {
            if (!grid.InBounds(x, y, z)) continue;
            int i = grid.Index(x, y, z);
            if (!grid.HasCondensed(i)) continue;
            var def = grid.Materials[grid.Material[i]];
            bool good = potable ? def.Potable : def.Edible && def.IsPlant;
            if (!good) continue;
            int d = Math.Max(Math.Abs(x - body.X), Math.Abs(y - body.Y));
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
