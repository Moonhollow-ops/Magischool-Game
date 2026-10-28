using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Ether;

/// <summary>
/// Layer 7: the ether field.
///
/// <para>
/// Ether is energy that pervades space, the energy a caster draws on and turns
/// into heat, force or matter. Each tile holds some, in joules
/// (<see cref="WorldGrid.Ether"/>), counted in the energy audit like heat. How
/// much a tile holds at a given potential depends on what fills it: open space
/// counts as itself, and matter as its volume times its
/// <see cref="MaterialDef.EtherPermittivity"/> (an aether crystal holds forty
/// times as much, iron half). Ether flows from high potential to low across
/// every face, as heat flows from hot to cold, at a rate set by the
/// <see cref="MaterialDef.EtherConductivity"/> on each side, so it runs through
/// stone and water and all but stops at cold iron and lead.
/// </para>
/// <para>
/// Every place has a potential it settles to (<see cref="WorldGrid.EtherAmbient"/>),
/// set by its surroundings, so some places are rich in magic and others poor.
/// A deep reservoir under the world refills or drains each tile toward it, at
/// that tile's <see cref="WorldGrid.EtherRefillRate"/>: over minutes in ordinary
/// ground, within seconds at a ley node. What crosses into and out of the
/// reservoir is counted (<see cref="ReservoirInJ"/>), so the world plus the
/// reservoir never gains or loses a joule. Drawing ether slower than a place
/// refills leaves it effectively untouched; drawing faster, as a great ritual
/// does, drains it until it recovers.
/// </para>
/// <para>
/// A solid pushed past its <see cref="MaterialDef.EtherBreakdown"/> potential
/// ruptures: the charge it holds discharges into it as heat, and it shatters,
/// its bonds to every neighbour broken, to fall as rubble. While the field
/// holds it past breakdown it keeps discharging, heating until it melts.
/// </para>
/// <para>
/// Etherium, ether crystallised into matter, ties the tile it lies in to the plane:
/// the tile settles to the etherium's well potential, and the plane refills it
/// faster the more etherium there is, so a shard enriches the field around it
/// without wearing away (see <see cref="AmbientAt"/>).
/// </para>
/// <para>
/// Not modelled yet: ether carried along with moving matter, breakdown through
/// open air (arcs).
/// </para>
/// </summary>
public sealed class EtherSystem : ISimSystem
{
    /// <summary>How freely ether moves through open space, m²/s.</summary>
    public const double FreeSpaceConductivity = 0.05;

    /// <summary>
    /// A tile within this of the potential it is heading for, J/m³, is settled. Places hold
    /// around a million, so this is a part in a million.
    /// </summary>
    public const double QuietJm3 = 1.0;

    private const double FacesPerTile = 6.0;
    private const double HalfTileM = WorldGrid.TileSizeM / 2;
    private const double FaceAreaM2 = WorldGrid.TileFaceAreaM2;

    public string Name => "ether";

    /// <summary>Net ether that has come up from the reservoir since this system was created, J (negative: gone down).</summary>
    public double ReservoirInJ { get; private set; }

    /// <summary>
    /// How many whole solids have ruptured, and all the charge discharged into heat, J (rubble
    /// still held past breakdown goes on discharging).
    /// </summary>
    public int Ruptures { get; private set; }
    public double DischargedJ { get; private set; }

    private static readonly double DefaultRefillShare =
        1.0 - Math.Exp(-Simulation.SecondsPerTick * WorldGrid.DefaultEtherRefillPerSecond);
    private double[] _capacity = Array.Empty<double>();
    private double[] _halfResistance = Array.Empty<double>();
    private double[] _potential = Array.Empty<double>();
    private double[] _delta = Array.Empty<double>();
    private double[] _reservoirPerChunk = Array.Empty<double>();

    /// <summary>
    /// Gives every tile exactly the ether its ambient potential calls for, so a freshly built
    /// world starts settled (see <see cref="Magischool.Sim.Fluids.Atmosphere.Balance"/> for the air).
    /// </summary>
    public static void Balance(WorldGrid grid)
    {
        for (int i = 0; i < grid.Count; i++)
            grid.Ether[i] = grid.EtherCapacity(i) * AmbientAt(grid, i);
    }

    /// <summary>How fast the etheric plane refills a tile tied to it by etherium, per second, when the tile is full of it.</summary>
    public const double WellRefillPerSecond = 1.0;

    /// <summary>
    /// The potential a tile settles to, J/m³: its place's ambient potential, or the well potential
    /// of any etherium in it if that is higher.
    /// </summary>
    public static double AmbientAt(WorldGrid grid, int i)
    {
        double ambient = grid.EtherAmbient[i];
        if (!grid.HasCondensed(i)) return ambient;
        return Math.Max(ambient, grid.Materials[grid.Material[i]].EtherWellPotential);
    }

    /// <summary>
    /// How fast the reservoir pulls a tile toward <see cref="AmbientAt"/>, per second: etherium adds
    /// to it in proportion to how full of it the tile is, so a big crystal feeds the field around it
    /// harder than a shard.
    /// </summary>
    public static double RefillRateAt(WorldGrid grid, int i)
    {
        double rate = grid.EtherRefillRate[i];
        if (!grid.HasCondensed(i) || grid.Materials[grid.Material[i]].EtherWellPotential <= 0f) return rate;
        return rate + Math.Min(1.0, grid.FillFraction(i)) * WellRefillPerSecond;
    }

    /// <summary>Resistance of half a tile to ether flowing across it, s/m³.</summary>
    public static double HalfResistance(WorldGrid grid, int i)
    {
        double fill = Math.Min(1.0, grid.CondensedVolume(i) / WorldGrid.TileVolumeM3);
        double solid = grid.HasCondensed(i) ? grid.Materials[grid.Material[i]].EtherConductivity : 0.0;
        double conductivity = fill * solid + (1.0 - fill) * FreeSpaceConductivity;
        return conductivity > 0.0 ? HalfTileM / (conductivity * FaceAreaM2) : double.PositiveInfinity;
    }

    public void Update(Simulation sim)
    {
        var grid = sim.Grid;
        var activity = sim.Activity;
        Prepare(grid, activity);
        var chunks = activity.AwakeChunks();

        // Pass 0: each awake tile's capacity, resistance and potential at the start of the tick.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                double capacity = grid.EtherCapacity(i);
                _capacity[i] = capacity;
                _halfResistance[i] = HalfResistance(grid, i);
                _potential[i] = grid.Ether[i] / capacity;
            }
        });

        // Pass 1: each tile sums the flow across its faces, computed identically from both
        // sides, and checks whether it (or a sleeping neighbour) is still on its way somewhere.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            bool restless = false;
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                double flow = 0.0, pull = 0.0, conductance = 0.0;
                for (int f = 0; f < 6; f++)
                {
                    var (dx, dy, dz) = Faces[f];
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (!grid.InBounds(nx, ny, nz)) continue;
                    int n = grid.Index(nx, ny, nz);
                    bool inside = nx >= x0 && nx < x1 && ny >= y0 && ny < y1 && nz >= z0 && nz < z1;
                    if (inside || activity.IsAwakeAt(nx, ny, nz))
                    {
                        double g = 1.0 / (_halfResistance[i] + _halfResistance[n]);
                        flow += Flow(_potential[n], _potential[i], _capacity[n], _capacity[i], g);
                        pull += g * (_potential[n] - _potential[i]);
                        conductance += g;
                    }
                    else
                    {
                        // A face to a sleeping chunk carries nothing, but if what is on this side
                        // would move that tile, its chunk must wake.
                        double g = 1.0 / (_halfResistance[i] + HalfResistance(grid, n));
                        double potential = grid.EtherPotential(n);
                        pull += g * (potential - _potential[i]);
                        conductance += g;
                        if (Math.Abs(Imbalance(grid, nx, ny, nz)) > QuietJm3)
                            activity.Stirred[activity.ChunkAt(nx, ny, nz)] = true;
                    }
                }
                double refill = _capacity[i] * RefillRateAt(grid, i);
                pull += refill * (AmbientAt(grid, i) - _potential[i]);
                conductance += refill;
                if (Math.Abs(pull / conductance) > QuietJm3) restless = true;
                _delta[i] = flow;
            }
            if (restless) activity.Stirred[chunk] = true;
        });

        // Pass 2: apply the flows, then let the reservoir pull each tile toward its ambient potential.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            double fromReservoir = 0.0;
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                double charge = grid.Ether[i] + _delta[i];
                // Relaxed exactly over the tick, so even a fast ley node never overshoots.
                double rate = RefillRateAt(grid, i);
                double share = rate == WorldGrid.DefaultEtherRefillPerSecond
                    ? DefaultRefillShare
                    : 1.0 - Math.Exp(-Simulation.SecondsPerTick * rate);
                double refill = (_capacity[i] * AmbientAt(grid, i) - charge) * share;
                grid.Ether[i] = charge + refill;
                fromReservoir += refill;
            }
            _reservoirPerChunk[chunk] = fromReservoir;
        });
        foreach (int chunk in chunks) ReservoirInJ += _reservoirPerChunk[chunk];

        // Breakdown, in a fixed order: a rupture breaks bonds that its neighbours share.
        foreach (int chunk in chunks)
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                if (!grid.HasCondensed(i)) continue;
                var def = grid.Materials[grid.Material[i]];
                if (def.Phase != Phase.Solid || float.IsPositiveInfinity(def.EtherBreakdown)) continue;
                if (grid.Ether[i] < def.EtherBreakdown * grid.EtherCapacity(i)) continue;
                Rupture(sim, i, x, y, z);
            }
        }
    }

    /// <summary>
    /// The charge the solid itself holds discharges into it as heat, and it shatters. The open
    /// space around it in the tile keeps its share: air does not break down.
    /// </summary>
    private void Rupture(Simulation sim, int i, int x, int y, int z)
    {
        var grid = sim.Grid;
        double solid = Math.Min(grid.CondensedVolume(i), WorldGrid.TileVolumeM3) * grid.Materials[grid.Material[i]].EtherPermittivity;
        double charge = grid.Ether[i] * solid / grid.EtherCapacity(i);
        grid.Ether[i] -= charge;
        EnergyAudit.AddJoules(grid, i, charge, 0.0);
        if ((grid.Structure[i] & WorldGrid.LooseBit) == 0) Ruptures++;
        grid.BreakBonds(i);
        sim.Activity.WakeAround(x, y, z);
        DischargedJ += charge;
    }

    /// <summary>
    /// Ether moving this tick into the tile at <paramref name="to"/> from the one at
    /// <paramref name="from"/> potential across a face of conductance <paramref name="g"/>.
    /// Swapping the sides negates it exactly, and it never moves more than this face's share
    /// of what would bring the two to one potential, so it cannot overshoot.
    /// </summary>
    private static double Flow(double from, double to, double fromCapacity, double toCapacity, double g)
    {
        double dPhi = from - to;
        if (dPhi == 0.0 || g == 0.0) return 0.0;
        double joules = g * dPhi * Simulation.SecondsPerTick;
        double limit = dPhi * (fromCapacity * toCapacity / (fromCapacity + toCapacity)) / FacesPerTile;
        return Math.Abs(joules) > Math.Abs(limit) ? limit : joules;
    }

    /// <summary>
    /// How far a tile's potential is from where its faces and the reservoir are pulling it,
    /// J/m³, computed straight from the grid (for tiles in sleeping chunks).
    /// </summary>
    private static double Imbalance(WorldGrid grid, int x, int y, int z)
    {
        int i = grid.Index(x, y, z);
        double capacity = grid.EtherCapacity(i);
        double potential = grid.Ether[i] / capacity;
        double half = HalfResistance(grid, i);
        double pull = 0.0, conductance = 0.0;
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = Faces[f];
            int nx = x + dx, ny = y + dy, nz = z + dz;
            if (!grid.InBounds(nx, ny, nz)) continue;
            int n = grid.Index(nx, ny, nz);
            double g = 1.0 / (half + HalfResistance(grid, n));
            pull += g * (grid.EtherPotential(n) - potential);
            conductance += g;
        }
        double refill = capacity * RefillRateAt(grid, i);
        pull += refill * (AmbientAt(grid, i) - potential);
        conductance += refill;
        return pull / conductance;
    }

    private static readonly (int, int, int)[] Faces =
    {
        (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1),
    };

    private void Prepare(WorldGrid grid, ActivityMap activity)
    {
        if (_capacity.Length != grid.Count)
        {
            _capacity = new double[grid.Count];
            _halfResistance = new double[grid.Count];
            _potential = new double[grid.Count];
            _delta = new double[grid.Count];
        }
        if (_reservoirPerChunk.Length != activity.Count) _reservoirPerChunk = new double[activity.Count];
    }
}
