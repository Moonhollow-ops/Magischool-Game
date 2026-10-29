using System;
using System.Collections.Generic;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Perception;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Ecology;

/// <summary>
/// Layer 11: life's slow work. Water in the soil, plants growing on it, and dead things rotting.
/// All of it is slow (hours to months), so it is worked out once every <see cref="StrideTicks"/>
/// ticks for the time since, over the whole world, awake or asleep.
///
/// <para>
/// <b>Soil water.</b> Porous solids (soil) hold water in their pores (<see cref="WorldGrid.PoreWater"/>),
/// which keeps its own heat and is brought to the soil's temperature each step, freezing and
/// thawing with its latent heat. It moves by Darcy's law through the soil's water potential and
/// gravity, with Campbell's curves fitted to the material's field capacity and wilting point: wet
/// soil drains to field capacity in a day or two and then barely at all, dry soil draws water
/// sideways and up. Water lying on or beside soil soaks into it. Pore water left in a tile that is
/// no longer soil runs out as liquid. Wet soil open to the air gives off vapour as fast as the
/// light falling on it can pay the latent heat (Priestley–Taylor), slowing as it dries.
/// </para>
/// <para>
/// <b>Plants.</b> A plant is a material (grass) in a tile over soil, its roots in the two tiles
/// below. Its leaves (8 m² per dry kg) catch the light falling on them by Beer's law and fix
/// carbon dioxide from the air of their tile into carbohydrate, at 0.05 molecules per photon in
/// dim light and up to 30 µmol/(m²·s) in full sun, less in cold, heat, drought and thin carbon
/// dioxide; the oxygen made goes back into the air (with the nitrogen that carbon dioxide came
/// with, which is what makes it air again). The water it takes is drawn from the roots' soil, and
/// its leaves give off vapour in light as wet soil does. A quarter of what it fixes is respired
/// at once to build the rest, and its living tissue respires 1.5% of itself a day at 20 °C,
/// doubling every 10 °C: respiration is its own burning chemistry, slow, and warms the tile. So a
/// meadow grows in light until its own shade and upkeep stop it, shrinks in the dark, and draws
/// down the carbon dioxide of a closed room. An established plant puts out a seedling into open
/// ground beside it about once a week. Drought, frost and heat raise its stress; past it the
/// plant dies into its dead material, and its excess water goes into the soil.
/// </para>
/// <para>
/// <b>Decay.</b> Dead organic matter (dead grass, bread) rots by its burning chemistry at its
/// material's rate, doubling every 10 °C and stopping when frozen or above 60 °C, as fast as it
/// is damp (its own water, the soil under it, or humid air) and has oxygen. Dead bodies rot too:
/// their soft tissue liquefies and seeps out over weeks and their fat goes to carbon dioxide and
/// water, until little but bone is left.
/// </para>
/// <para>
/// Light carries no energy in the world (layer 10), so what plants fix and the latent heat that
/// leaves and soil spend come from outside it, and are counted in <see cref="LightJ"/>.
/// Everything else is the world's own energy and mass, exactly. Not yet: other plants than
/// grass, seeds and seasons, nutrients, roots as their own tissue, and water flowing through a
/// tile with a plant in it (a tile holds one solid or liquid, so a flood soaks into the soil at a
/// meadow's edge instead).
/// </para>
/// </summary>
public sealed class EcologySystem : ISimSystem
{
    public string Name => "ecology";

    /// <summary>Ticks between ecology steps: ten seconds.</summary>
    public const int StrideTicks = 100;
    public const double StepSeconds = StrideTicks * (double)Simulation.SecondsPerTick;

    /// <summary>
    /// Energy that has come in from light since this system was created, J: what plants have fixed
    /// and the latent heat that leaves and soil have spent giving off vapour.
    /// </summary>
    public double LightJ { get; private set; }

    // ---------------------------------------------------------------- constants

    /// <summary>Suction of soil water at field capacity and at the wilting point, m of water (33 kPa and 1.5 MPa).</summary>
    public const double FieldCapacitySuctionM = 3.3, WiltingSuctionM = 150.0;

    public const double WaterDensity = 1000.0;

    /// <summary>Luminous efficacy of daylight over all its wavelengths, lm/W.</summary>
    public const double DaylightLumensPerWatt = 110.0;

    /// <summary>Photosynthetic photons per lux of daylight, µmol/(m²·s): full sun's 100,000 lux is about 1,850.</summary>
    public const double PhotonsPerLux = 0.0185;

    /// <summary>Priestley–Taylor: evaporation from wet ground is 1.26 times the equilibrium rate, on half the shortwave light as net radiation.</summary>
    public const double PriestleyTaylor = 1.26, NetRadiationShare = 0.5, PsychrometricPaPerK = 66.0;

    /// <summary>Latent heat of evaporation used to size the rate, J/kg; the energy itself is counted exactly.</summary>
    public const double LatentSizingJPerKg = 2.45e6;

    /// <summary>Leaf area per kg of dry shoot, m², and how fast a canopy darkens with leaf area (Beer's law).</summary>
    public const double SpecificLeafArea = 8.0, CanopyExtinction = 0.5;

    /// <summary>Canopy photosynthesis: the most a grass sward fixes in full sun, µmol CO₂/(m²·s), and its yield per photon absorbed in dim light.</summary>
    public const double MaxPhotosynthesis = 30.0, QuantumYield = 0.05;

    /// <summary>Carbon dioxide where photosynthesis just balances light respiration, and its half-saturation, ppm.</summary>
    public const double CompensationPpm = 45.0, HalfSaturationPpm = 300.0;

    /// <summary>Photosynthesis stops at 0 °C and 45 °C and is best at 22 °C.</summary>
    public const double ColdestK = 273.15, BestK = 295.15, HottestK = 318.15;

    /// <summary>Share of what is fixed respired to build the rest, and living tissue's upkeep, per second of its dry mass at 20 °C.</summary>
    public const double GrowthRespiration = 0.25, UpkeepPerSecond = 0.015 / 86400.0;

    /// <summary>Dry mass of a seedling, of a plant established enough to spread, and below which a plant is dead, kg.</summary>
    public const double SeedlingDryKg = 0.002, EstablishedDryKg = 0.05, SmallestDryKg = 2e-4;

    /// <summary>How often an established, unstressed plant puts out a seedling, s.</summary>
    public const double SpreadSeconds = 7 * 86400.0;

    /// <summary>Time to die of drought (dry soil), of hard frost (below <see cref="FrostK"/>), and of heat (above <see cref="HottestK"/>), s; and to recover.</summary>
    public const double DroughtSeconds = 14 * 86400.0, FrostSeconds = 6 * 3600.0, HeatSeconds = 3600.0, RecoverySeconds = 7 * 86400.0;
    public const double FrostK = 268.15;

    /// <summary>Soft tissue of the dead rots at this rate at 20 °C, per second: half gone in a fortnight.</summary>
    public const double RemainsDecayPerSecond = 5e-7;

    /// <summary>What is left of each soft tissue when it has rotted away, of what it was made with.</summary>
    public const double RemainsFloor = 0.02;

    // ---------------------------------------------------------------- state for one step

    private Physiology.World _w = null!;
    private ushort _ice;
    private double[] _offsets = Array.Empty<double>();
    private double _waterC, _iceC, _waterO, _iceO;
    private double[] _dKg = Array.Empty<double>(), _dJ = Array.Empty<double>();
    private IReadOnlyList<LightSource> _sources = Array.Empty<LightSource>();

    public void Update(Simulation sim)
    {
        if (sim.Tick % StrideTicks != 0) return;
        Prepare(sim);
        var grid = sim.Grid;
        _sources = LightField.AllSources(sim);
        const double dt = StepSeconds;
        Equalize(grid);
        Drain(grid, dt);
        Soak(sim, dt);
        Loosen(sim);
        Breathe(sim, dt);
        Rot(sim, dt);
        RotRemains(sim, dt);
    }

    private void Prepare(Simulation sim)
    {
        var table = sim.Materials;
        _w = Physiology.For(table);
        _ice = table.Id(BuiltInMaterials.Ice);
        _offsets = EnergyAudit.OffsetsFor(table);
        _waterC = table[_w.Water].SpecificHeat;
        _iceC = table[_ice].SpecificHeat;
        _waterO = _offsets[_w.Water];
        _iceO = _offsets[_ice];
        if (_dKg.Length != sim.Grid.Count)
        {
            _dKg = new double[sim.Grid.Count];
            _dJ = new double[sim.Grid.Count];
        }
    }

    private static void Stir(Simulation sim, int i)
    {
        var (x, y, z) = sim.Grid.Coordinates(i);
        sim.Activity.Awake[sim.Activity.ChunkAt(x, y, z)] = true;
    }

    // ---------------------------------------------------------------- soil water

    /// <summary>Volume of the soil in a tile, m³: its solid as packed, or none if it holds no water.</summary>
    public static double SoilVolume(WorldGrid grid, int i)
    {
        if (!grid.HasCondensed(i)) return 0.0;
        var def = grid.Materials[grid.Material[i]];
        return def.Porosity > 0f && def.Phase == Phase.Solid ? Math.Min(grid.CondensedVolume(i), WorldGrid.TileVolumeM3) : 0.0;
    }

    /// <summary>Most water a tile's pores hold, kg.</summary>
    public static double PoreCapacityKg(WorldGrid grid, int i)
    {
        double v = SoilVolume(grid, i);
        return v > 0.0 ? grid.Materials[grid.Material[i]].Porosity * v * WaterDensity : 0.0;
    }

    /// <summary>Water in a tile's soil by volume, 0 to its porosity (0 where there is no soil).</summary>
    public static double Moisture(WorldGrid grid, int i)
    {
        double v = SoilVolume(grid, i);
        return v > 0.0 ? grid.PoreWater[i] / (WaterDensity * v) : 0.0;
    }

    /// <summary>The temperature of a tile's pore water, K, and the share of it frozen.</summary>
    public (double Kelvin, double Frozen) PoreState(WorldGrid grid, int i)
    {
        if (_offsets.Length == 0)
        {
            var table = grid.Materials;
            _offsets = EnergyAudit.OffsetsFor(table);
            ushort water = table.Id(BuiltInMaterials.Water), ice = table.Id(BuiltInMaterials.Ice);
            (_waterC, _iceC, _waterO, _iceO) = (table[water].SpecificHeat, table[ice].SpecificHeat, _offsets[water], _offsets[ice]);
        }
        return State(grid.PoreWater[i], grid.PoreWaterJ[i]);
    }

    private (double Kelvin, double Frozen) State(double kg, double joules)
    {
        if (!(kg > 0.0)) return (0.0, 0.0);
        double per = joules / kg;
        double liquid0 = _waterC * PhaseMeltK + _waterO, solid0 = _iceC * PhaseMeltK + _iceO;
        if (per >= liquid0) return ((per - _waterO) / _waterC, 0.0);
        if (per <= solid0) return ((per - _iceO) / _iceC, 1.0);
        return (PhaseMeltK, (liquid0 - per) / (liquid0 - solid0));
    }

    private const double PhaseMeltK = 273.15;

    /// <summary>Energy a kg of liquid water holds at a temperature, J.</summary>
    private double WaterJ(double kelvin) => _waterC * kelvin + _waterO;

    /// <summary>Brings each tile's pore water to its soil's temperature, freezing or thawing it, keeping their energy together.</summary>
    private void Equalize(WorldGrid grid)
    {
        for (int i = 0; i < grid.Count; i++)
        {
            double kg = grid.PoreWater[i];
            if (!(kg > 0.0) || SoilVolume(grid, i) <= 0.0) continue;
            var (t, frozen) = State(kg, grid.PoreWaterJ[i]);
            double soilT = grid.Temperature[i];
            bool phaseOk = soilT > PhaseMeltK ? frozen == 0.0 : soilT < PhaseMeltK ? frozen == 1.0 : true;
            if (Math.Abs(t - soilT) < 1e-6 && phaseOk) continue;
            double c = grid.CondensedHeatCapacity(i), ec = EnergyAudit.CondensedJoules(grid, i);
            double fixedJ = ec - c * soilT, total = ec + grid.PoreWaterJ[i];
            double pore;
            double liquidT = (total - fixedJ - kg * _waterO) / (c + kg * _waterC);
            if (liquidT >= PhaseMeltK) pore = kg * (_waterC * liquidT + _waterO);
            else
            {
                double solidT = (total - fixedJ - kg * _iceO) / (c + kg * _iceC);
                pore = solidT <= PhaseMeltK ? kg * (_iceC * solidT + _iceO) : total - fixedJ - c * PhaseMeltK;
            }
            grid.PoreWaterJ[i] = pore;
            EnergyAudit.SolveTemperatures(grid, i, total - pore, EnergyAudit.GasJoules(grid, i));
        }
    }

    /// <summary>Campbell's exponent b and air-entry suction, m, fitted to a material's field capacity and wilting point.</summary>
    private static (double B, double AirEntryM) Campbell(MaterialDef def)
    {
        double b = Math.Log(WiltingSuctionM / FieldCapacitySuctionM) / Math.Log(def.FieldCapacity / def.WiltingPoint);
        return (b, FieldCapacitySuctionM * Math.Pow(def.FieldCapacity / def.Porosity, b));
    }

    /// <summary>Water potential of liquid pore water at volumetric content θ, m (negative: suction).</summary>
    public static double Potential(MaterialDef def, double theta)
    {
        var (b, entry) = Campbell(def);
        double s = Math.Clamp(theta / def.Porosity, 0.01, 1.0);
        return -entry * Math.Pow(s, -b);
    }

    /// <summary>Hydraulic conductivity at volumetric content θ, m/s.</summary>
    public static double Conductivity(MaterialDef def, double theta)
    {
        var (b, _) = Campbell(def);
        double s = Math.Clamp(theta / def.Porosity, 0.0, 1.0);
        return def.SaturatedConductivity * Math.Pow(s, 2.0 * b + 3.0);
    }

    /// <summary>Liquid water in a tile's pores, kg, and its volumetric content.</summary>
    private (double Kg, double Theta) Liquid(WorldGrid grid, int i)
    {
        double kg = grid.PoreWater[i];
        if (!(kg > 0.0)) return (0.0, 0.0);
        var (_, frozen) = State(kg, grid.PoreWaterJ[i]);
        double liquid = kg * (1.0 - frozen);
        return (liquid, liquid / (WaterDensity * SoilVolume(grid, i)));
    }

    /// <summary>Water flowing between neighbouring soil tiles, by Darcy's law, from the start-of-step state.</summary>
    private void Drain(WorldGrid grid, double dt)
    {
        Array.Clear(_dKg);
        Array.Clear(_dJ);
        bool any = false;
        int plane = grid.Width * grid.Height;
        for (int i = 0; i < grid.Count; i++)
        {
            if (SoilVolume(grid, i) <= 0.0) continue;
            var (x, y, z) = grid.Coordinates(i);
            if (x + 1 < grid.Width) any |= Flow(grid, i, i + 1, 0.0, dt);
            if (y + 1 < grid.Height) any |= Flow(grid, i, i + grid.Width, 0.0, dt);
            // Toward the tile above, gravity pulls back down by a tile's height.
            if (z + 1 < grid.Depth) any |= Flow(grid, i, i + plane, WorldGrid.TileSizeM, dt);
        }
        if (!any) return;
        for (int i = 0; i < grid.Count; i++)
        {
            if (_dKg[i] == 0.0) continue;
            grid.PoreWater[i] += _dKg[i];
            grid.PoreWaterJ[i] += _dJ[i];
            if (grid.PoreWater[i] <= 1e-12) { grid.PoreWater[i] = 0.0; grid.PoreWaterJ[i] = 0.0; }
        }
    }

    /// <summary>Water from tile a to tile b (b above a by <paramref name="rise"/> m, or beside it), kg this step. Returns whether any moved.</summary>
    private bool Flow(WorldGrid grid, int a, int b, double rise, double dt)
    {
        if (SoilVolume(grid, b) <= 0.0) return false;
        var defA = grid.Materials[grid.Material[a]];
        var defB = grid.Materials[grid.Material[b]];
        var (kgA, thetaA) = Liquid(grid, a);
        var (kgB, thetaB) = Liquid(grid, b);
        if (!(kgA > 0.0) && !(kgB > 0.0)) return false;
        double headA = thetaA > 0.0 ? Potential(defA, thetaA) : -1e6, headB = (thetaB > 0.0 ? Potential(defB, thetaB) : -1e6) + rise;
        double k = 0.5 * (Conductivity(defA, thetaA) + Conductivity(defB, thetaB));
        double q = k * (headA - headB) / WorldGrid.TileSizeM; // m/s from a to b
        double kg = q * WorldGrid.TileFaceAreaM2 * WaterDensity * dt;
        if (kg == 0.0) return false;
        int from = kg > 0.0 ? a : b, to = kg > 0.0 ? b : a;
        double donor = kg > 0.0 ? kgA : kgB;
        double room = Math.Max(0.0, PoreCapacityKg(grid, to) - grid.PoreWater[to]);
        double moved = Math.Min(Math.Abs(kg), Math.Min(donor, room) / 7.0);
        if (!(moved > 1e-15)) return false;
        var (t, _) = State(grid.PoreWater[from], grid.PoreWaterJ[from]);
        double j = moved * WaterJ(Math.Max(t, PhaseMeltK));
        _dKg[from] -= moved; _dJ[from] -= j;
        _dKg[to] += moved; _dJ[to] += j;
        return true;
    }

    /// <summary>Water lying on soil, or beside it, soaks into its pores.</summary>
    private void Soak(Simulation sim, double dt)
    {
        var grid = sim.Grid;
        int plane = grid.Width * grid.Height;
        for (int i = 0; i < grid.Count; i++)
        {
            if (SoilVolume(grid, i) <= 0.0) continue;
            var (x, y, z) = grid.Coordinates(i);
            var def = grid.Materials[grid.Material[i]];
            if (z + 1 < grid.Depth) SoakFrom(sim, i, i + plane, def, dt, gravity: true);
            if (x > 0) SoakFrom(sim, i, i - 1, def, dt, gravity: false);
            if (x + 1 < grid.Width) SoakFrom(sim, i, i + 1, def, dt, gravity: false);
            if (y > 0) SoakFrom(sim, i, i - grid.Width, def, dt, gravity: false);
            if (y + 1 < grid.Height) SoakFrom(sim, i, i + grid.Width, def, dt, gravity: false);
        }
    }

    private void SoakFrom(Simulation sim, int soil, int pond, MaterialDef def, double dt, bool gravity)
    {
        var grid = sim.Grid;
        if (grid.Material[pond] != _w.Water || !(grid.Mass[pond] > 0.0)) return;
        var (_, theta) = Liquid(grid, soil);
        double depth = Math.Min(1.0, grid.FillFraction(pond)) * WorldGrid.TileSizeM;
        // Into the wetting front: the pond's depth and the soil's suction pull it in, over half a tile.
        double drive = (gravity ? depth + 0.5 * WorldGrid.TileSizeM : 0.5 * depth) - Potential(def, Math.Max(theta, 1e-3));
        double q = def.SaturatedConductivity * drive / (0.5 * WorldGrid.TileSizeM);
        double room = Math.Max(0.0, PoreCapacityKg(grid, soil) - grid.PoreWater[soil]);
        double kg = Math.Min(q * WorldGrid.TileFaceAreaM2 * WaterDensity * dt, Math.Min(0.2 * grid.Mass[pond], 0.5 * room));
        if (!(kg > 1e-12)) return;
        double j = EatFood.Take(grid, pond, ref kg);
        grid.PoreWater[soil] += kg;
        grid.PoreWaterJ[soil] += j;
        Stir(sim, pond);
    }

    /// <summary>Pore water in a tile that is no longer soil runs out into it as liquid, if there is room.</summary>
    private void Loosen(Simulation sim)
    {
        var grid = sim.Grid;
        for (int i = 0; i < grid.Count; i++)
        {
            double kg = grid.PoreWater[i];
            if (!(kg > 0.0) || SoilVolume(grid, i) > 0.0) continue;
            if (grid.HasCondensed(i) && grid.Material[i] != _w.Water) continue;
            double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
            if (!grid.HasCondensed(i))
            {
                grid.SetCondensed(i, _w.Water, kg);
                grid.Temperature[i] = grid.GasTemperatureAt(i);
            }
            else grid.Mass[i] += kg;
            EnergyAudit.SolveTemperatures(grid, i, condensed + grid.PoreWaterJ[i], gas);
            grid.PoreWater[i] = 0.0;
            grid.PoreWaterJ[i] = 0.0;
            Stir(sim, i);
        }
    }

    /// <summary>Takes up to <paramref name="kg"/> of liquid water from a tile's pores, down to <paramref name="floorTheta"/>; returns kg and J taken.</summary>
    private (double Kg, double J) TakePore(WorldGrid grid, int i, double kg, double floorTheta)
    {
        var (liquid, theta) = Liquid(grid, i);
        if (!(liquid > 0.0) || !(kg > 0.0)) return (0.0, 0.0);
        double spare = (theta - floorTheta) * WaterDensity * SoilVolume(grid, i);
        double take = Math.Min(kg, Math.Min(spare, liquid));
        if (!(take > 0.0)) return (0.0, 0.0);
        var (t, _) = State(grid.PoreWater[i], grid.PoreWaterJ[i]);
        double j = take * WaterJ(Math.Max(t, PhaseMeltK));
        grid.PoreWater[i] -= take;
        grid.PoreWaterJ[i] -= j;
        return (take, j);
    }

    // ---------------------------------------------------------------- light and vapour

    /// <summary>Equilibrium evaporation driven by the light falling on a surface, kg/(m²·s) (Priestley–Taylor).</summary>
    public static double EvaporationKgPerM2S(double lux, double airK)
    {
        if (!(lux > 0.0)) return 0.0;
        double net = NetRadiationShare * lux / DaylightLumensPerWatt;
        double slope = (Vapour.SaturationPressurePa(airK + 0.5) - Vapour.SaturationPressurePa(airK - 0.5));
        return PriestleyTaylor * slope / (slope + PsychrometricPaPerK) * net / LatentSizingJPerKg;
    }

    /// <summary>
    /// Gives off <paramref name="kg"/> of water already taken (holding <paramref name="joules"/>) as
    /// vapour into tile i's gas at its temperature, no more than the gas can hold; what it can't
    /// hold is returned to the caller (kg and J). The latent heat comes from light.
    /// </summary>
    private (double Kg, double J) Vent(Simulation sim, int i, double kg, double joules)
    {
        var grid = sim.Grid;
        double room = Vapour.SaturationKg(Math.Max(grid.FreeVolume(i), WorldGrid.MinFreeVolumeM3), grid.GasTemperatureAt(i)) - grid.GasMass[_w.SteamSpecies][i];
        double vented = Math.Min(kg, Math.Max(0.0, room));
        if (!(vented > 0.0)) return (kg, joules);
        double ventJ = joules * vented / kg;
        double steamJ = vented * _w.GasJPerKg(_w.Steam, grid.GasTemperatureAt(i));
        double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
        grid.GasMass[_w.SteamSpecies][i] += vented;
        EnergyAudit.SolveTemperatures(grid, i, condensed, gas + steamJ);
        LightJ += steamJ - ventJ;
        Stir(sim, i);
        return (kg - vented, joules - ventJ);
    }

    /// <summary>Whether a tile has air enough in it for leaves or wet ground to breathe into.</summary>
    private static bool HasAir(WorldGrid grid, int i) =>
        grid.FreeVolume(i) >= Fluids.FluidSystem.GasLockFraction * WorldGrid.TileVolumeM3 && grid.TotalGasMass(i) > WorldGrid.SeparateGasKg;

    /// <summary>Soil open to the air, and the plants in it: light, vapour, photosynthesis, growth and death.</summary>
    private void Breathe(Simulation sim, double dt)
    {
        var grid = sim.Grid;
        int plane = grid.Width * grid.Height;
        for (int i = 0; i < grid.Count; i++)
        {
            bool plant = grid.HasCondensed(i) && grid.Materials[grid.Material[i]].IsPlant;
            int below = i - plane;
            bool overSoil = below >= 0 && SoilVolume(grid, below) > 0.0;
            if (!plant && !(overSoil && grid.PoreWater[below] > 0.0)) continue;
            if (!HasAir(grid, i)) continue;
            var (x, y, z) = grid.Coordinates(i);
            // Light at the top of the tile: what the plants in it catch, and what passes them reaches the ground.
            double lux = LightField.Illuminance(sim, _sources, x + 0.5, y + 0.5, z + 0.999);
            double ground = lux * LightField.TileTransmittance(grid, i, 1.0);
            if (overSoil) EvaporateSoil(sim, below, i, ground, dt);
            if (plant) Grow(sim, i, lux, overSoil ? below : -1, dt);
        }
    }

    private void EvaporateSoil(Simulation sim, int soil, int air, double lux, double dt)
    {
        var grid = sim.Grid;
        var def = grid.Materials[grid.Material[soil]];
        var (liquid, theta) = Liquid(grid, soil);
        if (!(liquid > 0.0)) return;
        double dryest = 0.5 * def.WiltingPoint;
        double wet = Math.Clamp((theta - dryest) / (def.FieldCapacity - dryest), 0.0, 1.0);
        double kg = EvaporationKgPerM2S(lux, grid.GasTemperatureAt(air)) * wet * WorldGrid.TileFaceAreaM2 * dt;
        if (!(kg > 0.0)) return;
        var (took, j) = TakePore(grid, soil, kg, dryest);
        if (!(took > 0.0)) return;
        var (back, backJ) = Vent(sim, air, took, j);
        grid.PoreWater[soil] += back;
        grid.PoreWaterJ[soil] += backJ;
    }

    // ---------------------------------------------------------------- plants

    /// <summary>Dry share of a plant or food material: what is carbohydrate rather than water.</summary>
    public static double DryShare(MaterialDef def) => EatFood.StarchShare(def);

    /// <summary>Leaf area of a tile's plant over the ground it covers.</summary>
    public static double LeafAreaIndex(WorldGrid grid, int i) =>
        grid.HasCondensed(i) && grid.Materials[grid.Material[i]].IsPlant
            ? SpecificLeafArea * grid.Mass[i] * DryShare(grid.Materials[grid.Material[i]]) / WorldGrid.TileFaceAreaM2
            : 0.0;

    /// <summary>How much better or worse photosynthesis goes for carbon dioxide than at 400 ppm.</summary>
    public static double CarbonFactor(double ppm)
    {
        double at400 = (400.0 - CompensationPpm) / (400.0 + HalfSaturationPpm);
        return Math.Max(0.0, ppm - CompensationPpm) / (ppm + HalfSaturationPpm) / at400;
    }

    /// <summary>How well photosynthesis goes at a leaf temperature, 0 to 1 (a beta curve: none at 0 and 45 °C, best at 22 °C).</summary>
    public static double WarmthFactor(double kelvin)
    {
        if (kelvin <= ColdestK || kelvin >= HottestK) return 0.0;
        double rise = (kelvin - ColdestK) / (BestK - ColdestK), fall = (HottestK - kelvin) / (HottestK - BestK);
        return rise * Math.Pow(fall, (HottestK - BestK) / (BestK - ColdestK));
    }

    /// <summary>Carbon dioxide in a tile's gas, parts per million by volume.</summary>
    public double CarbonDioxidePpm(WorldGrid grid, int i)
    {
        var table = grid.Materials;
        double mol = 0.0;
        for (int s = 0; s < table.GasCount; s++) mol += grid.GasMass[s][i] / table[table.GasIds[s]].MolarMass;
        return mol > 0.0 ? 1e6 * grid.GasMass[_w.CarbonDioxideSpecies][i] / table[_w.CarbonDioxide].MolarMass / mol : 0.0;
    }

    /// <summary>How freely a plant's roots draw water, 0 (soil at the wilting point) to 1 (moist), from the two tiles below.</summary>
    private double RootWater(WorldGrid grid, int soil, out double spareKg)
    {
        spareKg = 0.0;
        if (soil < 0) return 0.0;
        double supply = 0.0, weight = 0.0;
        int plane = grid.Width * grid.Height;
        for (int k = 0; k < 2; k++)
        {
            int r = soil - k * plane;
            if (r < 0 || SoilVolume(grid, r) <= 0.0) break;
            var def = grid.Materials[grid.Material[r]];
            var (_, theta) = Liquid(grid, r);
            double share = k == 0 ? 0.7 : 0.3;
            supply += share * Math.Clamp((theta - def.WiltingPoint) / (0.6 * (def.FieldCapacity - def.WiltingPoint)), 0.0, 1.0);
            weight += share;
            spareKg += Math.Max(0.0, (theta - def.WiltingPoint) * WaterDensity * SoilVolume(grid, r));
        }
        return weight > 0.0 ? supply / weight : 0.0;
    }

    /// <summary>Draws water from the roots' soil, the upper tile first; returns kg and J.</summary>
    private (double Kg, double J) Drink(WorldGrid grid, int soil, double kg)
    {
        double took = 0.0, j = 0.0;
        int plane = grid.Width * grid.Height;
        for (int k = 0; k < 2 && took < kg; k++)
        {
            int r = soil - k * plane;
            if (r < 0 || SoilVolume(grid, r) <= 0.0) break;
            var (a, b) = TakePore(grid, r, kg - took, grid.Materials[grid.Material[r]].WiltingPoint);
            took += a;
            j += b;
        }
        return (took, j);
    }

    private void Grow(Simulation sim, int i, double lux, int soil, double dt)
    {
        var grid = sim.Grid;
        var def = grid.Materials[grid.Material[i]];
        double share = DryShare(def);
        double dry = grid.Mass[i] * share;
        double t = grid.Temperature[i];
        double beta = RootWater(grid, soil, out _);
        double stress = grid.PlantStress[i];

        // Photosynthesis, µmol CO₂ per m² of ground per second.
        double absorbed = PhotonsPerLux * lux * (1.0 - Math.Exp(-CanopyExtinction * LeafAreaIndex(grid, i)));
        double gross = MaxPhotosynthesis * (1.0 - Math.Exp(-QuantumYield * absorbed / MaxPhotosynthesis))
                       * CarbonFactor(CarbonDioxidePpm(grid, i)) * WarmthFactor(t) * beta * (1.0 - stress);
        double fixedDry = gross * 1e-6 * WorldGrid.TileFaceAreaM2 * dt * Physiology.MolarMassStarchPerCarbon;
        if (fixedDry > 0.0) fixedDry = Fix(sim, i, soil, def, fixedDry);

        // Respiration: building what was fixed, and the upkeep of living tissue.
        double q10 = Math.Pow(2.0, (t - 293.15) / 10.0);
        double respired = GrowthRespiration * fixedDry + UpkeepPerSecond * q10 * dry * dt;
        if (respired > 0.0) Respire(grid, i, soil, Math.Min(respired / share, grid.Mass[i]));

        // Transpiration: its leaves give off what water the light and its roots allow.
        if (soil >= 0 && lux > 0.0)
        {
            double cover = 1.0 - Math.Exp(-CanopyExtinction * LeafAreaIndex(grid, i));
            double want = EvaporationKgPerM2S(lux, grid.GasTemperatureAt(i)) * cover * beta * (1.0 - stress) * WorldGrid.TileFaceAreaM2 * dt;
            var (took, j) = Drink(grid, soil, want);
            if (took > 0.0)
            {
                var (back, backJ) = Vent(sim, i, took, j);
                if (back > 0.0) { grid.PoreWater[soil] += back; grid.PoreWaterJ[soil] += backJ; }
            }
        }

        // Stress: drought, frost and heat build it; good days ease it.
        double rise = 0.0;
        if (beta < 0.05) rise += dt / DroughtSeconds;
        if (t < FrostK) rise += dt / FrostSeconds * Math.Min(1.0, (FrostK - t) / 5.0);
        if (t > HottestK) rise += dt / HeatSeconds * Math.Min(1.0, (t - HottestK) / 5.0);
        grid.PlantStress[i] = rise > 0.0 ? Math.Min(1.0, stress + rise) : Math.Max(0.0, stress - dt / RecoverySeconds);

        if (!grid.HasCondensed(i)) return;
        dry = grid.Mass[i] * share;
        if (grid.PlantStress[i] >= 1.0 || dry < SmallestDryKg) { Die(sim, i, soil); return; }
        if (dry >= EstablishedDryKg && beta > 0.3 && grid.PlantStress[i] < 0.3 && sim.Rng.NextFloat() < dt / SpreadSeconds)
            Spread(sim, i);
    }

    /// <summary>
    /// Fixes up to <paramref name="dryKg"/> of carbohydrate from the tile's carbon dioxide and the
    /// roots' water, as much as they allow, into fresh tissue; the oxygen goes back into the air.
    /// Everything stays at its temperature, the energy stored coming from light. Returns kg fixed.
    /// </summary>
    private double Fix(Simulation sim, int i, int soil, MaterialDef def, double dryKg)
    {
        var grid = sim.Grid;
        double share = DryShare(def);
        double co2PerKg = Physiology.GlycogenCO2, o2PerKg = Physiology.GlycogenO2;
        double waterPerKg = 1.0 + o2PerKg - co2PerKg + (1.0 - share) / share;
        double f = _w.OxygenFraction;
        double nitrogenPerKg = o2PerKg * (1.0 - f) / f;
        dryKg = Math.Min(dryKg, 0.5 * grid.GasMass[_w.CarbonDioxideSpecies][i] / co2PerKg);
        dryKg = Math.Min(dryKg, 0.5 * grid.GasMass[_w.NitrogenSpecies][i] / nitrogenPerKg);
        if (soil < 0) return 0.0;
        RootWater(grid, soil, out double spare);
        dryKg = Math.Min(dryKg, 0.5 * spare / waterPerKg);
        if (!(dryKg > 0.0)) return 0.0;

        double before = EnergyAudit.CondensedJoules(grid, i) + EnergyAudit.GasJoules(grid, i);
        var (water, waterJ) = Drink(grid, soil, dryKg * waterPerKg);
        dryKg = water / waterPerKg;
        grid.Mass[i] += dryKg / share;
        grid.GasMass[_w.CarbonDioxideSpecies][i] -= dryKg * co2PerKg;
        grid.GasMass[_w.NitrogenSpecies][i] -= dryKg * nitrogenPerKg;
        grid.GasMass[_w.AirSpecies][i] += dryKg * (o2PerKg + nitrogenPerKg);
        double after = EnergyAudit.CondensedJoules(grid, i) + EnergyAudit.GasJoules(grid, i);
        LightJ += after - before - waterJ;
        Stir(sim, i);
        return dryKg;
    }

    /// <summary>
    /// A plant respires <paramref name="kg"/> of its tissue: its carbohydrate burns to carbon
    /// dioxide and water, and the water it held goes back to the soil it was drawn from rather than
    /// into the air (or into the air, if there is no soil). Its heat of condensing stays in the gas.
    /// </summary>
    private void Respire(WorldGrid grid, int i, int soil, double kg)
    {
        double share = DryShare(grid.Materials[grid.Material[i]]);
        kg = Chemistry.Oxidise(grid, i, kg);
        double water = Math.Min(kg * (1.0 - share), grid.GasMass[_w.SteamSpecies][i]);
        if (!(water > 0.0) || soil < 0 || PoreCapacityKg(grid, soil) - grid.PoreWater[soil] < water) return;
        double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
        double j = water * WaterJ(Math.Max(grid.Temperature[soil], PhaseMeltK));
        grid.GasMass[_w.SteamSpecies][i] -= water;
        grid.PoreWater[soil] += water;
        grid.PoreWaterJ[soil] += j;
        EnergyAudit.SolveTemperatures(grid, i, condensed, gas - j);
    }

    /// <summary>A plant dies into its dead material, its spare water going into the soil under it (or nowhere yet, if there is no room).</summary>
    private void Die(Simulation sim, int i, int soil)
    {
        var grid = sim.Grid;
        var def = grid.Materials[grid.Material[i]];
        var deadDef = grid.Materials[grid.Materials.Id(def.DiesInto!)];
        ushort dead = grid.Materials.Id(def.DiesInto!);
        double kg = grid.Mass[i];
        double deadKg = kg * DryShare(def) / DryShare(deadDef);
        double water = kg - deadKg;
        double room = soil >= 0 ? PoreCapacityKg(grid, soil) - grid.PoreWater[soil] : 0.0;
        if (water > room) return;
        double before = EnergyAudit.CondensedJoules(grid, i) + EnergyAudit.GasJoules(grid, i);
        double t = grid.Temperature[i];
        double waterJ = water * WaterJ(t);
        grid.Material[i] = dead;
        grid.Mass[i] = deadKg;
        grid.LatentEnergy[i] = 0.0;
        grid.PlantStress[i] = 0.0;
        if (water > 0.0)
        {
            grid.PoreWater[soil] += water;
            grid.PoreWaterJ[soil] += waterJ;
        }
        double gas = EnergyAudit.GasJoules(grid, i);
        EnergyAudit.SolveTemperatures(grid, i, before - waterJ - gas, gas);
        Stir(sim, i);
    }

    /// <summary>An established plant puts a seedling into open ground beside it.</summary>
    private void Spread(Simulation sim, int i)
    {
        var grid = sim.Grid;
        var (x, y, z) = grid.Coordinates(i);
        int dir = (int)sim.Rng.NextUInt(8);
        int dx = dir switch { 0 or 6 or 7 => -1, 2 or 3 or 4 => 1, _ => 0 };
        int dy = dir switch { 0 or 1 or 2 => -1, 4 or 5 or 6 => 1, _ => 0 };
        int nx = x + dx, ny = y + dy;
        if (!grid.InBounds(nx, ny, z) || z == 0) return;
        int j = grid.Index(nx, ny, z);
        if (grid.HasCondensed(j) || !HasAir(grid, j) || SoilVolume(grid, j - grid.Width * grid.Height) <= 0.0) return;
        var def = grid.Materials[grid.Material[i]];
        double kg = SeedlingDryKg / DryShare(def);
        if (grid.Mass[i] < 4.0 * kg) return;
        ushort m = grid.Material[i];
        double joules = EatFood.Take(grid, i, ref kg);
        Plant(grid, j, m, kg, joules);
        Stir(sim, j);
    }

    /// <summary>Puts kg of a plant holding <paramref name="joules"/> into an empty tile i.</summary>
    public static void Plant(WorldGrid grid, int i, ushort material, double kg, double joules)
    {
        double gas = EnergyAudit.GasJoules(grid, i);
        grid.SetCondensed(i, material, kg);
        grid.Temperature[i] = grid.GasTemperatureAt(i);
        grid.PlantStress[i] = 0.0;
        EnergyAudit.SolveTemperatures(grid, i, joules, gas);
    }

    // ---------------------------------------------------------------- decay

    /// <summary>How fast decomposers work at a temperature against 20 °C: doubling every 10 °C, none frozen or past 60 °C.</summary>
    public static double DecayWarmth(double kelvin) =>
        kelvin <= 273.15 || kelvin >= 333.15 ? 0.0 : Math.Pow(2.0, (kelvin - 293.15) / 10.0);

    /// <summary>How damp dead matter in tile i is for decomposers, 0 to 1: its own water, the soil under it, or the air's humidity.</summary>
    private double Damp(WorldGrid grid, int i, MaterialDef def)
    {
        double own = Math.Clamp((1.0 - DryShare(def) - 0.15) / 0.3, 0.0, 1.0);
        int below = i - grid.Width * grid.Height;
        double soil = 0.0;
        if (below >= 0 && SoilVolume(grid, below) > 0.0)
        {
            var under = grid.Materials[grid.Material[below]];
            soil = Math.Clamp(Liquid(grid, below).Theta / under.FieldCapacity, 0.0, 1.0);
        }
        return Math.Max(own, Math.Max(soil, Humidity(grid, i)));
    }

    /// <summary>Decomposers' share of full work for the humidity of a tile's air: none below 60%, all by 95%.</summary>
    private double Humidity(WorldGrid grid, int i)
    {
        double sat = Vapour.SaturationKg(Math.Max(grid.FreeVolume(i), WorldGrid.MinFreeVolumeM3), grid.GasTemperatureAt(i));
        double rh = sat > 0.0 ? grid.GasMass[_w.SteamSpecies][i] / sat : 0.0;
        return Math.Clamp((rh - 0.6) / 0.35, 0.0, 1.0);
    }

    /// <summary>Oxygen in a tile's gas against fresh air's share, 0 to 1.</summary>
    private double Airiness(WorldGrid grid, int i)
    {
        double total = grid.TotalGasMass(i);
        return total > 0.0 ? Math.Clamp(grid.GasMass[_w.AirSpecies][i] / total, 0.0, 1.0) : 0.0;
    }

    private void Rot(Simulation sim, double dt)
    {
        var grid = sim.Grid;
        for (int i = 0; i < grid.Count; i++)
        {
            if (!grid.HasCondensed(i)) continue;
            var def = grid.Materials[grid.Material[i]];
            if (!(def.DecayPerSecond > 0f) || DryShare(def) <= 0.0) continue;
            double rate = def.DecayPerSecond * DecayWarmth(grid.Temperature[i]) * Damp(grid, i, def) * Airiness(grid, i);
            if (!(rate > 0.0)) continue;
            double kg = grid.Mass[i] * (1.0 - Math.Exp(-rate * dt));
            // The last crumbs go together.
            if (grid.Mass[i] - kg < 1e-6) kg = grid.Mass[i];
            if (Chemistry.Oxidise(grid, i, kg) > 0.0) Stir(sim, i);
        }
    }

    /// <summary>Dead bodies rot: soft tissue seeps away as fluid, fat goes to carbon dioxide and water, bone is left.</summary>
    private void RotRemains(Simulation sim, double dt)
    {
        var grid = sim.Grid;
        foreach (var body in sim.Bodies)
        {
            if (body.Alive || !grid.InBounds(body.X, body.Y, body.Z)) continue;
            var anatomy = body.Anatomy;
            int tile = grid.Index(body.X, body.Y, body.Z);
            bool changed = false;
            double lipid = Physiology.LipidKg(body);
            for (int n = 0; n < body.Mass.Length; n++)
            {
                var tissue = anatomy.NodeTissue[n];
                if (tissue is Tissue.Bone or Tissue.Tooth) continue;
                double floor = RemainsFloor * anatomy.InitialMass[n];
                if (body.Mass[n] <= floor) continue;
                double rate = RemainsDecayPerSecond * DecayWarmth(body.Temperature[n]);
                if (!(rate > 0.0)) continue;
                double kg = Math.Min(body.Mass[n] - floor, body.Mass[n] * (1.0 - Math.Exp(-rate * dt)));
                double water = kg * body.WaterKg[n] / body.Mass[n];
                double fat = tissue == Tissue.Fat ? kg - water : 0.0;
                if (fat > 0.0 && !BurnFat(grid, body, n, tile, fat, ref lipid)) { kg = water; fat = 0.0; }
                double fluid = kg - fat;
                if (fluid > 0.0)
                {
                    double j = Physiology.JoulesOf(_w, body, n, fluid);
                    Physiology.AddMass(_w, body, n, -fluid, -j);
                    body.WaterKg[n] -= water;
                    body.SpillKg += fluid;
                    body.SpillJ += j;
                }
                changed = true;
            }
            if (body.SpillKg >= 0.02) Seep(sim, body);
            if (changed) Stir(sim, tile);
        }
    }

    /// <summary>
    /// Fat in a dead layer goes to carbon dioxide and water in the gas of the tile the body lies in,
    /// taking oxygen from its air; its chemical energy and heat go into that gas. False if the
    /// air can't supply the oxygen.
    /// </summary>
    private bool BurnFat(WorldGrid grid, Body body, int n, int tile, double fat, ref double lipid)
    {
        double o2 = fat * Physiology.FatO2;
        double air = o2 / _w.OxygenFraction;
        if (!(lipid > 0.0) || grid.GasMass[_w.AirSpecies][tile] < 2.0 * air) return false;
        double chem = body.FatJ * fat / lipid;
        double sensible = Physiology.JoulesOf(_w, body, n, fat);
        Physiology.AddMass(_w, body, n, -fat, -sensible);
        body.FatJ -= chem;
        lipid -= fat;
        double co2 = fat * Physiology.FatCO2, steam = fat + o2 - co2;
        double condensed = EnergyAudit.CondensedJoules(grid, tile), gas = EnergyAudit.GasJoules(grid, tile);
        grid.GasMass[_w.AirSpecies][tile] -= air;
        grid.GasMass[_w.NitrogenSpecies][tile] += air - o2;
        grid.GasMass[_w.CarbonDioxideSpecies][tile] += co2;
        grid.GasMass[_w.SteamSpecies][tile] += steam;
        EnergyAudit.SolveTemperatures(grid, tile, condensed, gas + chem + sensible);
        return true;
    }

    /// <summary>The fluid of a rotting body runs out where it lies: into the tile's liquid, or into the soil under it.</summary>
    private void Seep(Simulation sim, Body body)
    {
        var grid = sim.Grid;
        int i = grid.Index(body.X, body.Y, body.Z);
        double kg = body.SpillKg, joules = body.SpillJ;
        if (!grid.HasCondensed(i) || grid.Material[i] == _w.Water || grid.Material[i] == _w.Blood)
        {
            double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
            if (!grid.HasCondensed(i))
            {
                grid.SetCondensed(i, _w.Blood, kg);
                grid.Temperature[i] = grid.GasTemperatureAt(i);
            }
            else grid.Mass[i] += kg;
            EnergyAudit.SolveTemperatures(grid, i, condensed + joules, gas);
        }
        else
        {
            int below = i - grid.Width * grid.Height;
            if (below < 0 || SoilVolume(grid, below) <= 0.0 || PoreCapacityKg(grid, below) - grid.PoreWater[below] < kg) return;
            grid.PoreWater[below] += kg;
            grid.PoreWaterJ[below] += joules;
        }
        body.SpillKg = 0.0;
        body.SpillJ = 0.0;
        Stir(sim, i);
    }
}
