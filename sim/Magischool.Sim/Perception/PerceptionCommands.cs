using System;
using System.IO;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;

namespace Magischool.Sim.Perception;

/// <summary>A body turns to face a way (DX and DY each -1, 0 or 1) without stepping. Only a conscious body turns.</summary>
public sealed record FaceBody(int Id, int DX, int DY) : WorldCommand
{
    internal const byte TagValue = 15;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim)
    {
        var body = SpawnBody.Find(sim, Id);
        int dx = Math.Clamp(DX, -1, 1), dy = Math.Clamp(DY, -1, 1);
        if (body == null || !body.Conscious || (dx == 0 && dy == 0)) return;
        body.FacingX = dx;
        body.FacingY = dy;
    }

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(Id); writer.Write(DX); writer.Write(DY);
    }

    internal static FaceBody ReadFrom(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
}

/// <summary>
/// Sets the light falling from the open sky, lux (see <see cref="Simulation.SkyIlluminanceLux"/>).
/// A stand-in for the sun and moon until world time turns the day.
/// </summary>
public sealed record SetSkyLight(double Lux) : WorldCommand
{
    internal const byte TagValue = 16;
    internal override byte Tag => TagValue;

    public override void Apply(Simulation sim) => sim.SkyIlluminanceLux = Math.Max(0.0, Lux);

    internal override void Write(BinaryWriter writer)
    {
        writer.Write(Tag);
        writer.Write(Lux);
    }

    internal static SetSkyLight ReadFrom(BinaryReader r) => new(r.ReadDouble());
}
