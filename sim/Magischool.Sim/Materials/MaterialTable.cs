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
    }

    public int Count => _defs.Length;

    public MaterialDef this[ushort id] => _defs[id];

    public ushort Id(string key) =>
        _idsByKey.TryGetValue(key, out var id)
            ? id
            : throw new KeyNotFoundException($"Unknown material '{key}'.");

    public bool TryGetId(string key, out ushort id) => _idsByKey.TryGetValue(key, out id);

    public IEnumerable<MaterialDef> All => _defs;

    private void CheckLink(MaterialDef def, string? target)
    {
        if (target != null && !_idsByKey.ContainsKey(target))
            throw new ArgumentException($"Material '{def.Key}' links to unknown material '{target}'.");
    }
}
