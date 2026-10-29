using System;
using System.Collections.Generic;
using Magischool.Sim.Core;

namespace Magischool.Sim.Bodies;

/// <summary>
/// Finds a walk for a body: the steps <see cref="MoveBody"/> would take it, by the same rules the
/// bodies layer walks by. A step goes to any of the eight tiles around, on the level or up one
/// tile if there is headroom, and a body may step off an edge one tile high but no further. The
/// world can change under the walk, so whoever follows it checks each step landed.
/// </summary>
public static class Pathfinder
{
    /// <summary>Most tiles one search looks at before giving up.</summary>
    public const int MaxSearched = 20_000;

    private static readonly (int Dx, int Dy)[] Steps =
        { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    /// <summary>
    /// The feet positions, one per step, of a shortest walk from where the body stands to column
    /// (<paramref name="tx"/>, <paramref name="ty"/>) at any height, or null when there is none.
    /// Empty when it is already there.
    /// </summary>
    public static List<(int X, int Y, int Z)>? Find(Simulation sim, Body body, int tx, int ty)
    {
        var grid = sim.Grid;
        int h = body.HeightTiles;
        var start = (body.X, body.Y, body.Z);
        if (body.X == tx && body.Y == ty) return new List<(int, int, int)>();
        var from = new Dictionary<(int, int, int), (int, int, int)> { [start] = start };
        var queue = new Queue<(int X, int Y, int Z)>();
        queue.Enqueue(start);
        while (queue.Count > 0 && from.Count < MaxSearched)
        {
            var (x, y, z) = queue.Dequeue();
            if (BodySystem.Blocked(grid, x, y, z) || !BodySystem.Supported(grid, x, y, z)) continue;
            foreach (var (dx, dy) in Steps)
            {
                if (Land(grid, x, y, z, x + dx, y + dy, h) is not { } next || from.ContainsKey(next)) continue;
                from[next] = (x, y, z);
                if (next.X == tx && next.Y == ty) return Trace(from, start, next);
                queue.Enqueue(next);
            }
        }
        return null;
    }

    /// <summary>Where a step from (x, y, z) toward (nx, ny) leaves the body's feet, or null if it can't go or would fall too far.</summary>
    private static (int X, int Y, int Z)? Land(WorldGrid grid, int x, int y, int z, int nx, int ny, int h)
    {
        int nz;
        if (BodySystem.Clear(grid, nx, ny, z, h)) nz = z;
        else if (BodySystem.Clear(grid, nx, ny, z + 1, h) && !BodySystem.Blocked(grid, x, y, z + h)) nz = z + 1;
        else return null;
        if (BodySystem.Supported(grid, nx, ny, nz)) return (nx, ny, nz);
        // One tile down is a step off; more is a fall.
        if (nz > 0 && BodySystem.Supported(grid, nx, ny, nz - 1) && BodySystem.Clear(grid, nx, ny, nz - 1, h)) return (nx, ny, nz - 1);
        return null;
    }

    private static List<(int X, int Y, int Z)> Trace(Dictionary<(int, int, int), (int, int, int)> from, (int, int, int) start, (int, int, int) end)
    {
        var path = new List<(int X, int Y, int Z)>();
        for (var at = end; at != start; at = from[at]) path.Add(at);
        path.Reverse();
        return path;
    }
}
