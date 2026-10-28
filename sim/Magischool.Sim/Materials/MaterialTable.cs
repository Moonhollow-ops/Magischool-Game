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

    /// <summary>The id stored in a tile's condensed slot when it holds no solid or liquid.</summary>
    public const ushort Empty = 0;

    private readonly ushort[] _gasIds;
    private readonly int[] _gasIndex;

    public MaterialTable(IReadOnlyList<MaterialDef> defs)
    {
        if (defs.Count == 0 || defs.Count > ushort.MaxValue)
            throw new ArgumentException("A table needs between 1 and 65535 materials.", nameof(defs));
        if (defs[0].Phase != Phase.Gas)
            throw new ArgumentException("Material id 0 must be a gas (it marks an empty condensed slot).", nameof(defs));

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

        var gasIds = new List<ushort>();
        _gasIndex = new int[_defs.Length];
        for (ushort id = 0; id < _defs.Length; id++)
        {
            var def = _defs[id];
            _gasIndex[id] = -1;
            if (def.Phase == Phase.Gas)
            {
                if (def.MolarMass <= 0f) throw new ArgumentException($"Gas '{def.Key}' needs a molar mass.");
                _gasIndex[id] = gasIds.Count;
                gasIds.Add(id);
            }
            else if (def.Phase == Phase.Liquid && def.Viscosity <= 0f)
                throw new ArgumentException($"Liquid '{def.Key}' needs a viscosity.");
        }
        _gasIds = gasIds.ToArray();

        foreach (var def in _defs) CheckCombustion(def);

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

    /// <summary>Material ids of every gas, in id order. A gas's position here is its species index.</summary>
    public IReadOnlyList<ushort> GasIds => _gasIds;

    public int GasCount => _gasIds.Length;

    /// <summary>Species index of a gas material, or -1 if it isn't a gas.</summary>
    public int GasIndexOf(ushort id) => _gasIndex[id];

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

    /// <summary>Burning must neither make nor destroy mass, and must turn into gases the table knows.</summary>
    private void CheckCombustion(MaterialDef def)
    {
        if (def.OxygenFraction > 0f)
        {
            if (def.Phase != Phase.Gas || def.OxygenFraction >= 1f)
                throw new ArgumentException($"'{def.Key}' can only hold oxygen as a gas, with a fraction below 1.");
            if (def.OxygenRemainder == null || !TryGetId(def.OxygenRemainder, out var rest) || this[rest].Phase != Phase.Gas)
                throw new ArgumentException($"'{def.Key}' needs a gas to leave behind when its oxygen is used.");
        }
        if (def.HeatOfCombustion <= 0f) return;
        if (def.Phase == Phase.Gas || float.IsPositiveInfinity(def.IgnitionPoint) || def.OxygenPerKg <= 0f)
            throw new ArgumentException($"Fuel '{def.Key}' must be a solid or liquid with an ignition point and an oxygen need.");
        double total = 0.0;
        foreach (var product in def.CombustionProducts)
        {
            if (!TryGetId(product.Key, out var id) || this[id].Phase != Phase.Gas)
                throw new ArgumentException($"Fuel '{def.Key}' burns into '{product.Key}', which is not a known gas.");
            total += product.KgPerKgFuel;
        }
        if (Math.Abs(total - (1.0 + def.OxygenPerKg)) > 1e-4)
            throw new ArgumentException($"Fuel '{def.Key}' products weigh {total} kg per kg but fuel plus oxygen is {1.0 + def.OxygenPerKg}.");
    }

    private void CheckLink(MaterialDef def, string? target)
    {
        if (target != null && !_idsByKey.ContainsKey(target))
            throw new ArgumentException($"Material '{def.Key}' links to unknown material '{target}'.");
    }
}
