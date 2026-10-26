using System;
using System.Collections.Generic;

namespace Magischool.Sim.Materials;

/// <summary>
/// Immutable lookup from a material id (the ushort stored per tile) to its
/// constants. Ids are assigned in registration order, so the order is part
/// of the save format: append new materials, never reorder.
/// </summary>
public sealed class MaterialTable
{
    private readonly MaterialDef[] _defs;
    private readonly Dictionary<string, ushort> _idsByKey;

    public MaterialTable(IReadOnlyList<MaterialDef> defs)
    {
        if (defs.Count == 0 || defs.Count > ushort.MaxValue)
            throw new ArgumentException("A table needs between 1 and 65535 materials.", nameof(defs));

        _defs = new MaterialDef[defs.Count];
        _idsByKey = new Dictionary<string, ushort>(defs.Count, StringComparer.Ordinal);
        for (int i = 0; i < defs.Count; i++)
        {
            var def = defs[i];
            if (!_idsByKey.TryAdd(def.Key, (ushort)i))
                throw new ArgumentException($"Duplicate material key '{def.Key}'.", nameof(defs));
            _defs[i] = def;
        }

        foreach (var def in _defs)
        {
            CheckLink(def, def.MeltsInto);
            CheckLink(def, def.FreezesInto);
            CheckLink(def, def.BoilsInto);
            CheckLink(def, def.CondensesInto);
        }

        foreach (var def in _defs)
        {
            if (def.MeltsInto != null) CheckPair(def, this[Id(def.MeltsInto)], fusion: true);
            if (def.FreezesInto != null) CheckPair(this[Id(def.FreezesInto)], def, fusion: true);
            if (def.BoilsInto != null) CheckPair(def, this[Id(def.BoilsInto)], fusion: false);
            if (def.CondensesInto != null) CheckPair(this[Id(def.CondensesInto)], def, fusion: false);
        }
    }

    public int Count => _defs.Length;

    public MaterialDef this[ushort id] => _defs[id];

    public ushort Id(string key) =>
        _idsByKey.TryGetValue(key, out var id)
            ? id
            : throw new KeyNotFoundException($"Unknown material '{key}'.");

    public bool TryGetId(string key, out ushort id) => _idsByKey.TryGetValue(key, out id);

    public IEnumerable<MaterialDef> All => _defs;

    /// <summary>
    /// The two sides of a phase change must agree on the transition, or energy
    /// would appear or vanish on the way through and back.
    /// </summary>
    private static void CheckPair(MaterialDef lower, MaterialDef upper, bool fusion)
    {
        string name = $"'{lower.Key}' <-> '{upper.Key}'";
        bool linked = fusion
            ? lower.MeltsInto == upper.Key && upper.FreezesInto == lower.Key
            : lower.BoilsInto == upper.Key && upper.CondensesInto == lower.Key;
        bool phases = fusion
            ? lower.Phase == Phase.Solid && upper.Phase == Phase.Liquid
            : lower.Phase == Phase.Liquid && upper.Phase == Phase.Gas;
        bool matches = fusion
            ? lower.MeltingPoint == upper.MeltingPoint && lower.LatentHeatFusion == upper.LatentHeatFusion
            : lower.BoilingPoint == upper.BoilingPoint && lower.LatentHeatVaporization == upper.LatentHeatVaporization;
        if (!linked) throw new ArgumentException($"Phase link {name} must point both ways.");
        if (!phases) throw new ArgumentException($"Phase link {name} joins the wrong phases.");
        if (!matches) throw new ArgumentException($"Phase link {name} disagrees on transition temperature or latent heat.");
    }

    private void CheckLink(MaterialDef def, string? target)
    {
        if (target != null && !_idsByKey.ContainsKey(target))
            throw new ArgumentException($"Material '{def.Key}' links to unknown material '{target}'.");
    }
}
