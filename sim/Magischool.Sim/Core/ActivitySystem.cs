using Magischool.Sim.Thermal;

namespace Magischool.Sim.Core;

/// <summary>
/// Runs last each tick and decides which chunks sleep next tick.
///
/// An awake chunk falls asleep when, across every face inside it and on its
/// border, temperatures differ by no more than <see cref="SleepThresholdK"/>,
/// no tile in it is part-way through or due for a phase change, and nothing
/// moved in it this tick (see <see cref="ActivityMap.Stirred"/>).
/// A sleeping chunk wakes when an awake neighbour differs from it by more than
/// the threshold across their shared border, when fluid wants to cross into
/// it, or when a command touches it.
///
/// Sleeping never creates or destroys energy (faces to a sleeping chunk carry
/// nothing from either side). The fidelity cost is that differences smaller
/// than the threshold stop evening out until something wakes the area.
/// </summary>
public sealed class ActivitySystem : ISimSystem
{
    /// <summary>One millikelvin: far below anything a body, a material or a player could notice.</summary>
    public const double SleepThresholdK = 1e-3;

    private bool[] _quiet = System.Array.Empty<bool>();
    private bool[] _wake = System.Array.Empty<bool>();

    public string Name => "activity";

    public void Update(Simulation sim)
    {
        var grid = sim.Grid;
        var activity = sim.Activity;
        var rules = PhaseRules.For(sim.Materials);
        if (_quiet.Length != activity.Count)
        {
            _quiet = new bool[activity.Count];
            _wake = new bool[activity.Count];
        }

        var chunks = activity.AwakeChunks();
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            _quiet[chunk] = !activity.Stirred[chunk] && IsQuiet(grid, rules, activity, chunk);
        });

        // Sleeping neighbours that an awake, changing chunk now pushes on must wake.
        // Checked against the state before anyone falls asleep this tick.
        foreach (int chunk in chunks)
            if (!_quiet[chunk]) MarkPushedNeighbours(grid, activity, chunk);

        foreach (int chunk in chunks)
            if (_quiet[chunk]) activity.Awake[chunk] = false;
        for (int c = 0; c < activity.Count; c++)
        {
            if (_wake[c] || activity.Stirred[c]) activity.Awake[c] = true;
            _wake[c] = false;
            activity.Stirred[c] = false;
            _quiet[c] = false;
        }
    }

    private static bool IsQuiet(WorldGrid grid, PhaseRules rules, ActivityMap activity, int chunk)
    {
        var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
        double[] t = grid.Temperature;
        for (int z = z0; z < z1; z++)
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int i = grid.Index(x, y, z);
            if (!rules.IsSettled(grid, i)) return false;
            // Each face is checked from its lower side, plus the chunk's lower borders.
            if (x + 1 < grid.Width && System.Math.Abs(t[i] - t[i + 1]) > SleepThresholdK) return false;
            if (y + 1 < grid.Height && System.Math.Abs(t[i] - t[i + grid.Width]) > SleepThresholdK) return false;
            if (z + 1 < grid.Depth && System.Math.Abs(t[i] - t[i + grid.Width * grid.Height]) > SleepThresholdK) return false;
            if (x == x0 && x > 0 && System.Math.Abs(t[i] - t[i - 1]) > SleepThresholdK) return false;
            if (y == y0 && y > 0 && System.Math.Abs(t[i] - t[i - grid.Width]) > SleepThresholdK) return false;
            if (z == z0 && z > 0 && System.Math.Abs(t[i] - t[i - grid.Width * grid.Height]) > SleepThresholdK) return false;
        }
        return true;
    }

    private void MarkPushedNeighbours(WorldGrid grid, ActivityMap activity, int chunk)
    {
        var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
        for (int z = z0; z < z1; z++)
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            bool edge = x == x0 || x == x1 - 1 || y == y0 || y == y1 - 1 || z == z0 || z == z1 - 1;
            if (!edge) continue;
            CheckNeighbour(grid, activity, x, y, z, x - 1, y, z);
            CheckNeighbour(grid, activity, x, y, z, x + 1, y, z);
            CheckNeighbour(grid, activity, x, y, z, x, y - 1, z);
            CheckNeighbour(grid, activity, x, y, z, x, y + 1, z);
            CheckNeighbour(grid, activity, x, y, z, x, y, z - 1);
            CheckNeighbour(grid, activity, x, y, z, x, y, z + 1);
        }
    }

    private void CheckNeighbour(WorldGrid grid, ActivityMap activity, int x, int y, int z, int nx, int ny, int nz)
    {
        if (!grid.InBounds(nx, ny, nz)) return;
        int neighbour = activity.ChunkAt(nx, ny, nz);
        if (activity.Awake[neighbour] || _wake[neighbour]) return;
        if (System.Math.Abs(grid.Temperature[grid.Index(x, y, z)] - grid.Temperature[grid.Index(nx, ny, nz)]) > SleepThresholdK)
            _wake[neighbour] = true;
    }
}
