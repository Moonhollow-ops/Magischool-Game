using System.Runtime.CompilerServices;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Thermal;

/// <summary>The phase transitions each material can make, looked up once per table.</summary>
internal sealed class PhaseRules
{
    private static readonly ConditionalWeakTable<MaterialTable, PhaseRules> Cache = new();

    /// <summary>Solid → liquid (melting), per condensed material.</summary>
    public readonly Transition[] Melt;
    /// <summary>Liquid → solid (freezing), per condensed material.</summary>
    public readonly Transition[] Freeze;
    /// <summary>Liquid → gas (boiling), per condensed material; Into is the gas material.</summary>
    public readonly Transition[] Boil;
    /// <summary>Gas → liquid (condensing), per gas species index; Into is the liquid material.</summary>
    public readonly Transition[] Condense;

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
        Melt = new Transition[table.Count];
        Freeze = new Transition[table.Count];
        Boil = new Transition[table.Count];
        Condense = new Transition[table.GasCount];
        for (ushort id = 0; id < table.Count; id++)
        {
            var def = table[id];
            if (def.Phase == Phase.Solid && def.MeltsInto != null)
                Melt[id] = new Transition(def.MeltingPoint, def.LatentHeatFusion, table.Id(def.MeltsInto));
            if (def.Phase == Phase.Liquid && def.FreezesInto != null)
                Freeze[id] = new Transition(def.MeltingPoint, def.LatentHeatFusion, table.Id(def.FreezesInto));
            if (def.Phase == Phase.Liquid && def.BoilsInto != null)
                Boil[id] = new Transition(def.BoilingPoint, def.LatentHeatVaporization, table.Id(def.BoilsInto));
            if (def.Phase == Phase.Gas && def.CondensesInto != null)
                Condense[table.GasIndexOf(id)] = new Transition(def.BoilingPoint, def.LatentHeatVaporization, table.Id(def.CondensesInto));
        }
    }

    /// <summary>True when nothing in a tile is changing phase or due to.</summary>
    public bool IsSettled(WorldGrid grid, int i)
    {
        double t = grid.Temperature[i];
        if (t < 0.0 || grid.LatentEnergy[i] != 0.0) return false;
        ushort m = grid.Material[i];
        if (m != MaterialTable.Empty)
        {
            if (Melt[m].Exists && t > Melt[m].TemperatureK) return false;
            if (Freeze[m].Exists && t < Freeze[m].TemperatureK) return false;
            if (Boil[m].Exists && t > Boil[m].TemperatureK) return false;
        }
        for (int s = 0; s < Condense.Length; s++)
        {
            var c = Condense[s];
            if (c.Exists && t < c.TemperatureK && grid.GasMass[s][i] > 0.0 && (m == MaterialTable.Empty || m == c.Into))
                return false;
        }
        return true;
    }
}
