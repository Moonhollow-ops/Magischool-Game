using System;
using System.Collections.Generic;
using Magischool.Sim.Combustion;
using Magischool.Sim.Core;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Structure;

/// <summary>
/// Layer 6: structure and load.
///
/// <para>
/// Solid tiles that touch are bonded, and every bond is a short elastic beam
/// between the two tile centres: it stretches and squashes, bends and twists,
/// with a cross-section set by how full the thinner of the two tiles is (a
/// tile a twentieth full of wood is a post about 11 cm square). The bottom
/// layer of the world is bedrock and does not move. Every
/// <see cref="CheckIntervalTicks"/> ticks, each group of bonded tiles that has
/// changed recently is solved for static equilibrium under its own weight,
/// the weight of the rubble and liquid resting on it, and the push of any
/// water that froze where it had no room to expand. That gives the force and
/// bending moment in every bond, so every bond knows its stress: tension and
/// crushing along it, bending at its corners, and shear across it.
/// </para>
/// <para>
/// A bond breaks when its stress passes the strength of the weaker of its two
/// tiles. Strength falls with temperature (wood is char by its ignition
/// point), and burning thins a tile's cross-section, so a post in a fire
/// weakens as it burns. The most overloaded bond breaks first, the group is
/// solved again with the load shifted onto what remains, and so on, so a
/// failure can run through a structure. Any piece no longer held to bedrock
/// comes loose.
/// </para>
/// <para>
/// Loose tiles are rubble: they rest on whatever is below them and pass their
/// weight straight down. With nothing solid beneath, they fall at gravity's
/// rate (up to one tile per tick), trading places with the gas or lighter
/// liquid below. Every metre fallen turns its share of potential energy into
/// heat in the falling tile, so energy stays conserved.
/// </para>
/// <para>
/// Not modelled yet: stiffness falling with heat, impact forces, sideways
/// pressure from liquids on their containers, creep, and gravity's potential
/// energy for liquids and gases.
/// </para>
/// </summary>
public sealed class StructureSystem : ISimSystem
{
    /// <summary>Ticks between structural checks (one second).</summary>
    public const int CheckIntervalTicks = 10;

    /// <summary>A tile less full than this is too thin to bond.</summary>
    public const double MinSectionFill = 1e-3;

    /// <summary>Most bonds broken in one group per check; the rest of a collapse carries on next check.</summary>
    public const int MaxBreaksPerCheck = 64;

    public const double PoissonRatio = 0.3;

    /// <summary>Fastest a loose tile falls, m/s: one tile per tick.</summary>
    public static readonly double MaxFallSpeed = WorldGrid.TileSizeM / Simulation.SecondsPerTick;

    private const double BondLength = WorldGrid.TileSizeM;

    public string Name => "structure";

    /// <summary>Gravitational potential energy turned into heat by falling rubble since this system was created, J.</summary>
    public double ReleasedPotentialJ { get; private set; }

    /// <summary>Bonds broken since this system was created.</summary>
    public int BondsBroken { get; private set; }

    private MaterialTable? _preparedFor;
    private bool[] _structural = Array.Empty<bool>();
    private double[] _modulus = Array.Empty<double>();
    private double[] _tensile = Array.Empty<double>();
    private double[] _compressive = Array.Empty<double>();
    private double[] _lossStart = Array.Empty<double>();
    private double[] _lossEnd = Array.Empty<double>();
    private double[] _bulkModulus = Array.Empty<double>();
    // For a solid whose melt is denser (ice), how far its melting point drops per pascal, K/Pa.
    private double[] _meltDropPerPa = Array.Empty<double>();
    private double[] _meltingPoint = Array.Empty<double>();

    private int[] _visited = Array.Empty<int>();
    private int[] _local = Array.Empty<int>();
    private int _check, _flood;
    private float[] _utilisation = Array.Empty<float>();

    /// <summary>
    /// How close the most loaded bond of each tile came to breaking at the last check
    /// (1 = breaking), for display. Not part of the world's state.
    /// </summary>
    public float UtilisationAt(int index) => index < _utilisation.Length ? _utilisation[index] : 0f;

    public void Update(Simulation sim)
    {
        var grid = sim.Grid;
        Prepare(grid);
        Fall(sim);
        if (sim.Tick % CheckIntervalTicks == 0) Check(sim);
    }

    // ---------------------------------------------------------------- falling

    private readonly List<int> _loose = new();

    private void Fall(Simulation sim)
    {
        var grid = sim.Grid;
        var activity = sim.Activity;
        int plane = grid.Width * grid.Height;
        _loose.Clear();
        foreach (int chunk in activity.AwakeChunks())
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = Math.Max(z0, 1); z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                if ((grid.Structure[i] & WorldGrid.LooseBit) != 0 && IsSolid(grid, i)) _loose.Add(i);
            }
        }
        // Lowest first, so a stack falls together.
        _loose.Sort();

        double dt = Simulation.SecondsPerTick;
        foreach (int i in _loose)
        {
            if ((grid.Structure[i] & WorldGrid.LooseBit) == 0 || !IsSolid(grid, i)) continue;
            int j = i - plane;
            var (x, y, z) = grid.Coordinates(j);
            if (!CanFallInto(grid, i, j))
            {
                grid.FallSpeed[i] = 0.0;
                grid.FallTravel[i] = 0.0;
                continue;
            }
            int chunkBelow = activity.ChunkAt(x, y, z);
            activity.Stirred[activity.ChunkAt(x, y, z + 1)] = true;
            if (!activity.Awake[chunkBelow])
            {
                activity.Stirred[chunkBelow] = true;
                continue;
            }
            double speed = Math.Min(grid.FallSpeed[i] + FluidSystem.Gravity * dt, MaxFallSpeed);
            double travel = grid.FallTravel[i] + speed * dt;
            grid.FallSpeed[i] = speed;
            grid.FallTravel[i] = travel;
            if (travel < WorldGrid.TileSizeM - 1e-12) continue;
            grid.FallTravel[i] = Math.Max(0.0, travel - WorldGrid.TileSizeM);

            // The tiles trade places; what came up loses the potential energy the faller gave up.
            double released = (TileMass(grid, i) - TileMass(grid, j)) * FluidSystem.Gravity * WorldGrid.TileSizeM;
            Swap(grid, i, j);
            grid.ResetBonds(i);
            EnergyAudit.AddJoules(grid, j, released, 0.0);
            ReleasedPotentialJ += released;
        }
    }

    /// <summary>Whether the loose solid in tile i can drop into tile j below it.</summary>
    private bool CanFallInto(WorldGrid grid, int i, int j)
    {
        if (!grid.HasCondensed(j)) return true;
        var below = grid.Materials[grid.Material[j]];
        if (below.Phase == Phase.Solid) return false;
        // A liquid: the solid sinks through it only if it is the denser of the two.
        return grid.Materials[grid.Material[i]].Density > below.Density || grid.FillFraction(j) < 0.5;
    }

    private static double TileMass(WorldGrid grid, int i) => grid.Mass[i] + grid.TotalGasMass(i);

    private static void Swap(WorldGrid grid, int a, int b)
    {
        (grid.Material[a], grid.Material[b]) = (grid.Material[b], grid.Material[a]);
        (grid.Mass[a], grid.Mass[b]) = (grid.Mass[b], grid.Mass[a]);
        (grid.LatentEnergy[a], grid.LatentEnergy[b]) = (grid.LatentEnergy[b], grid.LatentEnergy[a]);
        (grid.Temperature[a], grid.Temperature[b]) = (grid.Temperature[b], grid.Temperature[a]);
        (grid.GasTemperature[a], grid.GasTemperature[b]) = (grid.GasTemperature[b], grid.GasTemperature[a]);
        foreach (var gas in grid.GasMass) (gas[a], gas[b]) = (gas[b], gas[a]);
        (grid.Flame[a], grid.Flame[b]) = (grid.Flame[b], grid.Flame[a]);
        for (int k = 0; k < 6; k++)
            (grid.Skin[a * 6 + k], grid.Skin[b * 6 + k]) = (grid.Skin[b * 6 + k], grid.Skin[a * 6 + k]);
        (grid.FallSpeed[a], grid.FallSpeed[b]) = (grid.FallSpeed[b], grid.FallSpeed[a]);
        (grid.FallTravel[a], grid.FallTravel[b]) = (grid.FallTravel[b], grid.FallTravel[a]);
        // Only the loose mark travels; bonds belong to places, and rubble has none.
        byte looseA = (byte)(grid.Structure[a] & WorldGrid.LooseBit), looseB = (byte)(grid.Structure[b] & WorldGrid.LooseBit);
        grid.Structure[a] = (byte)((grid.Structure[a] & ~WorldGrid.LooseBit) | looseB);
        grid.Structure[b] = (byte)((grid.Structure[b] & ~WorldGrid.LooseBit) | looseA);
    }

    // ---------------------------------------------------------------- checking

    private readonly List<int> _seeds = new();

    private void Check(Simulation sim)
    {
        var grid = sim.Grid;
        var activity = sim.Activity;
        _check++;
        _seeds.Clear();
        foreach (int chunk in activity.AwakeChunks())
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = Math.Max(z0, 1); z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                if (IsNode(grid, i)) _seeds.Add(i);
            }
        }
        _seeds.Sort();

        // A break can cut a piece off the group being solved; its two ends join the seeds so
        // the piece is solved (or let fall) in this same check.
        for (int s = 0; s < _seeds.Count; s++)
        {
            int seed = _seeds[s];
            if (_visited[seed] == _check || !IsNode(grid, seed)) continue;
            for (int round = 0; round <= MaxBreaksPerCheck; round++)
            {
                var group = Gather(grid, seed);
                if (!group.Anchored)
                {
                    foreach (int i in group.Nodes) MakeLoose(sim, i);
                    break;
                }
                Solve(grid, group);
                int worst = WorstBond(grid, group, out double utilisation);
                if (utilisation <= 1.0 || round == MaxBreaksPerCheck) break;
                Break(sim, group.Bonds[worst]);
            }
        }
    }

    private void MakeLoose(Simulation sim, int i)
    {
        var grid = sim.Grid;
        grid.Structure[i] = (byte)(grid.Structure[i] | WorldGrid.LooseBit);
        var (x, y, z) = grid.Coordinates(i);
        sim.Activity.Stirred[sim.Activity.ChunkAt(x, y, z)] = true;
        _visited[i] = _check;
        _utilisation[i] = 0f;
    }

    private void Break(Simulation sim, Bond bond)
    {
        var grid = sim.Grid;
        grid.Structure[bond.A] = (byte)(grid.Structure[bond.A] | (1 << bond.Axis));
        BondsBroken++;
        _seeds.Add(bond.A);
        _seeds.Add(bond.B);
        _visited[bond.A] = 0;
        _visited[bond.B] = 0;
        foreach (int end in new[] { bond.A, bond.B })
        {
            var (x, y, z) = grid.Coordinates(end);
            sim.Activity.WakeAround(x, y, z);
            sim.Activity.Stirred[sim.Activity.ChunkAt(x, y, z)] = true;
            // Ice that was pushing on this bond has cracked its way free.
            foreach (int n in FaceNeighbours(grid, end))
                if (IsPressurised(grid, n))
                    grid.Structure[n] = (byte)(grid.Structure[n] | WorldGrid.LooseBit);
        }
    }

    // ---------------------------------------------------------------- the lattice

    private readonly struct Bond
    {
        public readonly int A, B;        // tile indices, B = A + step along Axis
        public readonly int NodeA, NodeB; // positions in the group, -1 for bedrock
        public readonly int Axis;
        public readonly double AxialStiffness, TorsionStiffness, BendingEI;
        public readonly double Area, Side;

        public Bond(int a, int b, int nodeA, int nodeB, int axis, double modulus, double area)
        {
            A = a; B = b; NodeA = nodeA; NodeB = nodeB; Axis = axis;
            Area = area;
            Side = Math.Sqrt(area);
            double i = area * area / 12.0;
            double j = 0.1406 * area * area;
            AxialStiffness = modulus * area / BondLength;
            TorsionStiffness = modulus / (2.0 * (1.0 + PoissonRatio)) * j / BondLength;
            BendingEI = modulus * i;
        }
    }

    private sealed class Group
    {
        public readonly List<int> Nodes = new();
        public readonly List<Bond> Bonds = new();
        public bool Anchored;
        public double[] Load = Array.Empty<double>();
        public double[] Displacement = Array.Empty<double>();
    }

    private readonly Group _group = new();

    /// <summary>Every tile bonded to <paramref name="seed"/> short of bedrock, the bonds among them, and the loads on them.</summary>
    private Group Gather(WorldGrid grid, int seed)
    {
        var group = _group;
        group.Nodes.Clear();
        group.Bonds.Clear();
        group.Anchored = false;
        _flood++;

        // Flood out through intact bonds; bedrock ends the search.
        _local[seed] = -_flood;
        group.Nodes.Add(seed);
        for (int head = 0; head < group.Nodes.Count; head++)
        {
            int i = group.Nodes[head];
            foreach (var (n, axis, lower) in Bonded(grid, i))
            {
                if (IsBedrock(grid, n)) { group.Anchored = true; continue; }
                if (_local[n] == -_flood) continue;
                _local[n] = -_flood;
                group.Nodes.Add(n);
            }
        }
        // Nodes in index order, so the result never depends on where the search began.
        group.Nodes.Sort();
        for (int k = 0; k < group.Nodes.Count; k++)
        {
            int i = group.Nodes[k];
            _local[i] = k;
            _visited[i] = _check;
        }

        int count = group.Nodes.Count;
        if (group.Load.Length < count * 6)
        {
            group.Load = new double[count * 6 * 2];
            group.Displacement = new double[count * 6 * 2];
        }
        Array.Clear(group.Load, 0, count * 6);

        for (int k = 0; k < count; k++)
        {
            int i = group.Nodes[k];
            group.Load[k * 6 + 2] -= FluidSystem.Gravity * (grid.Mass[i] + RestingOn(grid, i));
            foreach (var (n, axis, lower) in Bonded(grid, i))
            {
                // Each bond once: from its lower end, or from the node when the other end is bedrock.
                if (IsBedrock(grid, n))
                {
                    group.Bonds.Add(MakeBond(grid, lower ? n : i, lower ? i : n, lower ? -1 : k, lower ? k : -1, axis));
                }
                else if (!lower)
                {
                    group.Bonds.Add(MakeBond(grid, i, n, k, _local[n], axis));
                }
            }
            // Ice with no room to grow pushes on everything around it.
            var (x, y, z) = grid.Coordinates(i);
            for (int f = 0; f < 6; f++)
            {
                int axis = f / 2, dir = f % 2 == 0 ? -1 : 1;
                int nx = x + (axis == 0 ? dir : 0), ny = y + (axis == 1 ? dir : 0), nz = z + (axis == 2 ? dir : 0);
                if (!grid.InBounds(nx, ny, nz)) continue;
                int n = grid.Index(nx, ny, nz);
                double p = Pressure(grid, n);
                if (p > 0.0) group.Load[k * 6 + axis] -= dir * p * WorldGrid.TileFaceAreaM2;
            }
        }
        return group;
    }

    private Bond MakeBond(WorldGrid grid, int a, int b, int nodeA, int nodeB, int axis)
    {
        double ea = _modulus[grid.Material[a]], eb = _modulus[grid.Material[b]];
        double modulus = 2.0 / (1.0 / ea + 1.0 / eb);
        double fill = Math.Min(Math.Min(grid.FillFraction(a), grid.FillFraction(b)), 1.0);
        return new Bond(a, b, nodeA, nodeB, axis, modulus, fill * WorldGrid.TileFaceAreaM2);
    }

    /// <summary>Mass of the rubble and liquid stacked directly on tile i, which it carries, kg.</summary>
    private double RestingOn(WorldGrid grid, int i)
    {
        int plane = grid.Width * grid.Height;
        double kg = 0.0;
        for (int j = i + plane; j < grid.Count; j += plane)
        {
            if (!grid.HasCondensed(j) || IsNode(grid, j) || grid.FallSpeed[j] > 0.0) break;
            kg += grid.Mass[j];
        }
        return kg;
    }

    /// <summary>The bonded neighbours of tile i, the axis of each bond, and whether the neighbour is the lower end.</summary>
    private IEnumerable<(int Neighbour, int Axis, bool Lower)> Bonded(WorldGrid grid, int i)
    {
        var (x, y, z) = grid.Coordinates(i);
        int[] step = { 1, grid.Width, grid.Width * grid.Height };
        int[] at = { x, y, z };
        int[] size = { grid.Width, grid.Height, grid.Depth };
        for (int axis = 0; axis < 3; axis++)
        {
            if (at[axis] > 0)
            {
                int n = i - step[axis];
                if (IsNode(grid, n) && (grid.Structure[n] & (1 << axis)) == 0) yield return (n, axis, true);
            }
            if (at[axis] + 1 < size[axis])
            {
                int n = i + step[axis];
                if (IsNode(grid, n) && (grid.Structure[i] & (1 << axis)) == 0) yield return (n, axis, false);
            }
        }
    }

    private static IEnumerable<int> FaceNeighbours(WorldGrid grid, int i)
    {
        var (x, y, z) = grid.Coordinates(i);
        if (x > 0) yield return i - 1;
        if (x + 1 < grid.Width) yield return i + 1;
        if (y > 0) yield return i - grid.Width;
        if (y + 1 < grid.Height) yield return i + grid.Width;
        int plane = grid.Width * grid.Height;
        if (z > 0) yield return i - plane;
        if (z + 1 < grid.Depth) yield return i + plane;
    }

    private bool IsSolid(WorldGrid grid, int i) =>
        grid.HasCondensed(i) && grid.Materials[grid.Material[i]].Phase == Phase.Solid;

    /// <summary>A tile that takes part in the lattice: a bonding solid, not rubble, not pressurised ice, thick enough.</summary>
    private bool IsNode(WorldGrid grid, int i) =>
        grid.HasCondensed(i)
        && _structural[grid.Material[i]]
        && (grid.Structure[i] & WorldGrid.LooseBit) == 0
        && grid.FillFraction(i) >= MinSectionFill
        && !IsPressurised(grid, i);

    private bool IsBedrock(WorldGrid grid, int i) => i < grid.Width * grid.Height;

    private bool IsPressurised(WorldGrid grid, int i) => Pressure(grid, i) > 0.0;

    /// <summary>
    /// Pressure a solid packed past full exerts on its neighbours, Pa: its bulk modulus times
    /// the overfill, and for ice no more than the pressure that would melt it at its present
    /// temperature. Zero unless every face is closed by a solid, a full liquid or the world's edge,
    /// and zero once it has cracked its container (loose).
    /// </summary>
    private double Pressure(WorldGrid grid, int i)
    {
        if (!grid.HasCondensed(i)) return 0.0;
        ushort m = grid.Material[i];
        if (!_structural[m] || (grid.Structure[i] & WorldGrid.LooseBit) != 0) return 0.0;
        double over = grid.FillFraction(i) - 1.0;
        if (over <= 1e-6) return 0.0;
        foreach (int n in FaceNeighbours(grid, i))
        {
            if (!grid.HasCondensed(n)) return 0.0;
            if (grid.Materials[grid.Material[n]].Phase != Phase.Solid && grid.FillFraction(n) < 0.999) return 0.0;
        }
        double p = _bulkModulus[m] * over;
        if (_meltDropPerPa[m] > 0.0) p = Math.Min(p, Math.Max(0.0, (_meltingPoint[m] - grid.Temperature[i]) / _meltDropPerPa[m]));
        return p;
    }

    // ---------------------------------------------------------------- solving

    private double[] _r = Array.Empty<double>(), _z = Array.Empty<double>(), _p = Array.Empty<double>(),
        _q = Array.Empty<double>(), _diag = Array.Empty<double>();

    /// <summary>Static equilibrium K·u = f by conjugate gradients with a diagonal preconditioner, from rest.</summary>
    private void Solve(WorldGrid grid, Group group)
    {
        int n = group.Nodes.Count * 6;
        if (_r.Length < n)
        {
            int size = n * 2;
            _r = new double[size]; _z = new double[size]; _p = new double[size]; _q = new double[size]; _diag = new double[size];
        }
        var u = group.Displacement;
        var f = group.Load;
        Array.Clear(u, 0, n);
        Array.Clear(_diag, 0, n);
        foreach (var bond in group.Bonds)
        {
            AddDiagonal(bond, bond.NodeA);
            AddDiagonal(bond, bond.NodeB);
        }

        double fNorm = 0.0;
        for (int k = 0; k < n; k++)
        {
            _r[k] = f[k];
            _z[k] = _r[k] / _diag[k];
            _p[k] = _z[k];
            fNorm += f[k] * f[k];
        }
        if (fNorm == 0.0) return;
        double rz = Dot(_r, _z, n);
        double tolerance = 1e-20 * fNorm;
        int maxIterations = Math.Max(200, 20 * n);
        for (int it = 0; it < maxIterations; it++)
        {
            Multiply(group, _p, _q, n);
            double alpha = rz / Dot(_p, _q, n);
            double rr = 0.0;
            for (int k = 0; k < n; k++)
            {
                u[k] += alpha * _p[k];
                _r[k] -= alpha * _q[k];
                rr += _r[k] * _r[k];
            }
            if (rr <= tolerance) break;
            for (int k = 0; k < n; k++) _z[k] = _r[k] / _diag[k];
            double rzNext = Dot(_r, _z, n);
            double beta = rzNext / rz;
            rz = rzNext;
            for (int k = 0; k < n; k++) _p[k] = _z[k] + beta * _p[k];
        }
    }

    private void AddDiagonal(in Bond bond, int node)
    {
        if (node < 0) return;
        int k = bond.Axis, p = (k + 1) % 3, q = (k + 2) % 3, o = node * 6;
        double l = BondLength;
        double shear = 12.0 * bond.BendingEI / (l * l * l), rotation = 4.0 * bond.BendingEI / l;
        _diag[o + k] += bond.AxialStiffness;
        _diag[o + p] += shear;
        _diag[o + q] += shear;
        _diag[o + 3 + k] += bond.TorsionStiffness;
        _diag[o + 3 + p] += rotation;
        _diag[o + 3 + q] += rotation;
    }

    private static double Dot(double[] a, double[] b, int n)
    {
        double sum = 0.0;
        for (int k = 0; k < n; k++) sum += a[k] * b[k];
        return sum;
    }

    private static void Multiply(Group group, double[] x, double[] y, int n)
    {
        Array.Clear(y, 0, n);
        Span<double> fa = stackalloc double[6];
        Span<double> fb = stackalloc double[6];
        foreach (var bond in group.Bonds)
        {
            EndForces(bond, x, fa, fb);
            if (bond.NodeA >= 0) for (int d = 0; d < 6; d++) y[bond.NodeA * 6 + d] += fa[d];
            if (bond.NodeB >= 0) for (int d = 0; d < 6; d++) y[bond.NodeB * 6 + d] += fb[d];
        }
    }

    /// <summary>
    /// The forces and moments a bond's two ends need to hold displacement x, in global axes:
    /// a 3D Euler-Bernoulli beam along the bond's axis, written in the frame (axis, p, q) with
    /// p and q the next two axes in turn, so the frame stays right-handed.
    /// </summary>
    private static void EndForces(in Bond bond, double[] x, Span<double> fa, Span<double> fb)
    {
        int k = bond.Axis, p = (k + 1) % 3, q = (k + 2) % 3;
        int oa = bond.NodeA * 6, ob = bond.NodeB * 6;
        double Get(int o, int d) => o < 0 ? 0.0 : x[o + d];
        double l = BondLength, ei = bond.BendingEI;
        double c1 = 12.0 * ei / (l * l * l), c2 = 6.0 * ei / (l * l), c3 = 4.0 * ei / l, c4 = 2.0 * ei / l;

        double du = Get(oa, k) - Get(ob, k);
        double dv = Get(oa, p) - Get(ob, p);
        double dw = Get(oa, q) - Get(ob, q);
        double txA = Get(oa, 3 + k), txB = Get(ob, 3 + k);
        double tyA = Get(oa, 3 + p), tyB = Get(ob, 3 + p);
        double tzA = Get(oa, 3 + q), tzB = Get(ob, 3 + q);

        double axial = bond.AxialStiffness * du;
        double torsion = bond.TorsionStiffness * (txA - txB);
        double shearP = c1 * dv + c2 * (tzA + tzB);
        double shearQ = c1 * dw - c2 * (tyA + tyB);

        fa[k] = axial; fb[k] = -axial;
        fa[p] = shearP; fb[p] = -shearP;
        fa[q] = shearQ; fb[q] = -shearQ;
        fa[3 + k] = torsion; fb[3 + k] = -torsion;
        fa[3 + q] = c2 * dv + c3 * tzA + c4 * tzB;
        fb[3 + q] = c2 * dv + c4 * tzA + c3 * tzB;
        fa[3 + p] = -c2 * dw + c3 * tyA + c4 * tyB;
        fb[3 + p] = -c2 * dw + c4 * tyA + c3 * tyB;
    }

    /// <summary>The bond closest to (or furthest past) breaking, and how loaded it is (1 = at its strength).</summary>
    private int WorstBond(WorldGrid grid, Group group, out double worstUtilisation)
    {
        Span<double> fa = stackalloc double[6];
        Span<double> fb = stackalloc double[6];
        foreach (int i in group.Nodes) _utilisation[i] = 0f;
        int worst = -1;
        worstUtilisation = 0.0;
        for (int b = 0; b < group.Bonds.Count; b++)
        {
            var bond = group.Bonds[b];
            EndForces(bond, group.Displacement, fa, fb);
            int k = bond.Axis, p = (k + 1) % 3, q = (k + 2) % 3;
            double a = bond.Side, area = bond.Area;
            double section = a * a * a / 6.0;

            double tension = -fa[k];
            double bending = Math.Max(Math.Abs(fa[3 + p]) + Math.Abs(fa[3 + q]), Math.Abs(fb[3 + p]) + Math.Abs(fb[3 + q])) / section;
            double maxTension = tension / area + bending;
            double maxCompression = -tension / area + bending;
            double shear = 1.5 * Math.Sqrt(fa[p] * fa[p] + fa[q] * fa[q]) / area + Math.Abs(fa[3 + k]) / (0.208 * a * a * a);

            double ft = Math.Min(Strength(grid, bond.A, _tensile), Strength(grid, bond.B, _tensile));
            double fc = Math.Min(Strength(grid, bond.A, _compressive), Strength(grid, bond.B, _compressive));
            double fs = 0.5 * Math.Min(ft, fc);
            double u = Math.Max(Ratio(maxTension, ft), Math.Max(Ratio(maxCompression, fc), Ratio(shear, fs)));

            if (bond.NodeA >= 0) _utilisation[bond.A] = (float)Math.Max(_utilisation[bond.A], u);
            if (bond.NodeB >= 0) _utilisation[bond.B] = (float)Math.Max(_utilisation[bond.B], u);
            if (u > worstUtilisation) { worstUtilisation = u; worst = b; }
        }
        return worst;
    }

    private static double Ratio(double stress, double strength) =>
        stress <= 0.0 ? 0.0 : strength <= 0.0 ? double.PositiveInfinity : stress / strength;

    /// <summary>A tile's strength from the given table, Pa, after any loss to heat.</summary>
    private double Strength(WorldGrid grid, int i, double[] table)
    {
        ushort m = grid.Material[i];
        double t = CombustionSystem.CoreTemperature(grid, i);
        double full = table[m];
        if (t <= _lossStart[m]) return full;
        if (t >= _lossEnd[m]) return 0.0;
        return full * (_lossEnd[m] - t) / (_lossEnd[m] - _lossStart[m]);
    }

    private void Prepare(WorldGrid grid)
    {
        if (_visited.Length != grid.Count)
        {
            _visited = new int[grid.Count];
            _local = new int[grid.Count];
            _utilisation = new float[grid.Count];
            _check = 0;
            _flood = 0;
        }
        var table = grid.Materials;
        if (ReferenceEquals(_preparedFor, table)) return;
        int count = table.Count;
        _structural = new bool[count];
        _modulus = new double[count];
        _tensile = new double[count];
        _compressive = new double[count];
        _lossStart = new double[count];
        _lossEnd = new double[count];
        _bulkModulus = new double[count];
        _meltDropPerPa = new double[count];
        _meltingPoint = new double[count];
        for (ushort id = 0; id < count; id++)
        {
            var def = table[id];
            if (def.Phase != Phase.Solid || def.YoungsModulus <= 0f) continue;
            _structural[id] = true;
            _modulus[id] = def.YoungsModulus * 1e9;
            _tensile[id] = def.TensileStrength * 1e6;
            _compressive[id] = def.CompressiveStrength * 1e6;
            _lossStart[id] = def.StrengthLossStartK;
            _lossEnd[id] = def.StrengthLossEndK;
            _bulkModulus[id] = _modulus[id] / (3.0 * (1.0 - 2.0 * PoissonRatio));
            _meltingPoint[id] = def.MeltingPoint;
            // Clausius-Clapeyron: dT/dp = T·Δv / L. Only a solid whose melt is denser melts under pressure.
            if (def.MeltsInto != null && def.LatentHeatFusion > 0f && table.TryGetId(def.MeltsInto, out var melt))
            {
                double dv = 1.0 / table[melt].Density - 1.0 / def.Density;
                if (dv < 0.0) _meltDropPerPa[id] = -def.MeltingPoint * dv / def.LatentHeatFusion;
            }
        }
        _preparedFor = table;
    }
}
