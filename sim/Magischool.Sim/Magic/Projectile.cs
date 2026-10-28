namespace Magischool.Sim.Magic;

/// <summary>
/// A bolt of a spell in flight, such as a fireball: a packet of energy moving in a straight line
/// until it strikes something solid or liquid. Position is in tiles (a tile's centre is at its
/// index + 0.5), velocity in m/s.
/// </summary>
public struct Projectile
{
    public double X, Y, Z;
    public double VX, VY, VZ;
    /// <summary>Energy it carries, J, all of it heat where it strikes.</summary>
    public double Joules;
    /// <summary>Which bolt it is, so a witness can follow it.</summary>
    public long Id;
    /// <summary>Seconds it has flown; past <see cref="SpellSystem.MaxFlightSeconds"/> it gutters out where it is.</summary>
    public double Age;
}
