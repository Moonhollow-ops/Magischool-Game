using System;
using System.Collections.Generic;
using Magischool.Sim.Core;

namespace Magischool.Sim.Fluids;

/// <summary>
/// Layer 4b: air currents.
///
/// <para>
/// Air has a velocity on every tile face (<see cref="WorldGrid.AirVelocity"/>).
/// Each tick, air that is lighter than the still air around it (warmer, or
/// mixed with a light gas such as steam) accelerates upward, and drag slows
/// every current down (<see cref="AirDragSeconds"/>). Then a pressure solve
/// adjusts the velocities so that as much air leaves each tile as enters it:
/// rising air has to be replaced from the sides and pushes aside the air above
/// it. That turns lone updrafts into the loops that make real rooms behave,
/// so a hot stove sends a plume to the ceiling, the warm air spreads out under
/// it as a layer, and cool air slides in along the floor. Currents carry
/// everything in the air with them: heat, steam, and later smoke.
/// </para>
/// <para>
/// This is the standard incompressible-flow method (a staggered grid with a
/// pressure projection), simplified: velocity isn't carried along by the flow
/// itself (no momentum advection), so a current stops soon after whatever
/// drives it does instead of coasting across a room. The solve runs a fixed
/// number of iterations; anything it leaves unbalanced is evened out by the
/// ordinary pressure flow.
/// </para>
/// </summary>
public sealed partial class FluidSystem
{
    /// <summary>How quickly a current dies down once nothing drives it, s (the time for it to fall to 37%).</summary>
    public const double AirDragSeconds = 2.0;

    /// <summary>Fastest air may move through a face, m/s.</summary>
    public const double MaxAirSpeed = 3.0;

    /// <summary>Iterations of the pressure solve each tick (each one sweeps both colours).</summary>
    public const int ProjectionIterations = 20;

    private const double Overrelaxation = 1.6;

    /// <summary>Currents slower than this, with nothing driving them faster, stop (0.1 mm/s).</summary>
    private const double AirSpeedEpsilon = 1e-4;

    /// <summary>A tile may send at most this share of its gas out through currents in one tick.</summary>
    private const double MaxCurrentOutFraction = 0.3;

    private static readonly double AirDamping = Math.Exp(-Simulation.SecondsPerTick / AirDragSeconds);

    // Faces open to air, per tile: bits -x, +x, -y, +y, -z, +z (the +z bit of a top tile is the sky).
    private const byte FaceMinusX = 1, FacePlusX = 2, FaceMinusY = 4, FacePlusY = 8, FaceMinusZ = 16, FacePlusZ = 32;

    private byte[] _airOpen = Array.Empty<byte>();
    private double[] _airPhi = Array.Empty<double>();
    private double[] _airDivergence = Array.Empty<double>();
    private double[] _airDrive = Array.Empty<double>();
    private double[] _airOutScale = Array.Empty<double>();
    private double _referenceDensityFactor;
    private bool[] _airUnbalanced = Array.Empty<bool>();
    private readonly List<int> _airSolveChunks = new();

    private void PrepareAir(WorldGrid grid)
    {
        if (_airOpen.Length == grid.Count) return;
        _airOpen = new byte[grid.Count];
        _airPhi = new double[grid.Count];
        _airDivergence = new double[grid.Count];
        _airDrive = new double[grid.Count];
        _airOutScale = new double[grid.Count];
    }

    /// <summary>Updates the air velocity field from the start-of-tick state in pass 0.</summary>
    private void UpdateAir(Simulation sim, List<int> chunks)
    {
        var grid = sim.Grid;
        var activity = sim.Activity;
        PrepareAir(grid);
        int width = grid.Width, plane = grid.Width * grid.Height;
        double[] u = grid.AirVelocity[0], v = grid.AirVelocity[1], w = grid.AirVelocity[2];

        // Which faces air can cross, then drag and buoyancy on the faces each tile owns (+x, +y, +z).
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                byte open = 0;
                if (!_gas[i].Locked)
                {
                    if (x > 0 && AirCell(grid, activity, i - 1, x - 1, y, z, x0, x1, y0, y1, z0, z1)) open |= FaceMinusX;
                    if (x + 1 < grid.Width && AirCell(grid, activity, i + 1, x + 1, y, z, x0, x1, y0, y1, z0, z1)) open |= FacePlusX;
                    if (y > 0 && AirCell(grid, activity, i - width, x, y - 1, z, x0, x1, y0, y1, z0, z1)) open |= FaceMinusY;
                    if (y + 1 < grid.Height && AirCell(grid, activity, i + width, x, y + 1, z, x0, x1, y0, y1, z0, z1)) open |= FacePlusY;
                    if (z > 0 && AirCell(grid, activity, i - plane, x, y, z - 1, x0, x1, y0, y1, z0, z1)) open |= FaceMinusZ;
                    if (z + 1 < grid.Depth ? AirCell(grid, activity, i + plane, x, y, z + 1, x0, x1, y0, y1, z0, z1) : _openSky)
                        open |= FacePlusZ;
                }
                _airOpen[i] = open;

                u[i] = (open & FacePlusX) != 0 ? Clamp(u[i] * AirDamping) : 0.0;
                v[i] = (open & FacePlusY) != 0 ? Clamp(v[i] * AirDamping) : 0.0;
                double drive = 0.0;
                if ((open & FacePlusZ) != 0)
                {
                    double above = z + 1 < grid.Depth ? PotentialDensity(grid, i + plane) : 1.0;
                    drive = Gravity * (1.0 - 0.5 * (PotentialDensity(grid, i) + above));
                    w[i] = Clamp(w[i] * AirDamping + drive * Simulation.SecondsPerTick);
                }
                else w[i] = 0.0;
                _airDrive[i] = drive;
            }
        });

        // Pressure solve: find φ so that correcting each face by -Δφ / dx leaves no tile with more
        // air leaving than arriving. The sky is held at φ = 0. Red-black successive over-relaxation:
        // each half-sweep updates one colour from the other, so it is parallel and deterministic.
        if (_airUnbalanced.Length != activity.Count) _airUnbalanced = new bool[activity.Count];
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            bool unbalanced = false;
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                byte open = _airOpen[i];
                double outflow = 0.0;
                if ((open & FacePlusX) != 0) outflow += u[i];
                if ((open & FaceMinusX) != 0) outflow -= u[i - 1];
                if ((open & FacePlusY) != 0) outflow += v[i];
                if ((open & FaceMinusY) != 0) outflow -= v[i - width];
                if ((open & FacePlusZ) != 0) outflow += w[i];
                if ((open & FaceMinusZ) != 0) outflow -= w[i - plane];
                _airDivergence[i] = outflow;
                _airPhi[i] = 0.0;
                if (outflow != 0.0) unbalanced = true;
            }
            _airUnbalanced[chunk] = unbalanced;
        });

        // Only solve where air is out of balance, plus one chunk around it. Elsewhere φ stays 0,
        // which is exact for still air; the ordinary pressure flow evens out what this leaves.
        _airSolveChunks.Clear();
        foreach (int chunk in chunks)
            if (NearUnbalanced(activity, chunk)) _airSolveChunks.Add(chunk);

        double[] phi = _airPhi;
        for (int sweep = 0; sweep < 2 * ProjectionIterations; sweep++)
        {
            int colour = sweep & 1;
            ChunkLoop.Run(sim, _airSolveChunks, chunk =>
            {
                var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
                for (int z = z0; z < z1; z++)
                for (int y = y0; y < y1; y++)
                {
                    int start = x0 + ((x0 + y + z + colour) & 1);
                    for (int x = start; x < x1; x += 2)
                    {
                        int i = grid.Index(x, y, z);
                        byte open = _airOpen[i];
                        if (open == 0) continue;
                        double sum = 0.0;
                        int n = 0;
                        if ((open & FaceMinusX) != 0) { sum += phi[i - 1]; n++; }
                        if ((open & FacePlusX) != 0) { sum += phi[i + 1]; n++; }
                        if ((open & FaceMinusY) != 0) { sum += phi[i - width]; n++; }
                        if ((open & FacePlusY) != 0) { sum += phi[i + width]; n++; }
                        if ((open & FaceMinusZ) != 0) { sum += phi[i - plane]; n++; }
                        if ((open & FacePlusZ) != 0) { if (z + 1 < grid.Depth) sum += phi[i + plane]; n++; }
                        double target = (sum - TileSizeM * _airDivergence[i]) / n;
                        phi[i] += Overrelaxation * (target - phi[i]);
                    }
                }
            });
        }

        // Correct the owned faces, stop negligible currents, and work out how much each tile sends out.
        double faceVolume = WorldGrid.TileFaceAreaM2 * Simulation.SecondsPerTick;
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                byte open = _airOpen[i];
                double phi = _airPhi[i];
                if ((open & FacePlusX) != 0) u[i] = Settle(Clamp(u[i] - (_airPhi[i + 1] - phi) / TileSizeM), 0.0);
                if ((open & FacePlusY) != 0) v[i] = Settle(Clamp(v[i] - (_airPhi[i + width] - phi) / TileSizeM), 0.0);
                if ((open & FacePlusZ) != 0)
                {
                    double above = z + 1 < grid.Depth ? _airPhi[i + plane] : 0.0;
                    w[i] = Settle(Clamp(w[i] - (above - phi) / TileSizeM), _airDrive[i]);
                }
            }
        });

        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            bool moving = false;
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                byte open = _airOpen[i];
                double outgoing = 0.0;
                if ((open & FacePlusX) != 0) outgoing += Math.Max(0.0, u[i]);
                if ((open & FaceMinusX) != 0) outgoing += Math.Max(0.0, -u[i - 1]);
                if ((open & FacePlusY) != 0) outgoing += Math.Max(0.0, v[i]);
                if ((open & FaceMinusY) != 0) outgoing += Math.Max(0.0, -v[i - width]);
                if ((open & FacePlusZ) != 0) outgoing += Math.Max(0.0, w[i]);
                if ((open & FaceMinusZ) != 0) outgoing += Math.Max(0.0, -w[i - plane]);
                if (outgoing != 0.0) moving = true;
                double volume = outgoing * faceVolume;
                double limit = MaxCurrentOutFraction * _gas[i].Free;
                _airOutScale[i] = volume > limit ? limit / volume : 1.0;
            }
            if (moving) activity.Stirred[chunk] = true;
        });
    }

    /// <summary>True when this chunk or any awake chunk touching it has air out of balance.</summary>
    private bool NearUnbalanced(ActivityMap activity, int chunk)
    {
        int cx = chunk % activity.ChunksX, rest = chunk / activity.ChunksX;
        int cy = rest % activity.ChunksY, cz = rest / activity.ChunksY;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int nx = cx + dx, ny = cy + dy, nz = cz + dz;
            if ((uint)nx >= (uint)activity.ChunksX || (uint)ny >= (uint)activity.ChunksY || (uint)nz >= (uint)activity.ChunksZ) continue;
            int n = (nz * activity.ChunksY + ny) * activity.ChunksX + nx;
            if (activity.Awake[n] && _airUnbalanced[n]) return true;
        }
        return false;
    }

    /// <summary>True when the neighbour at (nx, ny, nz) is open to air and awake this tick.</summary>
    private bool AirCell(WorldGrid grid, ActivityMap activity, int j, int nx, int ny, int nz,
        int x0, int x1, int y0, int y1, int z0, int z1)
    {
        bool inside = nx >= x0 && nx < x1 && ny >= y0 && ny < y1 && nz >= z0 && nz < z1;
        if (!inside && !activity.IsAwakeAt(nx, ny, nz)) return false;
        return !_gas[j].Locked;
    }

    /// <summary>
    /// Density of a tile's gas relative to still sky air at the same pressure (M / T compared
    /// with the reference), so weight from being lower down doesn't count, only warmth and mixture.
    /// </summary>
    private double PotentialDensity(WorldGrid grid, int i)
    {
        var g = _gas[i];
        if (g.Total <= 0.0) return 1.0;
        return _referenceDensityFactor / (g.MolPerKg * g.Temperature);
    }

    private static double Clamp(double speed) => Math.Clamp(speed, -MaxAirSpeed, MaxAirSpeed);

    /// <summary>Zero a current that is too slow to matter and that nothing is pushing faster.</summary>
    private static double Settle(double speed, double drive) =>
        Math.Abs(speed) < AirSpeedEpsilon && Math.Abs(drive) * AirDragSeconds < AirSpeedEpsilon ? 0.0 : speed;

    /// <summary>
    /// Gas carried by the current through the face between lo and its +axis neighbour hi,
    /// added to the face's flux (positive: lo to hi).
    /// </summary>
    private bool Advect(WorldGrid grid, int lo, int hi, int axis, in Gas gl, in Gas gh, Span<double> gasFlux, ref double energy)
    {
        double speed = grid.AirVelocity[axis][lo];
        if (speed == 0.0) return false;
        bool forward = speed > 0.0;
        int src = forward ? lo : hi;
        var gs = forward ? gl : gh;
        if (gs.Total <= 0.0 || gs.Free <= 0.0) return false;
        double volume = Math.Abs(speed) * WorldGrid.TileFaceAreaM2 * Simulation.SecondsPerTick * _airOutScale[src];
        double sign = forward ? 1.0 : -1.0;
        double t = gs.Temperature;
        double[][] gas = grid.GasMass;
        for (int s = 0; s < gasFlux.Length; s++)
        {
            double part = volume * (gas[s][src] / gs.Free);
            if (part == 0.0) continue;
            gasFlux[s] += sign * part;
            energy += sign * part * (_gasSpecificHeat[s] * t + _gasOffset[s]);
        }
        return true;
    }

    /// <summary>Gas the current carries between a top tile and the sky (positive: out).</summary>
    private bool AdvectSky(WorldGrid grid, int i, Span<double> gasFlux, ref double energy)
    {
        double speed = grid.AirVelocity[2][i];
        if (speed == 0.0) return false;
        var g = _gas[i];
        double volume = Math.Abs(speed) * WorldGrid.TileFaceAreaM2 * Simulation.SecondsPerTick;
        if (speed > 0.0)
        {
            if (g.Total <= 0.0 || g.Free <= 0.0) return false;
            volume *= _airOutScale[i];
            double t = _gas[i].Temperature;
            for (int s = 0; s < gasFlux.Length; s++)
            {
                double part = volume * (grid.GasMass[s][i] / g.Free);
                gasFlux[s] += part;
                energy += part * (_gasSpecificHeat[s] * t + _gasOffset[s]);
            }
        }
        else
        {
            double mass = volume * _skyDensity;
            for (int s = 0; s < gasFlux.Length; s++) gasFlux[s] -= mass * _skyFraction[s];
            energy -= mass * _skyEnergyPerKg;
        }
        return true;
    }
}
