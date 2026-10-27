using System;
using System.Runtime.CompilerServices;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Thermal;

/// <summary>The phase transitions each material can make, looked up once per table.</summary>
internal sealed class PhaseRules
{
    private static readonly ConditionalWeakTable<MaterialTable, PhaseRules> Cache = new();

    public readonly Transition[] Upper;
    public readonly Transition[] Lower;

    public readonly struct Transition
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

    public static PhaseRules For(MaterialTable table) => Cache.GetValue(table, t => new PhaseRules(t));

    private PhaseRules(MaterialTable table)
    {
        Upper = new Transition[table.Count];
        Lower = new Transition[table.Count];
        for (ushort id = 0; id < table.Count; id++)
        {
            var def = table[id];
            switch (def.Phase)
            {
                case Phase.Solid when def.MeltsInto != null:
                    Upper[id] = new Transition(def.MeltingPoint, def.LatentHeatFusion, table.Id(def.MeltsInto));
                    break;
                case Phase.Liquid:
                    if (def.BoilsInto != null)
                        Upper[id] = new Transition(def.BoilingPoint, def.LatentHeatVaporization, table.Id(def.BoilsInto));
                    if (def.FreezesInto != null)
                        Lower[id] = new Transition(def.MeltingPoint, def.LatentHeatFusion, table.Id(def.FreezesInto));
                    break;
                case Phase.Gas when def.CondensesInto != null:
                    Lower[id] = new Transition(def.BoilingPoint, def.LatentHeatVaporization, table.Id(def.CondensesInto));
                    break;
            }
        }
    }

    /// <summary>True when a tile has no phase change under way or due.</summary>
    public bool IsSettled(ushort material, double temperatureK, double latent)
    {
        if (latent != 0.0) return false;
        var up = Upper[material];
        if (up.Exists && temperatureK > up.TemperatureK) return false;
        var down = Lower[material];
        if (down.Exists && temperatureK < down.TemperatureK) return false;
        return temperatureK >= 0.0;
    }
}
