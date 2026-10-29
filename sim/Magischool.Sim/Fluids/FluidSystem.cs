using System;
using System.Collections.Generic;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Fluids;

/// <summary>
/// Layer 4: liquids flowing and gases moving.
///
/// <para>
/// Liquids fall, pool and spread. A tile's liquid falls into the tile below
/// until that one is full; liquid resting on something spreads sideways
/// toward emptier neighbours, slowed by its viscosity, and stops once it is a
/// thin film (<see cref="MinSpreadDepthM"/>). Liquid lower down is held very
/// slightly overfull, <see cref="LiquidCompression"/> per tile of depth, which
/// stands in for hydrostatic pressure: an overfull tile pushes liquid up and
/// sideways, so water finds its level in connected vessels and presses on
/// whatever holds it. Two different liquids don't mix or displace each other
/// yet; they stop where they meet.
/// </para>
/// <para>
/// Gases fill whatever volume the condensed contents leave free. Each tile's
/// pressure comes from the ideal gas law. Gas flows from high to low pressure
/// (allowing for its own weight, so air is denser at the bottom of a room),
/// is carried by air currents (see FluidSystem.Air.cs), and gas mixtures slowly
/// even out. Gas can't flow into a tile that
/// is almost completely full of solid or liquid, but trapped gas bubbles up
/// through liquid.
/// </para>
/// <para>
/// With an open sky (the default), the top of the world opens onto still air
/// that goes on forever: warm air, smoke and steam leave through it and fresh
/// air comes in, so a fire outdoors doesn't pressurise the whole city. What
/// crosses it is counted in <see cref="SkyGasOutKg"/> and <see cref="SkyEnergyOutJ"/>.
/// The world's sides and floor are sealed.
/// </para>
/// <para>
/// Moving matter carries its energy with it (sensible heat, latent heat, and
/// the heat any part-finished melting or freezing holds), so mass and energy
/// are both conserved to rounding. Every face is computed identically from both
/// sides from the start-of-tick state, so the result never depends on thread
/// count.
/// </para>
/// <para>
/// Not modelled yet: momentum (water doesn't slosh or jet, and pressure evens
/// out by diffusion rather than as a sound wave), the work done compressing a
/// gas, liquids displacing each other by density, and boiling points that
/// depend on pressure.
/// </para>
/// </summary>
public sealed partial class FluidSystem : ISimSystem
{
    public const double Gravity = 9.81;
    public const double GasConstant = 8.314462618;

    /// <summary>How much extra a liquid tile holds per full tile of the same liquid above it (a stand-in for hydrostatic pressure).</summary>
    public const double LiquidCompression = 0.01;

    /// <summary>Liquid thinner than this (a 5 mm film) stops spreading.</summary>
    public const double MinSpreadDepthM = 0.005;

    /// <summary>A tile whose free volume is below this fraction is closed to incoming gas.</summary>
    public const double GasLockFraction = 0.01;

    /// <summary>
    /// Mixing of gases in still air beyond what the resolved currents carry, m²/s: the small
    /// eddies and draughts too fine for half-metre tiles. Molecular diffusion alone is
    /// about 2e-5 m²/s.
    /// </summary>
    public const double EddyDiffusivity = 0.002;

    // Liquid rates and per-face caps, as fractions of the source tile's liquid. The caps over
    // all six faces (down, four sides, up) add to 0.9, so a tile can never go negative.
    private const double FallRate = 0.5, FallCap = 0.5;
    private const double SpreadRate = 0.2, SideCap = 0.08, RiseCap = 0.08;
    private const double VerticalEpsilon = 1e-7; // fill fraction
    private const double SideEpsilon = 1e-4;     // fill fraction
    private const double TinyFill = 1e-8;        // a droplet this small falls all at once; below VerticalEpsilon

    // Gas rates and per-face caps, as fractions of the source tile's gas. Six faces of pressure
    // flow and exchange plus one bubble face add to 13/14.
    private const double PressureRelax = 1.0 / 7.0, PressureCap = 1.0 / 14.0;
    private const double ExchangeCap = 1.0 / 14.0, BubbleFraction = 1.0 / 14.0;
    private const double PressureEpsilonPa = 1e-3;
    private const double CompositionEpsilon = 1e-6;
    private const double BubbleMinKg = 1e-9;

    private const double TileSizeM = WorldGrid.TileSizeM;
    private const double DiffusionFraction = EddyDiffusivity * Simulation.SecondsPerTick / (TileSizeM * TileSizeM);
    private const double LockVolumeM3 = GasLockFraction * WorldGrid.TileVolumeM3;
    private const double EmptyResidueKg = 1e-12;

    private readonly bool _openSky;
    private readonly double _skyTemperatureK;
    private readonly double _groundPressurePa;
    private IReadOnlyList<(string Key, double Fraction)>? _skyMix;
    // Mass share of each gas species in the sky.
    private double[] _skyFraction = Array.Empty<double>();
    private double _skyPressure, _skyDensity, _skyMolPerKg, _skyEnergyPerKg;
    private int _preparedDepth = -1;
    private double[] _skyStepEnergy = Array.Empty<double>();
    private double[][] _skyStepGas = Array.Empty<double[]>();

    private MaterialTable? _preparedFor;
    private bool[] _isLiquid = Array.Empty<bool>();
    private double[] _fullMass = Array.Empty<double>();
    private double[] _minSpreadKg = Array.Empty<double>();
    private double[] _viscosityFactor = Array.Empty<double>();
    private double[] _specificHeat = Array.Empty<double>();
    private double[] _offset = Array.Empty<double>();
    private double[] _gasSpecificHeat = Array.Empty<double>();
    private double[] _gasOffset = Array.Empty<double>();
    private double[] _gasMolPerKg = Array.Empty<double>();

    // Start-of-tick state of awake tiles (pass 0).
    private ushort[] _claim = Array.Empty<ushort>();
    private Gas[] _gas = Array.Empty<Gas>();

    // Net change per awake tile (pass 1).
    private double[] _dLiquid = Array.Empty<double>();
    private double[] _dEnergy = Array.Empty<double>();
    private double[] _dGasEnergy = Array.Empty<double>();
    private double[] _dLatent = Array.Empty<double>();
    private double[][] _dGas = Array.Empty<double[]>();

    public string Name => "fluid";

    /// <param name="openSky">True for an open top boundary; false seals the world on all sides (lab tests).</param>
    /// <param name="skyTemperatureK">Temperature of the air beyond the top of the world.</param>
    /// <param name="groundPressurePa">Still-air pressure at z = 0, which sets the sky's pressure above the top.</param>
    /// <remarks>What the sky is made of is the world's (<see cref="Simulation.SkyGas"/>).</remarks>
    public FluidSystem(bool openSky = true, double skyTemperatureK = BuiltInMaterials.RoomTemperatureK,
        double groundPressurePa = Atmosphere.StandardPressurePa)
    {
        _openSky = openSky;
        _skyTemperatureK = skyTemperatureK;
        _groundPressurePa = groundPressurePa;
    }

    /// <summary>Net gas that has left through the sky since this system was created, kg per species (negative: came in).</summary>
    public double[] SkyGasOutKg { get; private set; } = Array.Empty<double>();

    /// <summary>Net energy that has left through the sky since this system was created, J, counted as <see cref="EnergyAudit"/> counts it.</summary>
    public double SkyEnergyOutJ { get; private set; }

    /// <summary>A tile's gas, as the flow rules see it.</summary>
    private struct Gas
    {
        public double Free;      // m³ not taken by solid or liquid
        public double Total;     // kg
        public double MolPerKg;  // of the mixture; 0 when there is no gas
        public double Pressure;  // Pa
        public double Density;   // kg/m³
        public double Temperature; // K, of the gas
        public bool Locked => Free < LockVolumeM3;
    }

    /// <summary>Gas pressure of a tile, Pa.</summary>
    public static double PressureAt(WorldGrid grid, int index)
    {
        var table = grid.Materials;
        double mol = 0.0;
        for (int s = 0; s < table.GasCount; s++)
            mol += grid.GasMass[s][index] / (double)table[table.GasIds[s]].MolarMass;
        return mol * GasConstant * grid.GasTemperatureAt(index) / Math.Max(grid.FreeVolume(index), WorldGrid.MinFreeVolumeM3);
    }

    /// <summary>Total mass of one material in the world, kg, whichever phase slot holds it (water counts what soil holds in its pores).</summary>
    public static double TotalMass(WorldGrid grid, ushort material)
    {
        double total = 0.0;
        int species = grid.Materials.GasIndexOf(material);
        bool water = grid.Materials.TryGetId(BuiltInMaterials.Water, out ushort waterId) && waterId == material;
        for (int i = 0; i < grid.Count; i++)
        {
            if (species >= 0) total += grid.GasMass[species][i];
            else if (grid.Material[i] == material) total += grid.Mass[i];
            if (water) total += grid.PoreWater[i];
        }
        return total;
    }

    public void Update(Simulation sim)
    {
        var grid = sim.Grid;
        var activity = sim.Activity;
        Prepare(grid, sim.SkyGas);
        var chunks = activity.AwakeChunks();

        // Pass 0: record the start-of-tick state that flows are computed from.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                _claim[i] = ComputeClaim(grid, i);
                _gas[i] = ComputeGas(grid, i);
            }
        });

        UpdateAir(sim, chunks);

        // Pass 1: each tile sums the flow across its six faces. Faces are always evaluated as
        // (lower index, higher index), so both sides get bitwise-identical results.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            int gasCount = _dGas.Length;
            Span<double> gasFlux = stackalloc double[gasCount];
            Span<double> gasNet = stackalloc double[gasCount];
            int width = grid.Width, plane = grid.Width * grid.Height;
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                double liquid = 0.0, energy = 0.0, gasEnergy = 0.0, latent = 0.0;
                gasNet.Clear();
                bool moved = false;
                for (int face = 0; face < 6; face++)
                {
                    int nx = x, ny = y, nz = z, axis, j;
                    switch (face)
                    {
                        case 0: nx--; axis = 0; j = i - 1; break;
                        case 1: nx++; axis = 0; j = i + 1; break;
                        case 2: ny--; axis = 1; j = i - width; break;
                        case 3: ny++; axis = 1; j = i + width; break;
                        case 4: nz--; axis = 2; j = i - plane; break;
                        default: nz++; axis = 2; j = i + plane; break;
                    }
                    if (!grid.InBounds(nx, ny, nz)) continue;

                    bool inside = nx >= x0 && nx < x1 && ny >= y0 && ny < y1 && nz >= z0 && nz < z1;
                    bool neighbourAwake = inside || activity.IsAwakeAt(nx, ny, nz);
                    int lo = Math.Min(i, j), hi = Math.Max(i, j);
                    bool loAwake = lo == i || neighbourAwake, hiAwake = hi == i || neighbourAwake;

                    if (!Exchange(grid, lo, hi, axis, loAwake, hiAwake, gasFlux, out double fl, out double fe, out double fg, out double fs))
                        continue;

                    if (!neighbourAwake)
                    {
                        // Closed: a sleeping chunk takes nothing until it wakes, which this asks for.
                        activity.Stirred[activity.ChunkAt(nx, ny, nz)] = true;
                        continue;
                    }

                    moved = true;
                    if (i == lo)
                    {
                        liquid -= fl; energy -= fe; gasEnergy -= fg; latent -= fs;
                        for (int s = 0; s < gasCount; s++) gasNet[s] -= gasFlux[s];
                    }
                    else
                    {
                        liquid += fl; energy += fe; gasEnergy += fg; latent += fs;
                        for (int s = 0; s < gasCount; s++) gasNet[s] += gasFlux[s];
                    }
                }

                if (_openSky && z == grid.Depth - 1)
                {
                    int top = i - (grid.Depth - 1) * plane;
                    double skyEnergy = 0.0;
                    if (SkyExchange(grid, i, gasFlux, out skyEnergy))
                    {
                        moved = true;
                        gasEnergy -= skyEnergy;
                        for (int s = 0; s < gasCount; s++) gasNet[s] -= gasFlux[s];
                    }
                    else gasFlux.Clear();
                    _skyStepEnergy[top] = skyEnergy;
                    for (int s = 0; s < gasCount; s++) _skyStepGas[s][top] = gasFlux[s];
                }

                _dLiquid[i] = liquid;
                _dEnergy[i] = energy;
                _dGasEnergy[i] = gasEnergy;
                _dLatent[i] = latent;
                for (int s = 0; s < gasCount; s++) _dGas[s][i] = gasNet[s];
                if (moved) activity.Stirred[chunk] = true;
            }
        });

        if (_openSky) CountSky(grid, activity, chunks);

        // Pass 2: apply mass changes, then solve each changed tile's temperature from its energy.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            if (!activity.Stirred[chunk]) return;
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                Apply(grid, grid.Index(x, y, z));
        });
    }

    /// <summary>
    /// Everything crossing the face between tiles lo and hi this tick, as flow from lo to hi
    /// (hi is lo's +x, +y or +z neighbour). Returns false when nothing crosses.
    /// </summary>
    private bool Exchange(WorldGrid grid, int lo, int hi, int axis, bool loAwake, bool hiAwake,
        Span<double> gasFlux, out double liquid, out double energy, out double gasEnergy, out double latent)
    {
        liquid = 0.0;
        energy = 0.0;
        gasEnergy = 0.0;
        latent = 0.0;
        gasFlux.Clear();
        bool any = false;
        double[] temperature = grid.Temperature;

        // Liquid.
        ushort ml = grid.Material[lo], mh = grid.Material[hi];
        bool liquidLo = _isLiquid[ml], liquidHi = _isLiquid[mh];
        if (liquidLo || liquidHi)
        {
            ushort l = liquidLo ? ml : mh;
            bool connected = liquidLo && liquidHi
                ? ml == mh
                : liquidLo
                    ? mh == MaterialTable.Empty && ClaimAt(grid, hi, hiAwake) == l
                    : ml == MaterialTable.Empty && ClaimAt(grid, lo, loAwake) == l;
            if (connected)
            {
                double kg = LiquidFlow(grid, lo, hi, axis, l);
                if (kg != 0.0)
                {
                    int src = kg > 0.0 ? lo : hi;
                    double moved = Math.Abs(kg);
                    double share = grid.LatentEnergy[src] * (moved / grid.Mass[src]);
                    double e = moved * (_specificHeat[l] * temperature[src] + _offset[l]) + share;
                    liquid = kg;
                    energy = kg > 0.0 ? e : -e;
                    latent = kg > 0.0 ? share : -share;
                    any = true;
                }
            }
        }

        // Gas.
        if (gasFlux.Length == 0) return any;
        var gl = GasAt(grid, lo, loAwake);
        var gh = GasAt(grid, hi, hiAwake);
        if (gl.Total <= 0.0 && gh.Total <= 0.0) return any;
        double[][] gas = grid.GasMass;

        // Pressure flow, allowing for the weight of the gas between the two tile centres.
        double dP = gl.Pressure - gh.Pressure;
        if (axis == 2) dP -= 0.5 * (gl.Density + gh.Density) * Gravity * TileSizeM;
        if (Math.Abs(dP) > PressureEpsilonPa)
        {
            bool forward = dP > 0.0;
            int src = forward ? lo : hi, dst = forward ? hi : lo;
            var gs = forward ? gl : gh;
            var gd = forward ? gh : gl;
            if (!gd.Locked && gs.Total > 0.0)
            {
                double perKg = GasConstant * gs.MolPerKg;
                double stiffness = perKg * gs.Temperature / Math.Max(gs.Free, WorldGrid.MinFreeVolumeM3)
                                 + perKg * gd.Temperature / Math.Max(gd.Free, WorldGrid.MinFreeVolumeM3);
                double dm = Math.Min(Math.Abs(dP) / stiffness * PressureRelax, gs.Total * PressureCap);
                if (dm > 0.0)
                {
                    double sign = forward ? 1.0 : -1.0;
                    for (int s = 0; s < gasFlux.Length; s++)
                    {
                        double part = dm * (gas[s][src] / gs.Total);
                        if (part == 0.0) continue;
                        gasFlux[s] += sign * part;
                        gasEnergy += sign * part * (_gasSpecificHeat[s] * gs.Temperature + _gasOffset[s]);
                    }
                    any = true;
                }
            }
        }

        // Carried by the air current through this face.
        if (loAwake && hiAwake && Advect(grid, lo, hi, axis, gl, gh, gasFlux, ref gasEnergy)) any = true;

        // Exchange of equal volumes: gases slowly mixing.
        if (!gl.Locked && !gh.Locked && gl.Total > 0.0 && gh.Total > 0.0)
        {
            double fraction = 0.0;
            for (int s = 0; s < gasFlux.Length; s++)
            {
                if (Math.Abs(gas[s][lo] / gl.Total - gas[s][hi] / gh.Total) > CompositionEpsilon)
                {
                    fraction = DiffusionFraction;
                    break;
                }
            }
            if (fraction > 0.0)
            {
                double volume = Math.Min(fraction, ExchangeCap) * Math.Min(gl.Free, gh.Free);
                for (int s = 0; s < gasFlux.Length; s++)
                {
                    double up = volume * (gas[s][lo] / gl.Free);
                    double down = volume * (gas[s][hi] / gh.Free);
                    if (up == 0.0 && down == 0.0) continue;
                    gasFlux[s] += up - down;
                    gasEnergy += up * (_gasSpecificHeat[s] * gl.Temperature + _gasOffset[s])
                               - down * (_gasSpecificHeat[s] * gh.Temperature + _gasOffset[s]);
                }
                any = true;
            }
        }

        // Gas trapped in liquid bubbles up through liquid above it.
        if (axis == 2 && gh.Locked && _isLiquid[ml] && _isLiquid[mh] && gl.Total > BubbleMinKg)
        {
            for (int s = 0; s < gasFlux.Length; s++)
            {
                double part = gas[s][lo] * BubbleFraction;
                if (part == 0.0) continue;
                gasFlux[s] += part;
                gasEnergy += part * (_gasSpecificHeat[s] * gl.Temperature + _gasOffset[s]);
            }
            any = true;
        }

        return any;
    }

    /// <summary>
    /// Gas crossing from a top-layer tile into the sky this tick (negative: sky air coming in),
    /// by pressure, rising and mixing, as with any other upward face. The sky never changes.
    /// </summary>
    private bool SkyExchange(WorldGrid grid, int i, Span<double> gasFlux, out double energy)
    {
        energy = 0.0;
        gasFlux.Clear();
        var g = _gas[i];
        if (g.Locked && g.Total <= 0.0) return false;
        double t = g.Temperature;
        double[][] gas = grid.GasMass;
        bool any = false;

        double dP = g.Pressure - _skyPressure - 0.5 * (g.Density + _skyDensity) * Gravity * TileSizeM;
        if (dP > PressureEpsilonPa && g.Total > 0.0)
        {
            double stiffness = GasConstant * g.MolPerKg * t / Math.Max(g.Free, WorldGrid.MinFreeVolumeM3);
            double dm = Math.Min(dP / stiffness * PressureRelax, g.Total * PressureCap);
            for (int s = 0; s < gasFlux.Length; s++)
            {
                double part = dm * (gas[s][i] / g.Total);
                gasFlux[s] += part;
                energy += part * (_gasSpecificHeat[s] * t + _gasOffset[s]);
            }
            any = dm > 0.0;
        }
        else if (dP < -PressureEpsilonPa && !g.Locked)
        {
            double stiffness = GasConstant * _skyMolPerKg * t / Math.Max(g.Free, WorldGrid.MinFreeVolumeM3);
            double dm = Math.Min(-dP / stiffness * PressureRelax, Math.Max(g.Total, _skyDensity * g.Free) * PressureCap);
            for (int s = 0; s < gasFlux.Length; s++) gasFlux[s] -= dm * _skyFraction[s];
            energy -= dm * _skyEnergyPerKg;
            any = dm > 0.0;
        }

        if (AdvectSky(grid, i, gasFlux, ref energy)) any = true;

        if (!g.Locked && g.Total > 0.0)
        {
            // How far the tile's makeup is from the sky's: half the summed differences, the share that would have to change.
            double apart = 0.0;
            for (int s = 0; s < gasFlux.Length; s++) apart += Math.Abs(gas[s][i] / g.Total - _skyFraction[s]);
            double fraction = 0.5 * apart > CompositionEpsilon ? DiffusionFraction : 0.0;
            if (fraction > 0.0)
            {
                double volume = Math.Min(fraction, ExchangeCap) * g.Free;
                for (int s = 0; s < gasFlux.Length; s++)
                {
                    double up = volume * (gas[s][i] / g.Free);
                    gasFlux[s] += up;
                    energy += up * (_gasSpecificHeat[s] * t + _gasOffset[s]);
                }
                double down = volume * _skyDensity;
                for (int s = 0; s < gasFlux.Length; s++) gasFlux[s] -= down * _skyFraction[s];
                energy -= down * _skyEnergyPerKg;
                any = true;
            }
        }
        return any;
    }

    /// <summary>Adds this tick's sky crossings to the running totals, in a fixed order.</summary>
    private void CountSky(WorldGrid grid, ActivityMap activity, System.Collections.Generic.List<int> chunks)
    {
        int plane = grid.Width * grid.Height;
        foreach (int chunk in chunks)
        {
            var (x0, y0, _, x1, y1, z1) = activity.Bounds(chunk);
            if (z1 != grid.Depth) continue;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int top = y * grid.Width + x;
                SkyEnergyOutJ += _skyStepEnergy[top];
                for (int s = 0; s < _skyStepGas.Length; s++) SkyGasOutKg[s] += _skyStepGas[s][top];
            }
        }
    }

    /// <summary>Kilograms of liquid l moving from lo to hi this tick (negative: hi to lo).</summary>
    private double LiquidFlow(WorldGrid grid, int lo, int hi, int axis, ushort l)
    {
        double full = _fullMass[l];
        double mLo = grid.Material[lo] == l ? grid.Mass[lo] : 0.0;
        double mHi = grid.Material[hi] == l ? grid.Mass[hi] : 0.0;
        double fLo = mLo / full, fHi = mHi / full;

        if (axis == 2)
        {
            // lo is below hi. Settle toward the split where the lower tile is slightly compressed.
            double total = fLo + fHi;
            double down = total <= 1.0 ? fHi : StableLower(total) - fLo;
            if (down > 0.0 && mHi > 0.0)
            {
                // A last droplet falls all at once (overfilling by at most TinyFill, too little to push back up).
                if (fHi <= TinyFill) return -mHi;
                // Keep falling while there is room for all of it, however little; settle otherwise.
                if (down > VerticalEpsilon || down >= fHi) return -Math.Min(down * FallRate * full, FallCap * mHi);
            }
            if (down < -VerticalEpsilon && mLo > 0.0)
                return Math.Min(-down * FallRate * _viscosityFactor[l] * full, RiseCap * mLo);
            return 0.0;
        }

        double diff = fLo - fHi;
        if (Math.Abs(diff) <= SideEpsilon) return 0.0;
        int src = diff > 0.0 ? lo : hi;
        double mSrc = diff > 0.0 ? mLo : mHi;
        if (mSrc < _minSpreadKg[l] || !Supported(grid, src, l)) return 0.0;
        double kg = Math.Min(Math.Abs(diff) * SpreadRate * _viscosityFactor[l] * full, SideCap * mSrc);
        return diff > 0.0 ? kg : -kg;
    }

    /// <summary>
    /// How full the lower of two stacked tiles should be when they hold a total fill between
    /// them: everything up to one full tile, and beyond that, slightly more than the upper one.
    /// </summary>
    private static double StableLower(double total)
    {
        if (total <= 1.0) return 1.0;
        if (total < 2.0 + LiquidCompression) return (1.0 + total * LiquidCompression) / (1.0 + LiquidCompression);
        return (total + LiquidCompression) / 2.0;
    }

    /// <summary>True when liquid l in this tile has nowhere to fall, so it may spread.</summary>
    private bool Supported(WorldGrid grid, int index, ushort l)
    {
        int plane = grid.Width * grid.Height;
        if (index < plane) return true;
        int below = index - plane;
        ushort m = grid.Material[below];
        if (m == l) return grid.Mass[below] >= 0.999 * _fullMass[l];
        if (m == MaterialTable.Empty) return ComputeClaim(grid, below) != l;
        return true;
    }

    /// <summary>
    /// Which liquid may flow into a tile with an empty condensed slot: the first liquid found
    /// above, then on the sides (-x, +x, -y, +y), then below. One per tile, so liquids never
    /// meet inside a tile.
    /// </summary>
    private ushort ComputeClaim(WorldGrid grid, int i)
    {
        if (grid.Material[i] != MaterialTable.Empty) return MaterialTable.Empty;
        var (x, y, z) = grid.Coordinates(i);
        int width = grid.Width, plane = grid.Width * grid.Height;
        ushort m;
        if (z + 1 < grid.Depth && _isLiquid[m = grid.Material[i + plane]]) return m;
        if (x > 0 && _isLiquid[m = grid.Material[i - 1]]) return m;
        if (x + 1 < grid.Width && _isLiquid[m = grid.Material[i + 1]]) return m;
        if (y > 0 && _isLiquid[m = grid.Material[i - width]]) return m;
        if (y + 1 < grid.Height && _isLiquid[m = grid.Material[i + width]]) return m;
        if (z > 0 && _isLiquid[m = grid.Material[i - plane]]) return m;
        return MaterialTable.Empty;
    }

    private ushort ClaimAt(WorldGrid grid, int i, bool awake) => awake ? _claim[i] : ComputeClaim(grid, i);

    private Gas GasAt(WorldGrid grid, int i, bool awake) => awake ? _gas[i] : ComputeGas(grid, i);

    private Gas ComputeGas(WorldGrid grid, int i)
    {
        double total = 0.0, mol = 0.0;
        for (int s = 0; s < _gasMolPerKg.Length; s++)
        {
            double m = grid.GasMass[s][i];
            total += m;
            mol += m * _gasMolPerKg[s];
        }
        double free = grid.FreeVolume(i);
        double volume = Math.Max(free, WorldGrid.MinFreeVolumeM3);
        double t = grid.GasTemperatureAt(i);
        return new Gas
        {
            Free = free,
            Total = total,
            MolPerKg = total > 0.0 ? mol / total : 0.0,
            Pressure = mol * GasConstant * t / volume,
            Density = total / volume,
            Temperature = t,
        };
    }

    private void Apply(WorldGrid grid, int i)
    {
        double dl = _dLiquid[i], de = _dEnergy[i], dg = _dGasEnergy[i];
        bool gasChanged = false;
        for (int s = 0; s < _dGas.Length; s++)
            if (_dGas[s][i] != 0.0) { gasChanged = true; break; }
        if (dl == 0.0 && de == 0.0 && dg == 0.0 && !gasChanged) return;
        // Nothing has touched this tile yet this pass, so these are still its start-of-tick energies.
        double condensed = EnergyAudit.CondensedJoules(grid, i) + de;
        double gasJoules = EnergyAudit.GasJoules(grid, i) + dg;

        if (dl != 0.0)
        {
            ushort l = grid.Material[i] != MaterialTable.Empty ? grid.Material[i] : _claim[i];
            double mass = grid.Mass[i] + dl;
            if (mass <= EmptyResidueKg)
            {
                grid.SetCondensed(i, MaterialTable.Empty, 0.0);
                // The last drops' heat stays in the tile, with its gas.
                gasJoules += condensed;
                condensed = 0.0;
            }
            else
            {
                double latent = grid.LatentEnergy[i] + _dLatent[i];
                grid.Material[i] = l;
                grid.Mass[i] = mass;
                grid.LatentEnergy[i] = latent;
            }
        }
        for (int s = 0; s < _dGas.Length; s++)
        {
            double g = grid.GasMass[s][i] + _dGas[s][i];
            grid.GasMass[s][i] = g > 0.0 ? g : 0.0;
        }

        // Solve the temperatures that make the tile's energies match what it should now hold.
        EnergyAudit.SolveTemperatures(grid, i, condensed, gasJoules);
    }

    private void Prepare(WorldGrid grid, IReadOnlyList<(string Key, double Fraction)> sky)
    {
        if (_claim.Length != grid.Count)
        {
            _claim = new ushort[grid.Count];
            _gas = new Gas[grid.Count];
            _dLiquid = new double[grid.Count];
            _dEnergy = new double[grid.Count];
            _dGasEnergy = new double[grid.Count];
            _dLatent = new double[grid.Count];
            _dGas = new double[grid.Materials.GasCount][];
            for (int s = 0; s < _dGas.Length; s++) _dGas[s] = new double[grid.Count];
        }

        var table = grid.Materials;
        int plane = grid.Width * grid.Height;
        if (_skyStepEnergy.Length != plane || _skyStepGas.Length != table.GasCount)
        {
            _skyStepEnergy = new double[plane];
            _skyStepGas = new double[table.GasCount][];
            for (int s = 0; s < _skyStepGas.Length; s++) _skyStepGas[s] = new double[plane];
        }
        if (ReferenceEquals(_preparedFor, table) && _preparedDepth == grid.Depth && ReferenceEquals(_skyMix, sky)) return;
        _skyMix = sky;
        double[] offsets = EnergyAudit.OffsetsFor(table);
        _isLiquid = new bool[table.Count];
        _fullMass = new double[table.Count];
        _minSpreadKg = new double[table.Count];
        _viscosityFactor = new double[table.Count];
        _specificHeat = new double[table.Count];
        _offset = offsets;
        for (ushort id = 0; id < table.Count; id++)
        {
            var def = table[id];
            _isLiquid[id] = def.Phase == Phase.Liquid;
            _fullMass[id] = def.FullTileMass;
            _minSpreadKg[id] = MinSpreadDepthM * WorldGrid.TileFaceAreaM2 * def.Density;
            // Water flows freely; thicker liquids spread more slowly (molten iron about 0.4x, magma 0.003x).
            _viscosityFactor[id] = def.Viscosity > 0f ? Math.Min(1.0, Math.Sqrt(0.001 / def.Viscosity)) : 1.0;
            _specificHeat[id] = def.SpecificHeat;
        }
        _gasSpecificHeat = new double[table.GasCount];
        _gasOffset = new double[table.GasCount];
        _gasMolPerKg = new double[table.GasCount];
        for (int s = 0; s < table.GasCount; s++)
        {
            var def = table[table.GasIds[s]];
            _gasSpecificHeat[s] = def.SpecificHeat;
            _gasOffset[s] = offsets[table.GasIds[s]];
            _gasMolPerKg[s] = 1.0 / def.MolarMass;
        }
        if (SkyGasOutKg.Length != table.GasCount) SkyGasOutKg = new double[table.GasCount];
        {
            // The sky gas also sets what "still air" means for buoyancy, so it is needed even when sealed.
            _skyFraction = new double[table.GasCount];
            double share = 0.0;
            foreach (var (key, fraction) in _skyMix)
            {
                int species = table.GasIndexOf(table.Id(key));
                if (species < 0) throw new InvalidOperationException($"The sky must be gas, not '{key}'.");
                if (!(fraction >= 0.0)) throw new InvalidOperationException($"The sky can't hold {fraction} of '{key}'.");
                _skyFraction[species] += fraction;
                share += fraction;
            }
            if (!(share > 0.0)) throw new InvalidOperationException("The sky holds no gas.");
            _skyMolPerKg = 0.0;
            _skyEnergyPerKg = 0.0;
            for (int s = 0; s < table.GasCount; s++)
            {
                _skyFraction[s] /= share;
                _skyMolPerKg += _skyFraction[s] * _gasMolPerKg[s];
                _skyEnergyPerKg += _skyFraction[s] * (_gasSpecificHeat[s] * _skyTemperatureK + _gasOffset[s]);
            }
            _referenceDensityFactor = _skyMolPerKg * _skyTemperatureK;
            // The sky is the next level up of the same still air Atmosphere.Balance lays down.
            _skyPressure = Atmosphere.Profile(grid.Depth + 1, 1.0 / _skyMolPerKg, _skyTemperatureK, _groundPressurePa)[grid.Depth];
            _skyDensity = _skyPressure / (GasConstant * _skyMolPerKg * _skyTemperatureK);
        }
        _preparedFor = table;
        _preparedDepth = grid.Depth;
    }
}
