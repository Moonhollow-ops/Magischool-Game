using System;

namespace Magischool.Sim.Perception;

/// <summary>
/// Walks the tiles a straight segment passes through, in order, and how far it runs in each
/// (Amanatides and Woo's voxel traversal). Positions are in tiles: a tile's centre is at its
/// index + 0.5. Tiles off the grid are walked like any other; the caller decides what they are.
/// </summary>
internal struct RayWalk
{
    private int _x, _y, _z;
    private readonly int _sx, _sy, _sz;
    private double _tx, _ty, _tz;
    private readonly double _dx, _dy, _dz;
    private double _t;
    private readonly double _length;

    public RayWalk(double ax, double ay, double az, double bx, double by, double bz)
    {
        double vx = bx - ax, vy = by - ay, vz = bz - az;
        _length = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        _x = (int)Math.Floor(ax); _y = (int)Math.Floor(ay); _z = (int)Math.Floor(az);
        _sx = Math.Sign(vx); _sy = Math.Sign(vy); _sz = Math.Sign(vz);
        // Parameter t runs 0 to 1 along the segment; _tx is where it next crosses an x boundary.
        _dx = vx != 0.0 ? Math.Abs(1.0 / vx) : double.PositiveInfinity;
        _dy = vy != 0.0 ? Math.Abs(1.0 / vy) : double.PositiveInfinity;
        _dz = vz != 0.0 ? Math.Abs(1.0 / vz) : double.PositiveInfinity;
        _tx = vx > 0.0 ? (_x + 1 - ax) * _dx : vx < 0.0 ? (ax - _x) * _dx : double.PositiveInfinity;
        _ty = vy > 0.0 ? (_y + 1 - ay) * _dy : vy < 0.0 ? (ay - _y) * _dy : double.PositiveInfinity;
        _tz = vz > 0.0 ? (_z + 1 - az) * _dz : vz < 0.0 ? (az - _z) * _dz : double.PositiveInfinity;
        _t = 0.0;
    }

    /// <summary>Length of the whole segment, tiles.</summary>
    public double Length => _length;

    /// <summary>
    /// The next tile and the length of segment inside it, tiles. False once the segment has ended.
    /// The first tile is the one the segment starts in, the last the one it ends in.
    /// </summary>
    public bool Next(out int x, out int y, out int z, out double inside, out bool last)
    {
        x = _x; y = _y; z = _z;
        if (_t >= 1.0) { inside = 0.0; last = true; return false; }
        double exit = Math.Min(_tx, Math.Min(_ty, _tz));
        last = exit >= 1.0;
        double end = last ? 1.0 : exit;
        inside = (end - _t) * _length;
        _t = end;
        if (!last)
        {
            if (_tx <= _ty && _tx <= _tz) { _x += _sx; _tx += _dx; }
            else if (_ty <= _tz) { _y += _sy; _ty += _dy; }
            else { _z += _sz; _tz += _dz; }
        }
        return true;
    }
}
