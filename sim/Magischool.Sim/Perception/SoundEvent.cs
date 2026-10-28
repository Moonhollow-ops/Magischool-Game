namespace Magischool.Sim.Perception;

/// <summary>What made a sound. Append new kinds at the end: memories store them by number.</summary>
public enum SoundKind : byte
{
    /// <summary>The roar and crackle of flames.</summary>
    Fire,
    /// <summary>A body's foot coming down.</summary>
    Footstep,
    /// <summary>Something heavy striking the ground or a body: falling rubble, a body landing.</summary>
    Thud,
    /// <summary>A bond in a structure breaking.</summary>
    Crack,
    /// <summary>A solid shattering all at once (a crystal rupturing).</summary>
    Shatter,
    /// <summary>Heat suddenly released into the air: a fireball striking.</summary>
    Blast,
    /// <summary>A bolt rushing through the air.</summary>
    Whoosh,
}

/// <summary>
/// One sound made during a tick: where (in tiles), what made it, and the acoustic energy it
/// radiated this tick, J. Subject says which thing made it (a body's id, a bolt's id, or the
/// tile), so a listener who keeps hearing it knows it is the same.
/// </summary>
public readonly record struct SoundEvent(SoundKind Kind, double X, double Y, double Z, double Joules, long Subject);
