using System;
using System.IO;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Bodies;

/// <summary>
/// Brings a healthy, rested, fed body into the world with its feet in tile (X, Y, Z), if the
/// column it stands in is clear. Like <see cref="SetMaterial"/>, this makes mass and energy from
/// nothing: it is how the world is populated, not something that happens in it.
/// </summary>
public sealed record SpawnBody(int X, int Y, int Z, string Plan, double ClothingClo = 1.0) : WorldCommand
{
    internal const byte TagValue = 9;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var grid = sim.Grid;
        if (!BodyPlans.TryGet(Plan, out var plan)) return;
        for (int k = 0; k < plan.HeightTiles; k++)
        {
            if (!grid.InBounds(X, Y, Z + k)) return;
            int i = grid.Index(X, Y, Z + k);
            if (grid.HasCondensed(i) && grid.Materials[grid.Material[i]].Phase == Phase.Solid && grid.FillFraction(i) >= 0.5) return;
        }
        var body = Physiology.Create(Physiology.For(sim.Materials), plan, sim.NextBodyId++, X, Y, Z);
        body.ClothingClo = Math.Max(0.0, ClothingClo);
        sim.Bodies.Add(body);
        sim.Activity.WakeAround(X, Y, Z);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Plan);
        writer.Write(ClothingClo);
    }

    internal static SpawnBody ReadFrom(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadString(), r.ReadDouble());

    internal static Body? Find(Simulation sim, int id)
    {
        foreach (var body in sim.Bodies)
            if (body.Id == id) return body;
        return null;
    }

    /// <summary>Whether tile (x, y, z) is within arm's reach of a body: beside its column, from its feet's level below to just over its head.</summary>
    internal static bool InReach(Body body, int x, int y, int z) =>
        Math.Abs(x - body.X) <= 1 && Math.Abs(y - body.Y) <= 1 && z >= body.Z - 1 && z <= body.Z + body.HeightTiles;
}

/// <summary>
/// A body steps one tile (DX and DY each -1, 0 or 1), up a step of one tile if it must. It
/// walks only if it is conscious, standing, and has a sound leg; the effort is paid for from
/// its fuel. The step happens when the bodies layer next runs.
/// </summary>
public sealed record MoveBody(int Id, int DX, int DY) : WorldCommand
{
    internal const byte TagValue = 10;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var body = SpawnBody.Find(sim, Id);
        if (body == null) return;
        body.StepX = Math.Clamp(DX, -1, 1);
        body.StepY = Math.Clamp(DY, -1, 1);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(Id); writer.Write(DX); writer.Write(DY);
    }

    internal static MoveBody ReadFrom(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
}

/// <summary>
/// A body eats up to <paramref name="Kg"/> of food from a tile within reach, as much as its
/// stomach has room for. The food goes down with the energy it held, warming to the body (or
/// warming it).
/// </summary>
public sealed record EatFood(int Id, int X, int Y, int Z, double Kg) : WorldCommand
{
    internal const byte TagValue = 11;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var grid = sim.Grid;
        var body = SpawnBody.Find(sim, Id);
        if (body == null || !body.Conscious || !grid.InBounds(X, Y, Z) || !SpawnBody.InReach(body, X, Y, Z)) return;
        int i = grid.Index(X, Y, Z);
        if (!grid.HasCondensed(i)) return;
        var def = grid.Materials[grid.Material[i]];
        if (!def.Edible) return;
        double carbShare = StarchShare(def);
        if (carbShare <= 0.0) return;
        double room = body.Plan.GutMaxKg - body.GutCarbKg - body.GutWaterKg;
        if (!(body.FunctionLevel(PartFunction.Gut) > 0.0)) return;
        // Without teeth it can only gum soft mouthfuls: a fifth as much at a time.
        double bite = Kg * Math.Clamp(body.FunctionLevel(PartFunction.Teeth), 0.2, 1.0);
        double kg = Math.Min(Math.Min(bite, room), grid.Mass[i]);
        if (!(kg > 0.0)) return;

        var w = Physiology.For(sim.Materials);
        // What it would hold at body temperature: the stomach counts it at that.
        double atBody = kg * w.GasJPerKg(grid.Material[i], Physiology.ReferenceK);
        double joules = Take(grid, i, ref kg);
        double water = kg * (1.0 - carbShare);
        body.GutWaterKg += water;
        body.GutWaterJ += water * w.WaterJPerKg;
        body.GutCarbKg += kg - water;
        body.GutCarbJ += atBody - water * w.WaterJPerKg;
        BodySystem.HeatParts(body, PartFunction.Gut, joules - atBody);
        sim.Activity.WakeAround(X, Y, Z);
    }

    /// <summary>
    /// Share of a food that is starch, from how it burns: starch makes 1.629 kg of carbon dioxide
    /// a kilogram, and the rest is water. Zero if it doesn't burn as starch and water do.
    /// </summary>
    public static double StarchShare(MaterialDef def)
    {
        double co2 = 0.0, other = 0.0;
        foreach (var product in def.CombustionProducts)
        {
            if (product.Key == BuiltInMaterials.CarbonDioxide) co2 += product.KgPerKgFuel;
            else if (product.Key != BuiltInMaterials.Steam) other += product.KgPerKgFuel;
        }
        double starch = co2 / Physiology.GlycogenCO2;
        if (other > 0.0 || starch <= 0.0 || starch > 1.0) return 0.0;
        if (Math.Abs(def.OxygenPerKg - starch * Physiology.GlycogenO2) > 0.01 * def.OxygenPerKg) return 0.0;
        return starch;
    }

    /// <summary>
    /// Takes kg of a tile's solid or liquid, returning the energy it held; the rest of the tile
    /// keeps its temperature. Crumbs under a microgram go too, and are counted in <paramref name="kg"/>.
    /// </summary>
    internal static double Take(WorldGrid grid, int i, ref double kg)
    {
        double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
        double mass = grid.Mass[i];
        double joules = condensed * kg / mass;
        if (mass - kg < 1e-9)
        {
            kg = mass;
            joules = condensed;
            grid.SetCondensed(i, MaterialTable.Empty, 0.0);
        }
        else
        {
            grid.LatentEnergy[i] *= (mass - kg) / mass;
            grid.Mass[i] = mass - kg;
        }
        EnergyAudit.SolveTemperatures(grid, i, condensed - joules, gas);
        return joules;
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(Id); writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Kg);
    }

    internal static EatFood ReadFrom(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadDouble());
}

/// <summary>A body drinks up to <paramref name="Kg"/> of water from a tile within reach, as much as its stomach has room for.</summary>
public sealed record DrinkWater(int Id, int X, int Y, int Z, double Kg) : WorldCommand
{
    internal const byte TagValue = 12;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var grid = sim.Grid;
        var body = SpawnBody.Find(sim, Id);
        if (body == null || !body.Conscious || !grid.InBounds(X, Y, Z) || !SpawnBody.InReach(body, X, Y, Z)) return;
        int i = grid.Index(X, Y, Z);
        if (!grid.HasCondensed(i) || !grid.Materials[grid.Material[i]].Potable) return;
        double room = body.Plan.GutMaxKg - body.GutCarbKg - body.GutWaterKg;
        if (!(body.FunctionLevel(PartFunction.Gut) > 0.0)) return;
        double kg = Math.Min(Math.Min(Kg, room), grid.Mass[i]);
        if (!(kg > 0.0)) return;
        var w = Physiology.For(sim.Materials);
        double joules = EatFood.Take(grid, i, ref kg);
        body.GutWaterKg += kg;
        body.GutWaterJ += kg * w.WaterJPerKg;
        BodySystem.HeatParts(body, PartFunction.Gut, joules - kg * w.WaterJPerKg);
        sim.Activity.WakeAround(X, Y, Z);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(Id); writer.Write(X); writer.Write(Y); writer.Write(Z);
        writer.Write(Kg);
    }

    internal static DrinkWater ReadFrom(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadDouble());
}

/// <summary>A body lies down to sleep, or wakes. Only a rested body wakes by choice; past a day and a half awake it sleeps whatever it wants.</summary>
public sealed record SetAsleep(int Id, bool Asleep) : WorldCommand
{
    internal const byte TagValue = 13;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var body = SpawnBody.Find(sim, Id);
        if (body == null || !body.Alive || body.HeartStopped) return;
        if (Asleep) body.Asleep = true;
        else if (body.SleepPressure < 1.5) body.Asleep = false;
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(Id); writer.Write(Asleep);
    }

    internal static SetAsleep ReadFrom(BinaryReader r) => new(r.ReadInt32(), r.ReadBoolean());
}
