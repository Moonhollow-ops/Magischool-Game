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
/// A tile holding both a solid or liquid and gas keeps a temperature for each
/// (see <see cref="WorldGrid.HasTwoTemperatures"/>). Each of its faces is
/// shared between them by how full the tile is, so a face splits into four
/// paths (solid to solid, solid to gas, gas to solid, gas to gas), and inside
/// the tile the two trade heat through the surface between them by
/// convection and radiation (<see cref="InteriorExchange"/>).
/// </para>
/// <para>
/// Not modelled yet: radiation between tiles (fire has its own, onto fuel).
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
    private bool[] _two = Array.Empty<bool>();
    private double[] _shareSide = Array.Empty<double>(), _shareDown = Array.Empty<double>(), _shareUp = Array.Empty<double>();
    private double[] _capC = Array.Empty<double>(), _capG = Array.Empty<double>();
    private double[] _tC = Array.Empty<double>(), _tG = Array.Empty<double>();
    private double[] _deltaC = Array.Empty<double>(), _deltaG = Array.Empty<double>();

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

    /// <summary>Emissivity of solid and liquid surfaces inside a tile.</summary>
    public const double SurfaceEmissivity = 0.9;

    private const double StefanBoltzmann = 5.670374419e-8;

    public void Update(Simulation sim)
    {
        Prepare(sim);

        var grid = sim.Grid;
        var activity = sim.Activity;
        var chunks = activity.AwakeChunks();

        // Pass 0: the start-of-tick state of each awake tile's one or two parts.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                double cc = grid.CondensedHeatCapacity(i), cg = grid.GasHeatCapacity(i);
                if (grid.HasTwoTemperatures(i))
                {
                    _two[i] = true;
                    SetShares(grid, i);
                    _capC[i] = cc;
                    _capG[i] = cg;
                    _tC[i] = grid.Temperature[i];
                    _tG[i] = grid.GasTemperature[i];
                }
                else
                {
                    // One temperature: all of the tile is whichever part it has.
                    _two[i] = false;
                    double all = grid.HasCondensed(i) ? 1.0 : 0.0;
                    _shareSide[i] = _shareDown[i] = _shareUp[i] = all;
                    _capC[i] = _capG[i] = cc + cg;
                    _tC[i] = _tG[i] = grid.Temperature[i];
                }
            }
        });

        // Pass 1: each tile sums the flow across its own six faces from the start-of-tick state.
        // A face is computed once from each side; the two results are exact negatives (every
        // operation is symmetric), so energy is conserved and tiles never write each other.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            int width = grid.Width, plane = grid.Width * grid.Height;
            ushort[] material = grid.Material;
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                double toC = 0.0, toG = 0.0;
                if (x > 0 && Open(activity, x - 1, y, z, x0, x1, y0, y1, z0, z1)) Face(i, i - 1, 0, material, ref toC, ref toG);
                if (x + 1 < grid.Width && Open(activity, x + 1, y, z, x0, x1, y0, y1, z0, z1)) Face(i, i + 1, 0, material, ref toC, ref toG);
                if (y > 0 && Open(activity, x, y - 1, z, x0, x1, y0, y1, z0, z1)) Face(i, i - width, 0, material, ref toC, ref toG);
                if (y + 1 < grid.Height && Open(activity, x, y + 1, z, x0, x1, y0, y1, z0, z1)) Face(i, i + width, 0, material, ref toC, ref toG);
                if (z > 0 && Open(activity, x, y, z - 1, x0, x1, y0, y1, z0, z1)) Face(i, i - plane, -1, material, ref toC, ref toG);
                if (z + 1 < grid.Depth && Open(activity, x, y, z + 1, x0, x1, y0, y1, z0, z1)) Face(i, i + plane, 1, material, ref toC, ref toG);
                _deltaC[i] = toC;
                _deltaG[i] = toG;
            }
        });

        // Pass 2: apply, then let each two-temperature tile's parts trade heat inside it.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                if (!_two[i])
                {
                    double delta = _deltaC[i] + _deltaG[i];
                    if (delta != 0.0 && _capC[i] > 0.0) grid.SetTemperature(i, grid.Temperature[i] + delta / _capC[i]);
                    continue;
                }
                double tc = grid.Temperature[i] + _deltaC[i] / _capC[i];
                double tg = grid.GasTemperature[i] + _deltaG[i] / _capG[i];
                double moved = InteriorExchange(grid, i, tc, tg, _capC[i], _capG[i]);
                grid.Temperature[i] = tc + moved / _capC[i];
                grid.GasTemperature[i] = tg - moved / _capG[i];
            }
        });
    }

    /// <summary>
    /// Joules passing this tick from a tile's gas into its solid or liquid, through the surface
    /// between them: convection plus radiation, solved exactly for two bodies relaxing toward
    /// each other, so it can never overshoot. A solid's surface is taken as that of a cube of
    /// its volume; a liquid's as the tile's floor, where it lies.
    /// </summary>
    public static double InteriorExchange(WorldGrid grid, int i, double condensedK, double gasK, double condensedCapacity, double gasCapacity)
    {
        double dT = gasK - condensedK;
        if (dT == 0.0) return 0.0;
        var def = grid.Materials[grid.Material[i]];
        double fill = Math.Min(1.0, grid.FillFraction(i));
        double area = def.Phase == Phase.Solid
            ? 6.0 * FaceAreaM2 * Math.Pow(fill, 2.0 / 3.0)
            : FaceAreaM2;
        double radiation = SurfaceEmissivity * StefanBoltzmann * (gasK * gasK + condensedK * condensedK) * (gasK + condensedK);
        double conductance = (GasFilmCoefficient + radiation) * area;
        double pair = condensedCapacity * gasCapacity / (condensedCapacity + gasCapacity);
        double decay = 1.0 - Math.Exp(-conductance * Simulation.SecondsPerTick / pair);
        return dT * pair * decay;
    }

    /// <summary>
    /// A face carries heat only when both sides are awake. Faces to a sleeping chunk are
    /// within <see cref="ActivitySystem.SleepThresholdK"/> of equilibrium by construction, and
    /// treating them as closed from both sides keeps energy exactly conserved.
    /// </summary>
    private static bool Open(ActivityMap activity, int nx, int ny, int nz, int x0, int x1, int y0, int y1, int z0, int z1) =>
        (nx >= x0 && nx < x1 && ny >= y0 && ny < y1 && nz >= z0 && nz < z1) || activity.IsAwakeAt(nx, ny, nz);

    /// <summary>
    /// Heat reaching tile a's solid or liquid and its gas from tile b this tick, over the face
    /// between them. Each part covers its share of the face on each side, giving four paths.
    /// </summary>
    private void Face(int a, int b, int up, ushort[] material, ref double toC, ref double toG)
    {
        double sa = Share(a, up), sb = Share(b, -up);
        ushort ma = material[a], mb = material[b];
        const ushort gas = MaterialTable.Empty;
        if (sa > 0.0)
        {
            if (sb > 0.0) toC -= Path(_tC[a], _tC[b], _capC[a], _capC[b], sa * sb, _halfResistance[ma] + _halfResistance[mb]);
            if (sb < 1.0) toC -= Path(_tC[a], _tG[b], _capC[a], _capG[b], sa * (1.0 - sb), _halfResistance[ma] + _halfResistance[gas]);
        }
        if (sa < 1.0)
        {
            if (sb > 0.0) toG -= Path(_tG[a], _tC[b], _capG[a], _capC[b], (1.0 - sa) * sb, _halfResistance[gas] + _halfResistance[mb]);
            if (sb < 1.0) toG -= Path(_tG[a], _tG[b], _capG[a], _capG[b], (1.0 - sa) * (1.0 - sb), _halfConduction[gas] + _halfConduction[gas]);
        }
    }

    /// <summary>Share of tile i's face toward <paramref name="up"/> (+1 above, -1 below, 0 a side) that its solid or liquid covers.</summary>
    private double Share(int i, int up) => up > 0 ? _shareUp[i] : up < 0 ? _shareDown[i] : _shareSide[i];

    /// <summary>
    /// How much of each face a tile's solid or liquid covers. A solid is spread through the tile,
    /// so it covers its fill on every face. A liquid pools: it covers the whole floor, the sides
    /// up to its depth, and the ceiling only once it is (nearly) full.
    /// </summary>
    private void SetShares(WorldGrid grid, int i)
    {
        double fill = Math.Min(1.0, grid.FillFraction(i));
        _shareSide[i] = fill;
        if (grid.Materials[grid.Material[i]].Phase == Phase.Liquid)
        {
            _shareDown[i] = 1.0;
            _shareUp[i] = Math.Clamp((fill - 0.95) / 0.05, 0.0, 1.0);
        }
        else
        {
            _shareDown[i] = _shareUp[i] = fill;
        }
    }

    /// <summary>
    /// Joules flowing from a part at <paramref name="ta"/> to one at <paramref name="tb"/> over
    /// <paramref name="share"/> of a face. Symmetric: swapping the two sides negates it exactly.
    /// </summary>
    private static double Path(double ta, double tb, double ca, double cb, double share, double resistance)
    {
        double dT = ta - tb;
        if (dT == 0.0 || ca <= 0.0 || cb <= 0.0) return 0.0;
        double joules = share / resistance * dT * Simulation.SecondsPerTick;
        // Never move more than this path's share of what would bring the pair to equilibrium.
        double limit = share * dT * (ca * cb / (ca + cb)) / FacesPerTile;
        return Math.Abs(joules) > Math.Abs(limit) ? limit : joules;
    }

    private void Prepare(Simulation sim)
    {
        int count = sim.Grid.Count;
        if (_deltaC.Length != count)
        {
            _two = new bool[count];
            _shareSide = new double[count]; _shareDown = new double[count]; _shareUp = new double[count];
            _capC = new double[count]; _capG = new double[count];
            _tC = new double[count]; _tG = new double[count];
            _deltaC = new double[count]; _deltaG = new double[count];
        }

        if (ReferenceEquals(_preparedFor, sim.Materials)) return;
        var table = sim.Materials;
        _halfResistance = new double[table.Count];
        _halfConduction = new double[table.Count];
        for (ushort id = 0; id < table.Count; id++)
        {
            var def = table[id];
            _halfResistance[id] = HalfResistance(def);
            _halfConduction[id] = def.ThermalConductivity > 0f ? HalfTileM / (def.ThermalConductivity * FaceAreaM2) : double.PositiveInfinity;
        }
        _preparedFor = table;
    }
}
