using System.IO;

namespace Magischool.Sim.Perception;

/// <summary>What a percept is of. Append new kinds at the end: memories store them by number.</summary>
public enum PerceptKind : byte
{
    /// <summary>Saw a body (a person, or remains). Subject: its id.</summary>
    Body,
    /// <summary>Saw a spell's bolt in flight. Subject: the bolt's id.</summary>
    Bolt,
    /// <summary>Saw flames. Subject: the chunk they burn in.</summary>
    Fire,
    /// <summary>Saw something glowing with heat. Subject: the chunk it is in.</summary>
    Glow,
    /// <summary>Heard a sound, of the <see cref="SoundKind"/> in <see cref="Percept.Detail"/>.</summary>
    Sound,
}

/// <summary>
/// One thing a body has noticed, and when: what it was, where it seemed to be, and how clearly.
/// Seeing or hearing the same thing again updates it. This is the raw stuff awareness and
/// suspicion are built from: a witness who saw a bolt fly saw magic.
/// </summary>
public struct Percept
{
    public PerceptKind Kind;
    /// <summary>For a sound, its <see cref="SoundKind"/>.</summary>
    public byte Detail;
    /// <summary>Which thing it was (see <see cref="PerceptKind"/>).</summary>
    public long Subject;
    /// <summary>The tile it was in, the last time.</summary>
    public int X, Y, Z;
    public long FirstTick, LastTick;
    /// <summary>
    /// How clearly, the last time: for sight, how many times over the eye's threshold it stood out
    /// (1 just made out, 10 plain); for sound, dB above what could just be heard over the background.
    /// </summary>
    public double Strength;
    /// <summary>The most clearly it was ever noticed, on the same scale.</summary>
    public double Peak;
    /// <summary>For a sound, how loud it was at the ear, dB(A); for sight, the luminance seen, cd/m².</summary>
    public double Level;

    internal void Write(BinaryWriter w)
    {
        w.Write((byte)Kind); w.Write(Detail); w.Write(Subject);
        w.Write(X); w.Write(Y); w.Write(Z);
        w.Write(FirstTick); w.Write(LastTick);
        w.Write(Strength); w.Write(Peak); w.Write(Level);
    }

    internal static Percept Read(BinaryReader r) => new()
    {
        Kind = (PerceptKind)r.ReadByte(), Detail = r.ReadByte(), Subject = r.ReadInt64(),
        X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(),
        FirstTick = r.ReadInt64(), LastTick = r.ReadInt64(),
        Strength = r.ReadDouble(), Peak = r.ReadDouble(), Level = r.ReadDouble(),
    };
}
