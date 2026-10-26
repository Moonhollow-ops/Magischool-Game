using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Thermal;

/// <summary>
/// Layer 3: melting, freezing, boiling and condensing with latent heat.
///
/// A tile that reaches a transition temperature stops changing temperature.
/// Extra energy goes into <see cref="WorldGrid.LatentEnergy"/> instead, and
/// only when the full latent heat (J/kg × the tile's mass) has been absorbed
/// does the material change. Cooling works the same way in reverse, and a
/// half-melted tile that starts cooling gives its stored energy back first.
///
/// Mass is kept through every change, so 917 kg of ice becomes 917 kg of
/// water (a tile 92% full) and 1000 kg of water freezes into ice that needs
/// 109% of a tile. Spreading the extra volume is the fluids layer's job, and
/// the squeeze is the structure layer's.
///
/// Runs after <see cref="ThermalSystem"/> each tick. Energy is conserved
/// exactly; <see cref="EnergyAudit"/> counts it.
/// </summary>
public sealed class PhaseSystem : ISimSystem
{
    // A tile that receives enormous energy in one tick can pass through several transitions (ice to steam).
    private const int MaxTransitionsPerTick = 4;

    private MaterialTable? _preparedFor;
    private Transition[] _upper = Array.Empty<Transition>();
    private Transition[] _lower = Array.Empty<Transition>();

    public string Name => "phase";

    private readonly struct Transition
    {
        public readonly bool Exists;
        public readonly double TemperatureK;
        public readonly double LatentPerKg;
        public readonly ushort Into;

        public Transition(double temperatureK, double latentPerKg, ushort into)
        {
            Exists = true;
            TemperatureK = temperatureK;
            LatentPerKg = latentPerKg;
            Into = into;
        }
    }

    public void Update(Simulation sim)
    {
        Prepare(sim.Materials);

        var grid = sim.Grid;
        ushort[] material = grid.Material;
        double[] temperature = grid.Temperature;
        double[] mass = grid.Mass;
        double[] latent = grid.LatentEnergy;
        var table = sim.Materials;

        for (int i = 0; i < grid.Count; i++)
        {
            for (int pass = 0; pass < MaxTransitionsPerTick; pass++)
            {
                ushort m = material[i];
                var up = _upper[m];
                var down = _lower[m];
                double t = temperature[i];
                double s = latent[i];

                bool heating = s > 0.0 || (s == 0.0 && up.Exists && t > up.TemperatureK);
                bool cooling = !heating && (s < 0.0 || (s == 0.0 && down.Exists && t < down.TemperatureK));
                if (!heating && !cooling) break;

                double capacity = mass[i] * table[m].SpecificHeat;
                if (capacity <= 0.0) break;

                var transition = heating ? up : down;
                double energy = s + (t - transition.TemperatureK) * capacity; // signed: + toward up, - toward down
                double needed = transition.LatentPerKg * mass[i];

                if (heating ? energy <= 0.0 : energy >= 0.0)
                {
                    // Turned back before finishing: return the stored energy as temperature.
                    latent[i] = 0.0;
                    temperature[i] = transition.TemperatureK + energy / capacity;
                    break;
                }

                if (Math.Abs(energy) < needed)
                {
                    latent[i] = energy;
                    temperature[i] = transition.TemperatureK;
                    break;
                }

                double leftover = heating ? energy - needed : energy + needed;
                material[i] = transition.Into;
                latent[i] = 0.0;
                temperature[i] = transition.TemperatureK + leftover / (mass[i] * table[transition.Into].SpecificHeat);
            }

            // Nothing is colder than absolute zero. Only an outside command can push a tile here;
            // flooring it means that command took out less energy than it asked for.
            if (temperature[i] < 0.0) temperature[i] = 0.0;
        }
    }

    private void Prepare(MaterialTable table)
    {
        if (ReferenceEquals(_preparedFor, table)) return;
        _upper = new Transition[table.Count];
        _lower = new Transition[table.Count];
        for (ushort id = 0; id < table.Count; id++)
        {
            var def = table[id];
            switch (def.Phase)
            {
                case Phase.Solid when def.MeltsInto != null:
                    _upper[id] = new Transition(def.MeltingPoint, def.LatentHeatFusion, table.Id(def.MeltsInto));
                    break;
                case Phase.Liquid:
                    if (def.BoilsInto != null)
                        _upper[id] = new Transition(def.BoilingPoint, def.LatentHeatVaporization, table.Id(def.BoilsInto));
                    if (def.FreezesInto != null)
                        _lower[id] = new Transition(def.MeltingPoint, def.LatentHeatFusion, table.Id(def.FreezesInto));
                    break;
                case Phase.Gas when def.CondensesInto != null:
                    _lower[id] = new Transition(def.BoilingPoint, def.LatentHeatVaporization, table.Id(def.CondensesInto));
                    break;
            }
        }
        _preparedFor = table;
    }
}
