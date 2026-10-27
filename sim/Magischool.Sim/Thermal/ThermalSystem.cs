using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Thermal;

/// <summary>
/// Layer 2: heat moving between neighbouring tiles.
///
/// Every shared face between two tiles is a thermal conductance G (W/K), built
/// from two half-tile resistances in series. Heat flows from hot to cold at
/// G × ΔT watts. Each face's transfer is subtracted from one tile and added to
/// the other, so energy is conserved to double precision. Only awake chunks
/// (see <see cref="ActivityMap"/>) are simulated, in parallel.
///
/// <para>Solids conduct: a half tile's resistance is (half a tile) / (k × face area).</para>
/// <para>
/// Fluids also convect, which at tile scale carries far more heat than
/// conduction (still air conducts so poorly that a hot stone would take days
/// to warm the air beside it). The thin boundary layer against a surface is
/// far smaller than a tile, so a fluid's side of a face to a solid or a
/// different fluid uses whichever is lower: its conduction resistance or a
/// natural-convection film resistance 1 / h. Between two tiles of open air,
/// the air currents (layer 4b) do the carrying, so only conduction applies.
/// </para>
/// <para>
/// Not modelled yet: radiation (needs line of sight; planned with fire).
/// World edges are insulated.
/// </para>
/// </summary>
public sealed class ThermalSystem : ISimSystem
{
    /// <summary>Natural convection between still air and a surface, W/(m²·K).</summary>
    public const float GasFilmCoefficient = 10f;

    /// <summary>Natural convection between still water and a surface, W/(m²·K).</summary>
    public const float LiquidFilmCoefficient = 500f;

    /// <summary>Faces per tile. Each face may move at most 1/6 of the energy that would equalise it, so a tile can never overshoot.</summary>
    private const int FacesPerTile = 6;

    private const double FaceAreaM2 = WorldGrid.TileFaceAreaM2;
    private const double HalfTileM = WorldGrid.TileSizeM / 2;

    private MaterialTable? _preparedFor;
    private double[] _halfResistance = Array.Empty<double>();
    private double[] _halfConduction = Array.Empty<double>();
    private bool[] _isGas = Array.Empty<bool>();
    private double[] _capacity = Array.Empty<double>();
    private double[] _delta = Array.Empty<double>();

    public string Name => "thermal";

    /// <summary>Thermal conductance through the face between two materials, W/K.</summary>
    public static double FaceConductance(MaterialDef a, MaterialDef b) => 1.0 / (HalfResistance(a) + HalfResistance(b));

    /// <summary>Resistance of half a tile of this material, K/W.</summary>
    public static double HalfResistance(MaterialDef def)
    {
        double conduction = def.ThermalConductivity > 0f
            ? HalfTileM / (def.ThermalConductivity * FaceAreaM2)
            : double.PositiveInfinity;
        return def.Phase switch
        {
            Phase.Gas => Math.Min(conduction, 1.0 / (GasFilmCoefficient * FaceAreaM2)),
            Phase.Liquid => Math.Min(conduction, 1.0 / (LiquidFilmCoefficient * FaceAreaM2)),
            _ => conduction,
        };
    }

    public void Update(Simulation sim)
    {
        Prepare(sim);

        var grid = sim.Grid;
        var activity = sim.Activity;
        var chunks = activity.AwakeChunks();
        double[] temperature = grid.Temperature;
        ushort[] material = grid.Material;

        // Pass 0: heat capacity of every awake tile (condensed contents plus gas).
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                _capacity[i] = grid.HeatCapacity(i);
            }
        });

        // Pass 1: each tile sums the flow across its own six faces from the start-of-tick state.
        // A face is computed once from each side; the two results are exact negatives (every
        // operation is symmetric), so energy is conserved and tiles never write each other.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            int width = grid.Width, plane = grid.Width * grid.Height;
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                double net = 0.0;
                if (x > 0 && Open(activity, x - 1, y, z, x0, x1, y0, y1, z0, z1)) net -= Flow(i, i - 1, material, temperature);
                if (x + 1 < grid.Width && Open(activity, x + 1, y, z, x0, x1, y0, y1, z0, z1)) net -= Flow(i, i + 1, material, temperature);
                if (y > 0 && Open(activity, x, y - 1, z, x0, x1, y0, y1, z0, z1)) net -= Flow(i, i - width, material, temperature);
                if (y + 1 < grid.Height && Open(activity, x, y + 1, z, x0, x1, y0, y1, z0, z1)) net -= Flow(i, i + width, material, temperature);
                if (z > 0 && Open(activity, x, y, z - 1, x0, x1, y0, y1, z0, z1)) net -= Flow(i, i - plane, material, temperature);
                if (z + 1 < grid.Depth && Open(activity, x, y, z + 1, x0, x1, y0, y1, z0, z1)) net -= Flow(i, i + plane, material, temperature);
                _delta[i] = net;
            }
        });

        // Pass 2: apply.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                double delta = _delta[i];
                if (delta == 0.0) continue;
                double capacity = _capacity[i];
                if (capacity > 0.0) temperature[i] += delta / capacity;
            }
        });
    }

    /// <summary>
    /// A face carries heat only when both sides are awake. Faces to a sleeping chunk are
    /// within <see cref="ActivitySystem.SleepThresholdK"/> of equilibrium by construction, and
    /// treating them as closed from both sides keeps energy exactly conserved.
    /// </summary>
    private static bool Open(ActivityMap activity, int nx, int ny, int nz, int x0, int x1, int y0, int y1, int z0, int z1) =>
        (nx >= x0 && nx < x1 && ny >= y0 && ny < y1 && nz >= z0 && nz < z1) || activity.IsAwakeAt(nx, ny, nz);

    /// <summary>Joules flowing from tile a to tile b this tick. Flow(a, b) == -Flow(b, a) exactly.</summary>
    private double Flow(int a, int b, ushort[] material, double[] temperature)
    {
        double dT = temperature[a] - temperature[b];
        if (dT == 0.0) return 0.0;

        ushort ma = material[a], mb = material[b];
        double ca = _capacity[a], cb = _capacity[b];
        if (ca <= 0.0 || cb <= 0.0) return 0.0;

        double conductance = _isGas[ma] && _isGas[mb]
            ? 1.0 / (_halfConduction[ma] + _halfConduction[mb])
            : 1.0 / (_halfResistance[ma] + _halfResistance[mb]);
        double joules = conductance * dT * Simulation.SecondsPerTick;

        // Never move more than this face's share of what would bring the pair to equilibrium.
        double limit = dT * (ca * cb / (ca + cb)) / FacesPerTile;
        return Math.Abs(joules) > Math.Abs(limit) ? limit : joules;
    }

    private void Prepare(Simulation sim)
    {
        if (_delta.Length != sim.Grid.Count)
        {
            _delta = new double[sim.Grid.Count];
            _capacity = new double[sim.Grid.Count];
        }

        if (ReferenceEquals(_preparedFor, sim.Materials)) return;
        var table = sim.Materials;
        _halfResistance = new double[table.Count];
        _halfConduction = new double[table.Count];
        _isGas = new bool[table.Count];
        for (ushort id = 0; id < table.Count; id++)
        {
            var def = table[id];
            _halfResistance[id] = HalfResistance(def);
            _halfConduction[id] = def.ThermalConductivity > 0f ? HalfTileM / (def.ThermalConductivity * FaceAreaM2) : double.PositiveInfinity;
            _isGas[id] = def.Phase == Phase.Gas;
        }
        _preparedFor = table;
    }
}
