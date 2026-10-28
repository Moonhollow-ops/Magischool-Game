using System;
using Magischool.Sim.Core;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Combustion;

/// <summary>
/// Layer 5: fire.
///
/// <para>
/// A fuel (a solid or liquid with a heat of combustion, such as wood) burns
/// wherever it touches gas that holds oxygen and the surface between them is
/// at or above the fuel's ignition point. Nothing is scripted: a fire starts
/// because something made a surface hot enough, it spreads because burning
/// heats the gas, the gas heats neighbouring surfaces and the air currents
/// carry the heat away and upward, and it goes out when the fuel or the oxygen
/// runs out.
/// </para>
/// <para>
/// Each kilogram burned takes oxygen from the air next to it (air is 23%
/// oxygen; what's left of it stays behind as nitrogen) and turns into the
/// fuel's products, for wood carbon dioxide, steam and smoke, added to that same
/// gas. The fuel carries its chemical energy in the energy audit, so burning
/// conserves energy exactly: the gas tile's temperature is solved from the
/// energy it now holds, which is where the heat of combustion shows up.
/// </para>
/// <para>
/// A tile is half a metre across, far coarser than a flame, so each fuel face
/// keeps the temperature of a thin surface layer (<see cref="SkinDepthM"/>,
/// in <see cref="WorldGrid.Skin"/>). The layer warms by convection and
/// radiation from the gas beside it and, when a flame burns anywhere in that
/// gas, by the flame's own heat flux (<see cref="FlameHeatFluxWm2"/>, weakened
/// as the oxygen thins); it cools by conduction into the fuel behind it. The
/// heat it takes in comes out of the gas, so a fire warms the wood around it
/// at the cost of its own heat. A face burns while its surface is at or above
/// the ignition point, so a flame keeps its own face lit and spreads by
/// heating the faces around the same pocket of gas over the minutes real wood
/// takes. Which faces are alight is kept in <see cref="WorldGrid.Flame"/>.
/// Burning stops below <see cref="ExtinctionOxygenFraction"/>.
/// </para>
/// <para>
/// Not modelled yet: charring and smouldering, incomplete burning to carbon
/// monoxide, radiation between distant surfaces, and wet fuel.
/// </para>
/// </summary>
public sealed class CombustionSystem : ISimSystem
{
    /// <summary>Mass burned per square metre of exposed fuel per second, in full air (flaming wood, about 12 g/(m²·s)).</summary>
    public const double BurnRateKgPerM2S = 0.012;

    /// <summary>Heat a flame pours onto the surface feeding it in full air, W/m² (flaming wood, about 25 kW/m²).</summary>
    public const double FlameHeatFluxWm2 = 25_000.0;

    /// <summary>Thickness of the surface layer whose temperature decides ignition, m.</summary>
    public const double SkinDepthM = 0.01;

    /// <summary>Emissivity of fuel surfaces.</summary>
    public const double SurfaceEmissivity = 0.9;

    /// <summary>Convective film coefficient between gas and a fuel surface, W/(m²·K).</summary>
    public const double FilmCoefficient = 10.0;

    private const double StefanBoltzmann = 5.670374419e-8;

    /// <summary>A face within this of its tile and its gas, with no flame near, is left alone.</summary>
    private const double QuietK = 1.0;

    private const double ShareCap = 1.0 / 7.0;

    /// <summary>Oxygen mass fraction below which flames go out (about 13% by volume).</summary>
    public const double ExtinctionOxygenFraction = 0.145;

    /// <summary>Fuel left in a tile below this is the last crumbs; they go up as smoke.</summary>
    private const double FuelResidueKg = 1e-6;

    // Each fuel tile feeds at most 7 sites (itself and six neighbours), and each site feeds at
    // most 7 fuel tiles, so per-pair caps of 1/8 of the fuel and 1/14 of the oxygen keep every
    // tile from running out mid-tick.
    private const double FuelCapPerSite = 1.0 / 8.0;
    private const double OxygenCapPerFuel = 1.0 / 14.0;

    private MaterialTable? _preparedFor;
    private bool[] _isFuel = Array.Empty<bool>();
    private double[] _ignition = Array.Empty<double>();
    private double[] _heatOfCombustion = Array.Empty<double>();
    private double[] _oxygenPerKg = Array.Empty<double>();
    private double[] _airPerKg = Array.Empty<double>();
    private int[][] _productSpecies = Array.Empty<int[]>();
    private double[][] _productKg = Array.Empty<double[]>();
    private double[] _skinConductance = Array.Empty<double>();
    private double[] _skinCapacity = Array.Empty<double>();
    private double[] _specificHeat = Array.Empty<double>();
    private double[] _offset = Array.Empty<double>();
    private int _air, _remainder, _smoke = -1;
    private double _oxygenFraction;

    private double[] _dFuel = Array.Empty<double>();
    private double[] _dEnergyC = Array.Empty<double>(), _dEnergyG = Array.Empty<double>();
    private double[][] _dGas = Array.Empty<double[]>();
    private bool[] _changed = Array.Empty<bool>();
    private byte[] _flame = Array.Empty<byte>();
    private double[] _skin = Array.Empty<double>();

    public string Name => "combustion";

    /// <summary>Total fuel burned since this system was created, kg.</summary>
    public double BurnedKg { get; private set; }
    private double[] _burnedPerChunk = Array.Empty<double>();

    public void Update(Simulation sim)
    {
        var grid = sim.Grid;
        var activity = sim.Activity;
        Prepare(grid, activity);
        var chunks = activity.AwakeChunks();
        int gasCount = _dGas.Length;

        // Pass 1: every tile gathers the burning and the surface heating it takes part in, as fuel
        // and as the gas beside it. Each fuel/gas pair is computed identically from both sides from
        // the start-of-tick state, so no tile writes another and the result doesn't depend on thread count.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            Span<int> near = stackalloc int[7];
            Span<bool> nearAwake = stackalloc bool[7];
            Span<int> nearChunk = stackalloc int[7];
            double burned = 0.0;
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                int count = Neighbours(grid, activity, x, y, z, x0, x1, y0, y1, z0, z1, near, nearAwake, nearChunk);
                double fuelLoss = 0.0, energyC = 0.0, energyG = 0.0;
                bool changed = false;
                byte flame = 0;
                bool isFuel = _isFuel[grid.Material[i]];
                for (int face = 0; face < 6; face++) _skin[i * 6 + face] = isFuel ? grid.Skin[i * 6 + face] : 0.0;
                for (int s = 0; s < gasCount; s++) _dGas[s][i] = 0.0;

                for (int k = 0; k < count; k++)
                {
                    int j = near[k];
                    int site = SiteBit(grid, i, j);

                    // i as fuel, j as the gas it burns in and the gas against its surface.
                    double asFuel = Burn(grid, i, j);
                    double heatIn = 0.0, skinAfter = 0.0;
                    if (isFuel && j != i) heatIn = FaceHeat(grid, i, j, site - 1, out skinAfter);
                    if (asFuel > 0.0 || heatIn != 0.0)
                    {
                        if (!nearAwake[k])
                        {
                            activity.Stirred[nearChunk[k]] = true;
                        }
                        else
                        {
                            fuelLoss += asFuel;
                            burned += asFuel;
                            // Fuel burning in its own tile's gas hands its energy across inside the tile.
                            energyC += heatIn - FuelEnergy(grid, i, asFuel);
                            if (asFuel > 0.0) flame |= (byte)(1 << site);
                            changed = true;
                        }
                    }
                    if (isFuel && j != i && nearAwake[k]) _skin[i * 6 + site - 1] = skinAfter;

                    // i as the gas, j as the fuel burning in it and warmed by it.
                    double asGas = Burn(grid, j, i);
                    double heatOut = j != i && _isFuel[grid.Material[j]] ? FaceHeat(grid, j, i, SiteBit(grid, j, i) - 1, out _) : 0.0;
                    if (asGas <= 0.0 && heatOut == 0.0) continue;
                    if (!nearAwake[k])
                    {
                        activity.Stirred[nearChunk[k]] = true;
                        continue;
                    }
                    changed = true;
                    energyG += FuelEnergy(grid, j, asGas) - heatOut;
                    if (asGas <= 0.0) continue;
                    ushort fuel = grid.Material[j];
                    _dGas[_air][i] -= asGas * _airPerKg[fuel];
                    _dGas[_remainder][i] += asGas * (_airPerKg[fuel] - _oxygenPerKg[fuel]);
                    int[] species = _productSpecies[fuel];
                    double[] kg = _productKg[fuel];
                    for (int p = 0; p < species.Length; p++) _dGas[species[p]][i] += asGas * kg[p];
                }
                double interior = InteriorHeat(grid, i);
                if (interior > 0.0)
                {
                    energyC += interior;
                    energyG -= interior;
                    changed = true;
                }
                _dFuel[i] = fuelLoss;
                _dEnergyC[i] = energyC;
                _dEnergyG[i] = energyG;
                _changed[i] = changed;
                _flame[i] = flame;
                if (changed) activity.Stirred[chunk] = true;
            }
            _burnedPerChunk[chunk] = burned;
        });

        // Pass 2: apply, then solve each changed tile's temperature from the energy it should hold.
        ChunkLoop.Run(sim, chunks, chunk =>
        {
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(chunk);
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = grid.Index(x, y, z);
                grid.Flame[i] = _flame[i];
                for (int face = 0; face < 6; face++) grid.Skin[i * 6 + face] = _skin[i * 6 + face];
                if (!_changed[i]) continue;
                double condensed = EnergyAudit.CondensedJoules(grid, i) + _dEnergyC[i];
                double gas = EnergyAudit.GasJoules(grid, i) + _dEnergyG[i];
                double loss = _dFuel[i];
                if (loss > 0.0)
                {
                    double before = grid.Mass[i];
                    double mass = before - loss;
                    // The share of any half-finished melt or freeze goes with the mass that burned.
                    grid.LatentEnergy[i] *= mass / before;
                    if (mass <= FuelResidueKg)
                    {
                        if (_smoke >= 0) grid.GasMass[_smoke][i] += Math.Max(mass, 0.0);
                        grid.SetCondensed(i, MaterialTable.Empty, 0.0);
                        gas += condensed;
                        condensed = 0.0;
                        grid.Flame[i] = 0;
                        for (int face = 0; face < 6; face++) grid.Skin[i * 6 + face] = 0.0;
                    }
                    else grid.Mass[i] = mass;
                }
                for (int s = 0; s < _dGas.Length; s++)
                {
                    double g = grid.GasMass[s][i] + _dGas[s][i];
                    grid.GasMass[s][i] = g > 0.0 ? g : 0.0;
                }
                EnergyAudit.SolveTemperatures(grid, i, condensed, gas);
            }
        });

        foreach (int chunk in chunks) BurnedKg += _burnedPerChunk[chunk];
    }

    /// <summary>
    /// Kilograms of the fuel in tile <paramref name="f"/> burning this tick in the gas of tile
    /// <paramref name="g"/> (the same tile when gas has got into the fuel's own tile).
    /// </summary>
    private double Burn(WorldGrid grid, int f, int g)
    {
        ushort fuel = grid.Material[f];
        if (!_isFuel[fuel]) return 0.0;
        double fuelMass = grid.Mass[f];
        if (fuelMass <= 0.0) return 0.0;
        double strength = OxygenStrength(grid, g);
        if (strength <= 0.0) return 0.0;

        // A part-full fuel tile (a post, a heap) with gas of its own burns into that gas over all
        // of its surface; a full one burns on its faces into the gas beside them.
        double area;
        if (StandsInOwnGas(grid, f))
        {
            if (f != g) return 0.0;
            if (InteriorSurfaceTemperature(grid, f) < _ignition[fuel]) return 0.0;
            area = InteriorArea(grid, f);
        }
        else
        {
            if (f == g || SkinOf(grid, f, SiteBit(grid, f, g) - 1) < _ignition[fuel]) return 0.0;
            area = WorldGrid.TileFaceAreaM2;
        }

        double kg = BurnRateKgPerM2S * area * Simulation.SecondsPerTick * strength;
        kg = Math.Min(kg, FuelCapPerSite * fuelMass);
        kg = Math.Min(kg, OxygenCapPerFuel * grid.GasMass[_air][g] * _oxygenFraction / _oxygenPerKg[fuel]);
        return kg;
    }

    /// <summary>
    /// Heat the flame at one site of fuel tile <paramref name="f"/> gives off, W (site as in
    /// <see cref="WorldGrid.Flame"/>: 0 burning in the tile's own gas, 1 to 6 on its faces): the
    /// fuel's heat of combustion at the rate that site burns in the air it has now. Light and
    /// noise are read from it; the per-tick caps that keep a tile from running dry are left out.
    /// </summary>
    public static double FlamePowerW(WorldGrid grid, int f, int site)
    {
        var def = grid.Materials[grid.Material[f]];
        if (def.HeatOfCombustion <= 0f || (grid.Flame[f] & (1 << site)) == 0) return 0.0;
        int g = f;
        if (site > 0)
        {
            var (x, y, z) = grid.Coordinates(f);
            var (dx, dy, dz) = Faces[site - 1];
            if (!grid.InBounds(x + dx, y + dy, z + dz)) return 0.0;
            g = grid.Index(x + dx, y + dy, z + dz);
        }
        var oxidizer = EnergyAudit.OxidizerOf(grid.Materials);
        if (grid.FreeVolume(g) < FluidSystem.GasLockFraction * WorldGrid.TileVolumeM3) return 0.0;
        double air = grid.GasMass[grid.Materials.GasIndexOf(grid.Materials.Id(oxidizer.Key))][g];
        double total = grid.TotalGasMass(g);
        if (!(air > 0.0) || !(total > 0.0)) return 0.0;
        double strength = Math.Min(1.0, (air * oxidizer.OxygenFraction / total - ExtinctionOxygenFraction) / (oxidizer.OxygenFraction - ExtinctionOxygenFraction));
        if (!(strength > 0.0)) return 0.0;
        double area = site == 0 ? InteriorArea(grid, f) : WorldGrid.TileFaceAreaM2;
        return BurnRateKgPerM2S * area * strength * def.HeatOfCombustion;
    }

    /// <summary>
    /// How well the gas in tile g feeds a flame: 1 in fresh air, falling to 0 at
    /// <see cref="ExtinctionOxygenFraction"/>; 0 or less where nothing can burn.
    /// </summary>
    private double OxygenStrength(WorldGrid grid, int g)
    {
        if (grid.FreeVolume(g) < FluidSystem.GasLockFraction * WorldGrid.TileVolumeM3) return 0.0;
        double air = grid.GasMass[_air][g];
        if (air <= 0.0) return 0.0;
        double oxygenShare = air * _oxygenFraction / grid.TotalGasMass(g);
        double strength = (oxygenShare - ExtinctionOxygenFraction) / (_oxygenFraction - ExtinctionOxygenFraction);
        return Math.Min(1.0, strength);
    }

    /// <summary>Surface temperature of one face of fuel tile f.</summary>
    private static double SkinOf(WorldGrid grid, int f, int face)
    {
        double stored = grid.Skin[f * 6 + face];
        return stored > 0.0 ? stored : grid.Temperature[f];
    }

    /// <summary>
    /// Joules moved this tick from the gas in tile <paramref name="g"/> into the face of fuel
    /// tile <paramref name="f"/> toward it (negative when the surface is the hotter), and that
    /// face's surface temperature after the tick. The surface takes in the flame's flux when
    /// there is a flame in the gas, convection and radiation from the gas, and loses heat by
    /// conduction into the fuel behind it.
    /// </summary>
    private double FaceHeat(WorldGrid grid, int f, int g, int face, out double skinAfter)
    {
        ushort fuel = grid.Material[f];
        double stored = grid.Skin[f * 6 + face];
        skinAfter = stored;
        if (grid.FreeVolume(g) < FluidSystem.GasLockFraction * WorldGrid.TileVolumeM3) return 0.0;
        // A part-full tile meets the world through the gas inside it (see InteriorHeat).
        if (StandsInOwnGas(grid, f))
        {
            skinAfter = 0.0;
            return 0.0;
        }

        double inside = CoreTemperature(grid, f);
        double skin = stored > 0.0 ? stored : inside;
        double gas = grid.GasTemperatureAt(g);
        bool flame = FlameIn(grid, g);
        bool quiet = !flame && Math.Abs(gas - inside) < QuietK;
        if (quiet && Math.Abs(skin - inside) < QuietK)
        {
            skinAfter = 0.0;
            return 0.0;
        }

        const double dt = Simulation.SecondsPerTick;
        double area = WorldGrid.TileFaceAreaM2 * Exposed(grid, f);
        double capacity = grid.HasTwoTemperatures(g) ? grid.GasHeatCapacity(g) : grid.HeatCapacity(g);
        double skinCapacity = _skinCapacity[fuel];
        double s2 = skin * skin, g2 = gas * gas;
        double exchange = (FilmCoefficient * (gas - skin) + SurfaceEmissivity * StefanBoltzmann * (g2 * g2 - s2 * s2)) * area * dt;
        // Neither side gives up more than a seventh of what it holds above the other, so a gas
        // tile serving several surfaces can't overshoot.
        exchange = exchange > 0.0
            ? Math.Min(exchange, capacity * (gas - skin) * ShareCap)
            : Math.Max(exchange, -skinCapacity * area * (skin - gas) * ShareCap);
        double flameJoules = flame
            ? Math.Min(FlameHeatFluxWm2 * Math.Max(0.0, OxygenStrength(grid, g)) * area * dt, capacity * gas * ShareCap * 0.5)
            : 0.0;

        // Backward Euler on the layer, linearised, so it settles without ringing.
        double net = (exchange + flameJoules) / (area * dt) - _skinConductance[fuel] * (skin - inside);
        double stiffness = FilmCoefficient + 4.0 * SurfaceEmissivity * StefanBoltzmann * s2 * skin + _skinConductance[fuel];
        double after = skin + dt * net / (skinCapacity + dt * stiffness);
        skinAfter = quiet && Math.Abs(after - inside) < QuietK ? 0.0 : after;
        return exchange + flameJoules;
    }

    /// <summary>Heat capacity of the surface layer on one face of a solid tile, J/K.</summary>
    public static double SurfaceLayerCapacity(WorldGrid grid, int i)
    {
        var def = grid.Materials[grid.Material[i]];
        return def.Density * def.SpecificHeat * SkinDepthM * WorldGrid.TileFaceAreaM2 * Exposed(grid, i);
    }

    /// <summary>
    /// Temperature of a tile beneath the surface layers of its faces, K. The layers' heat is part of
    /// the tile's, so the core holds what is left: a beam scorched on the outside can still be
    /// cool and strong inside. Faces with no layer of their own count as core.
    /// </summary>
    public static double CoreTemperature(WorldGrid grid, int i)
    {
        double total = grid.HasTwoTemperatures(i) ? grid.CondensedHeatCapacity(i) : grid.HeatCapacity(i);
        if (total <= 0.0 || !grid.HasCondensed(i)) return grid.Temperature[i];
        double layer = SurfaceLayerCapacity(grid, i);
        double skinCapacity = 0.0, skinHeat = 0.0;
        for (int face = 0; face < 6; face++)
        {
            double skin = grid.Skin[i * 6 + face];
            if (skin <= 0.0) continue;
            skinCapacity += layer;
            skinHeat += layer * skin;
        }
        if (skinCapacity == 0.0) return grid.Temperature[i];
        // A tile so thin it is nearly all surface has no core to speak of.
        if (skinCapacity > 0.9 * total)
        {
            double scale = 0.9 * total / skinCapacity;
            skinCapacity *= scale;
            skinHeat *= scale;
        }
        return Math.Max(0.0, (total * grid.Temperature[i] - skinHeat) / (total - skinCapacity));
    }

    /// <summary>
    /// Share of a face's area that a part-full fuel tile (a post, a plank) actually presents: as
    /// if its fuel were a cube, the area goes as the fill to the power 2/3.
    /// </summary>
    private static double Exposed(WorldGrid grid, int f) => Math.Pow(Math.Min(1.0, grid.FillFraction(f)), 2.0 / 3.0);

    /// <summary>
    /// Whether a fuel tile is mostly gas: a post or a heap standing in the air of its own tile,
    /// which it burns into and is heated by. A fuller tile (a wall, a log) burns on its faces into
    /// the gas of the tiles beside it, even when a little gas has got in.
    /// </summary>
    private static bool StandsInOwnGas(WorldGrid grid, int i) =>
        grid.FreeVolume(i) >= OwnGasFraction * WorldGrid.TileVolumeM3;

    /// <summary>Free share of a tile above which its fuel burns into the tile's own gas.</summary>
    private const double OwnGasFraction = 0.5;

    /// <summary>Surface a part-full fuel tile presents to the gas inside it, m²: that of a cube of its volume.</summary>
    private static double InteriorArea(WorldGrid grid, int f) => 6.0 * WorldGrid.TileFaceAreaM2 * Exposed(grid, f);

    /// <summary>
    /// The surface temperature of a part-full fuel tile, K: where the flame's flux (if a flame
    /// burns in the tile's gas), convection and radiation from that gas balance conduction into
    /// the fuel's core. The surface layer is thin enough to settle within a tick.
    /// </summary>
    private double InteriorSurfaceTemperature(WorldGrid grid, int f)
    {
        double gas = grid.GasTemperatureAt(f), core = grid.Temperature[f];
        double skin = _skinConductance[grid.Material[f]];
        double flux = FlameIn(grid, f) ? FlameHeatFluxWm2 * Math.Max(0.0, OxygenStrength(grid, f)) : 0.0;
        double Balance(double s)
        {
            double g2 = gas * gas, s2 = s * s;
            return flux + FilmCoefficient * (gas - s) + SurfaceEmissivity * StefanBoltzmann * (g2 * g2 - s2 * s2) - skin * (s - core);
        }
        double lo = Math.Min(gas, core), hi = Math.Max(gas, core) + 3000.0;
        for (int n = 0; n < 40; n++)
        {
            double mid = 0.5 * (lo + hi);
            if (Balance(mid) >= 0.0) lo = mid;
            else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// Joules the flame burning in a part-full fuel tile's own gas pours onto the fuel this
    /// tick, paid by that gas; convection and radiation between the two are the thermal
    /// layer's (see <see cref="ThermalSystem.InteriorExchange"/>).
    /// </summary>
    private double InteriorHeat(WorldGrid grid, int f)
    {
        if (!_isFuel[grid.Material[f]] || !StandsInOwnGas(grid, f) || !grid.HasTwoTemperatures(f) || !FlameIn(grid, f)) return 0.0;
        double joules = FlameHeatFluxWm2 * Math.Max(0.0, OxygenStrength(grid, f)) * InteriorArea(grid, f) * Simulation.SecondsPerTick;
        return Math.Min(joules, grid.GasHeatCapacity(f) * grid.GasTemperature[f] * ShareCap * 0.5);
    }

    /// <summary>Whether any fuel face around gas tile g, or gas inside g's own fuel, is alight.</summary>
    private static bool FlameIn(WorldGrid grid, int g)
    {
        if ((grid.Flame[g] & 1) != 0) return true;
        int plane = grid.Width * grid.Height;
        int z = g / plane, y = g % plane / grid.Width, x = g % grid.Width;
        for (int bit = 1; bit <= 6; bit++)
        {
            var (dx, dy, dz) = Faces[bit - 1];
            int nx = x + dx, ny = y + dy, nz = z + dz;
            if (!grid.InBounds(nx, ny, nz)) continue;
            // The neighbour's face toward g is the opposite one: bits pair up as 1/2, 3/4, 5/6.
            int back = bit % 2 == 1 ? bit + 1 : bit - 1;
            if ((grid.Flame[grid.Index(nx, ny, nz)] & (1 << back)) != 0) return true;
        }
        return false;
    }

    /// <summary>Which bit of <see cref="WorldGrid.Flame"/> stands for the face of f toward g (0 for f itself).</summary>
    private static int SiteBit(WorldGrid grid, int f, int g)
    {
        int d = g - f;
        if (d == 0) return 0;
        if (d == -1) return 1;
        if (d == 1) return 2;
        if (d == -grid.Width) return 3;
        if (d == grid.Width) return 4;
        return d < 0 ? 5 : 6;
    }

    /// <summary>Energy, as the audit counts it (chemical energy included), in kg of the fuel in tile f.</summary>
    private double FuelEnergy(WorldGrid grid, int f, double kg)
    {
        ushort fuel = grid.Material[f];
        double share = grid.LatentEnergy[f] * (kg / grid.Mass[f]);
        return kg * (_specificHeat[fuel] * grid.Temperature[f] + _offset[fuel]) + share;
    }

    private static readonly (int Dx, int Dy, int Dz)[] Faces =
        { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1) };

    /// <summary>The tile itself and its in-bounds face neighbours, whether each is awake, and its chunk.</summary>
    private static int Neighbours(WorldGrid grid, ActivityMap activity, int x, int y, int z,
        int x0, int x1, int y0, int y1, int z0, int z1, Span<int> near, Span<bool> awake, Span<int> chunkOf)
    {
        int n = 0;
        near[n] = grid.Index(x, y, z);
        chunkOf[n] = activity.ChunkAt(x, y, z);
        awake[n++] = true;
        foreach (var (dx, dy, dz) in Faces)
        {
            int nx = x + dx, ny = y + dy, nz = z + dz;
            if (!grid.InBounds(nx, ny, nz)) continue;
            near[n] = grid.Index(nx, ny, nz);
            chunkOf[n] = activity.ChunkAt(nx, ny, nz);
            awake[n++] = (nx >= x0 && nx < x1 && ny >= y0 && ny < y1 && nz >= z0 && nz < z1) || activity.IsAwakeAt(nx, ny, nz);
        }
        return n;
    }

    private void Prepare(WorldGrid grid, ActivityMap activity)
    {
        if (_dFuel.Length != grid.Count)
        {
            _dFuel = new double[grid.Count];
            _dEnergyC = new double[grid.Count];
            _dEnergyG = new double[grid.Count];
            _changed = new bool[grid.Count];
            _flame = new byte[grid.Count];
            _skin = new double[grid.Count * 6];
            _dGas = new double[grid.Materials.GasCount][];
            for (int s = 0; s < _dGas.Length; s++) _dGas[s] = new double[grid.Count];
        }
        if (_burnedPerChunk.Length != activity.Count) _burnedPerChunk = new double[activity.Count];

        var table = grid.Materials;
        if (ReferenceEquals(_preparedFor, table)) return;
        _offset = EnergyAudit.OffsetsFor(table);
        _isFuel = new bool[table.Count];
        _ignition = new double[table.Count];
        _heatOfCombustion = new double[table.Count];
        _oxygenPerKg = new double[table.Count];
        _airPerKg = new double[table.Count];
        _productSpecies = new int[table.Count][];
        _productKg = new double[table.Count][];
        _skinConductance = new double[table.Count];
        _skinCapacity = new double[table.Count];
        _specificHeat = new double[table.Count];

        bool anyFuel = false;
        foreach (var def in table.All) anyFuel |= def.HeatOfCombustion > 0f;
        MaterialDef? oxidizer = anyFuel ? EnergyAudit.OxidizerOf(table) : null;
        if (oxidizer != null)
        {
            _air = table.GasIndexOf(table.Id(oxidizer.Key));
            _remainder = table.GasIndexOf(table.Id(oxidizer.OxygenRemainder!));
            _oxygenFraction = oxidizer.OxygenFraction;
        }
        _smoke = table.TryGetId(BuiltInMaterials.Smoke, out var smokeId) ? table.GasIndexOf(smokeId) : -1;

        for (ushort id = 0; id < table.Count; id++)
        {
            var def = table[id];
            _specificHeat[id] = def.SpecificHeat;
            _skinConductance[id] = def.ThermalConductivity / SkinDepthM;
            _skinCapacity[id] = def.Density * def.SpecificHeat * SkinDepthM;
            _productSpecies[id] = Array.Empty<int>();
            _productKg[id] = Array.Empty<double>();
            if (def.HeatOfCombustion <= 0f || oxidizer == null) continue;
            _isFuel[id] = true;
            _ignition[id] = def.IgnitionPoint;
            _heatOfCombustion[id] = def.HeatOfCombustion;
            _oxygenPerKg[id] = def.OxygenPerKg;
            _airPerKg[id] = def.OxygenPerKg / (double)oxidizer.OxygenFraction;
            var yields = EnergyAudit.CombustionYields(def);
            _productSpecies[id] = new int[yields.Length];
            _productKg[id] = new double[yields.Length];
            for (int p = 0; p < yields.Length; p++)
            {
                _productSpecies[id][p] = table.GasIndexOf(table.Id(yields[p].Key));
                _productKg[id][p] = yields[p].Kg;
            }
        }
        _preparedFor = table;
    }
}
