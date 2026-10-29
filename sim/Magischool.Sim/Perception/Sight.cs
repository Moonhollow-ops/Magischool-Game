using System;
using System.Collections.Generic;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;

namespace Magischool.Sim.Perception;

/// <summary>
/// What a body can make out of the world around it, tile by tile, by the same eye the perception
/// layer uses for bodies: light falling on a tile, sent back at <see cref="PerceptionSystem.Reflectance"/>
/// and standing out by <see cref="PerceptionSystem.TargetContrast"/>, must pass the eye's threshold
/// at its adaptation, through whatever lies between. Unlike that layer it looks all round, as a
/// player turns their head. A tile that glows or burns is lit by its own light. Sight tells what
/// a thing looks like, never its temperature or what is in the air.
/// </summary>
public static class Sight
{
    /// <summary>How far in front of a tile's face the eye's line ends, tiles.</summary>
    private const double Inset = 0.02;

    /// <summary>Area a tile shows the eye, m²: one face.</summary>
    private const double TileFaceM2 = WorldGrid.TileSizeM * WorldGrid.TileSizeM;

    /// <summary>Whether the body's eyes are open and working.</summary>
    public static bool EyesOpen(Body body) => !body.IsRemains && body.Conscious && !body.Asleep && body.Vision > 0.0;

    /// <summary>
    /// Marks in <paramref name="seen"/> (one per tile of the level, row by row) the tiles of level
    /// <paramref name="z"/> the body can see now. <paramref name="lights"/> is
    /// <see cref="LightField.AllSources"/> for this tick. Looking down on a wall shows its top, not
    /// its foot, so a tile counts as seen when it or any tile of the same opaque column above it,
    /// up to the eye, is in sight.
    /// </summary>
    public static void Level(Simulation sim, IReadOnlyList<LightSource> lights, Body body, int z, bool[] seen)
    {
        var grid = sim.Grid;
        Array.Clear(seen);
        if (!EyesOpen(body) || z < 0 || z >= grid.Depth) return;
        var eye = new Eye(sim, lights, body);
        for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
        {
            bool sees = eye.Sees(x, y, z);
            for (int k = z; !sees && k < eye.Z && k + 1 < grid.Depth && LightField.IsOpaque(grid, grid.Index(x, y, k)); k++)
                sees = eye.Sees(x, y, k + 1);
            seen[y * grid.Width + x] = sees;
        }
    }

    /// <summary>Whether the body sees the tile itself (not the top of a column over it).</summary>
    public static bool Sees(Simulation sim, IReadOnlyList<LightSource> lights, Body body, int x, int y, int z) =>
        EyesOpen(body) && sim.Grid.InBounds(x, y, z) && new Eye(sim, lights, body).Sees(x, y, z);

    private readonly struct Eye
    {
        private readonly Simulation _sim;
        private readonly IReadOnlyList<LightSource> _lights;
        private readonly double _x, _y, _z, _adaptation, _vision;

        /// <summary>The level the eye is in.</summary>
        public int Z => (int)_z;

        public Eye(Simulation sim, IReadOnlyList<LightSource> lights, Body body)
        {
            _sim = sim;
            _lights = lights;
            int eyeZ = Math.Clamp(body.Z + PerceptionSystem.EyeDz(body), 0, sim.Grid.Depth - 1);
            (_x, _y, _z) = (body.X + 0.5, body.Y + 0.5, eyeZ + 0.5);
            double surround = PerceptionSystem.Reflectance * LightField.Illuminance(sim, lights, _x, _y, _z) / Math.PI;
            _adaptation = Math.Max(body.Adaptation, surround);
            _vision = body.Vision;
        }

        public bool Sees(int x, int y, int z)
        {
            // The eye meets a tile at the faces turned toward it: the line ends just in front of
            // the nearest point of them, so a wall seen along its length shows.
            double px = Face(_x, x), py = Face(_y, y), pz = Face(_z, z);
            double t = LightField.Transmittance(_sim.Grid, _x, _y, _z, px, py, pz, countLast: true);
            if (!(t > 0.0)) return false;
            double dx = px - _x, dy = py - _y, dz = pz - _z;
            double metres = Math.Max(0.25, Math.Sqrt(dx * dx + dy * dy + dz * dz) * WorldGrid.TileSizeM);
            double omega = TileFaceM2 / (metres * metres);
            double luminance = PerceptionSystem.Reflectance * LightField.Illuminance(_sim, _lights, px, py, pz) / Math.PI;
            double signal = PerceptionSystem.TargetContrast * luminance * t;
            return _vision * signal / PerceptionSystem.ThresholdLuminance(_adaptation, omega) >= 1.0;
        }

        /// <summary>Along one axis, the eye's coordinate if it is level with the tile, else just outside the tile's near side.</summary>
        private static double Face(double eye, int tile) =>
            eye < tile ? tile - Inset : eye > tile + 1 ? tile + 1 + Inset : eye;
    }
}
