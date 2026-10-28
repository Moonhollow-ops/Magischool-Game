using System;
using System.IO;
using Magischool.Sim.Core;

namespace Magischool.Sim.Magic;

/// <summary>
/// Heat a tile from afar: the caster gathers up to <paramref name="Joules"/> of ether and it
/// appears as heat in the target tile, in its solid or liquid first.
/// </summary>
public sealed record CastHeat(int CasterX, int CasterY, int CasterZ, int TargetX, int TargetY, int TargetZ, double Joules) : WorldCommand
{
    internal const byte TagValue = 4;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var grid = sim.Grid;
        if (!grid.InBounds(TargetX, TargetY, TargetZ)) return;
        int target = grid.Index(TargetX, TargetY, TargetZ);
        if (grid.HeatCapacity(target) <= 0.0) return;
        double joules = Spellcasting.Gather(sim, CasterX, CasterY, CasterZ, Joules);
        Spellcasting.Heat(grid, target, joules);
        sim.Activity.WakeAround(TargetX, TargetY, TargetZ);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(CasterX); writer.Write(CasterY); writer.Write(CasterZ);
        writer.Write(TargetX); writer.Write(TargetY); writer.Write(TargetZ);
        writer.Write(Joules);
    }

    internal static CastHeat ReadFrom(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadDouble());
}

/// <summary>
/// A thermal sink: pulls up to <paramref name="Joules"/> of heat out of the target tile, its
/// solid or liquid first, and hands it to the ether around the caster. At most half of the
/// tile's heat above absolute zero goes in one cast. Water freezes through its latent heat and
/// swells; what that does to its container is the structure layer's business.
/// </summary>
public sealed record CastFreeze(int CasterX, int CasterY, int CasterZ, int TargetX, int TargetY, int TargetZ, double Joules) : WorldCommand
{
    internal const byte TagValue = 5;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var grid = sim.Grid;
        if (!grid.InBounds(TargetX, TargetY, TargetZ) || !grid.InBounds(CasterX, CasterY, CasterZ) || !(Joules > 0.0)) return;
        int target = grid.Index(TargetX, TargetY, TargetZ);
        double joules = Math.Min(Joules, Spellcasting.FreezableJoules(grid, target));
        if (!(joules > 0.0)) return;
        Spellcasting.Heat(grid, target, -joules);
        Spellcasting.Release(sim, CasterX, CasterY, CasterZ, joules);
        sim.Activity.WakeAround(TargetX, TargetY, TargetZ);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(CasterX); writer.Write(CasterY); writer.Write(CasterZ);
        writer.Write(TargetX); writer.Write(TargetY); writer.Write(TargetZ);
        writer.Write(Joules);
    }

    internal static CastFreeze ReadFrom(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadDouble());
}

/// <summary>
/// Moves ether: the caster gathers up to <paramref name="Joules"/> from around them and pours it
/// into the target tile's field. Enough of it into a crystal and the crystal ruptures.
/// </summary>
public sealed record CastCharge(int CasterX, int CasterY, int CasterZ, int TargetX, int TargetY, int TargetZ, double Joules) : WorldCommand
{
    internal const byte TagValue = 6;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var grid = sim.Grid;
        if (!grid.InBounds(TargetX, TargetY, TargetZ)) return;
        double joules = Spellcasting.Gather(sim, CasterX, CasterY, CasterZ, Joules);
        grid.Ether[grid.Index(TargetX, TargetY, TargetZ)] += joules;
        sim.Activity.WakeAround(TargetX, TargetY, TargetZ);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(CasterX); writer.Write(CasterY); writer.Write(CasterZ);
        writer.Write(TargetX); writer.Write(TargetY); writer.Write(TargetZ);
        writer.Write(Joules);
    }

    internal static CastCharge ReadFrom(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadDouble());
}

/// <summary>
/// A fireball: the caster gathers up to <paramref name="Joules"/> of ether and looses it as a bolt
/// from the centre of their tile along (<paramref name="DirX"/>, <paramref name="DirY"/>,
/// <paramref name="DirZ"/>) at <see cref="SpeedMps"/>. It carries the energy as heat to whatever it
/// strikes (see <see cref="SpellSystem"/>); there is no damage number anywhere.
/// </summary>
public sealed record CastFireball(int CasterX, int CasterY, int CasterZ, double DirX, double DirY, double DirZ, double Joules) : WorldCommand
{
    internal const byte TagValue = 7;
    internal override byte Tag => TagValue;

    /// <summary>How fast a fireball flies, m/s.</summary>
    public const double SpeedMps = 30.0;

    public override void Apply(Simulation sim)
    {
        double length = Math.Sqrt(DirX * DirX + DirY * DirY + DirZ * DirZ);
        if (!(length > 0.0)) return;
        double joules = Spellcasting.Gather(sim, CasterX, CasterY, CasterZ, Joules);
        if (!(joules > 0.0)) return;
        double scale = SpeedMps / length;
        sim.Projectiles.Add(new Projectile
        {
            X = CasterX + 0.5, Y = CasterY + 0.5, Z = CasterZ + 0.5,
            VX = DirX * scale, VY = DirY * scale, VZ = DirZ * scale,
            Joules = joules,
        });
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(CasterX); writer.Write(CasterY); writer.Write(CasterZ);
        writer.Write(DirX); writer.Write(DirY); writer.Write(DirZ);
        writer.Write(Joules);
    }

    internal static CastFireball ReadFrom(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
}

/// <summary>
/// Conjures matter: up to <paramref name="Kg"/> of a material, crystallised from ether the caster
/// gathers, into the target tile. Once made it is ordinary matter, indistinguishable from any other.
/// </summary>
public sealed record CastConjure(int CasterX, int CasterY, int CasterZ, int TargetX, int TargetY, int TargetZ, ushort Material, double Kg) : WorldCommand
{
    internal const byte TagValue = 8;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        if (!sim.Grid.InBounds(TargetX, TargetY, TargetZ)) return;
        Spellcasting.Conjure(sim, CasterX, CasterY, CasterZ, sim.Grid.Index(TargetX, TargetY, TargetZ), Material, Kg);
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(CasterX); writer.Write(CasterY); writer.Write(CasterZ);
        writer.Write(TargetX); writer.Write(TargetY); writer.Write(TargetZ);
        writer.Write(Material);
        writer.Write(Kg);
    }

    internal static CastConjure ReadFrom(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadUInt16(), r.ReadDouble());
}
