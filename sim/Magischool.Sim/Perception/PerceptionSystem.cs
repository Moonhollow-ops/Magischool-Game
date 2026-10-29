using System;
using System.Collections.Generic;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;

namespace Magischool.Sim.Perception;

/// <summary>
/// Layer 10: bodies see with their eyes and hear with their ears, and remember what they noticed
/// (<see cref="Body.Percepts"/>). Nothing is noticed by a rule; it is noticed because enough light
/// or sound reached a working sense organ to stand out from what that organ was already taking in.
///
/// <para>
/// Sight. Light comes from flames, glowing matter, bolts and the sky (<see cref="LightField"/>). A
/// thing is seen when the light it sends to the eye stands out from the eye's adaptation: its
/// luminance contrast must pass the eye's threshold, which is Weber's 1.5% in daylight and rises
/// as the square root of the dark below (de Vries–Rose), and a target smaller than the eye's
/// summation area (Ricco's: a fifth of a degree by day, a degree in the dark) needs that much
/// more. A body or a wall shows the light falling on it (a reflectance of 0.3, standing out from
/// what is behind it by half of that); a flame, an ember or a bolt shows its own luminance. Smoke
/// and murky water in the way dim it. Eyes look the way the body faces, 100° to either side, and
/// adapt to their surroundings: to brightness within a second, to darkness over ten minutes and
/// more, so a body that walks out of a lit room into the night is blind at first. Asleep or
/// unconscious, the eyes are shut.
/// </para>
/// <para>
/// Hearing. Sounds made in the tick (<see cref="Simulation.Sounds"/>, and the fires burning)
/// reach the ear by the paths in <see cref="Acoustics"/>. A sound is heard when, in some band, it
/// rises above both the threshold of hearing and the background of everything else heard at the
/// same time, so a fire masks a footstep. Each ear adds what it is worth: with one ear the
/// threshold is 3 dB higher (which matters most in silence), with none nothing is heard. A sleeper hears too, and a sound of 55 dB(A) standing 10 dB over
/// the background wakes them.
/// </para>
/// </summary>
public sealed class PerceptionSystem : ISimSystem
{
    /// <summary>How many things a body remembers noticing; past this the longest unseen is forgotten.</summary>
    public const int MemoryCapacity = 48;

    /// <summary>Reflectance of skin, cloth, stone and the like: the share of light falling on them they send back.</summary>
    public const double Reflectance = 0.3;

    /// <summary>How much a body or a thing stands out from what is behind it, as a share of its luminance.</summary>
    public const double TargetContrast = 0.5;

    /// <summary>The faintest the eye adapts to, cd/m²: fully dark-adapted, past half an hour in the dark.</summary>
    public const double DarkestAdaptation = 1e-6;

    /// <summary>Time constants of adapting to brighter and to darker surroundings, s (in the logarithm of luminance).</summary>
    public const double LightAdaptSeconds = 1.0, DarkAdaptSeconds = 400.0;

    /// <summary>How far to either side of where it faces a body sees, degrees.</summary>
    public const double HalfFieldOfViewDeg = 100.0;

    /// <summary>A sleeper wakes to a sound this loud, dB(A), that stands out this far over the background, dB.</summary>
    public const double WakeDbA = 55.0, WakeOverBackgroundDb = 10.0;

    /// <summary>Farthest round the ear the air is searched for ways round walls, tiles.</summary>
    public const int MaxHearingRadius = 96;

    public string Name => "perception";

    private WorldGrid? _gridFor;
    private List<LightSource>[] _chunkLights = Array.Empty<List<LightSource>>();
    private readonly List<LightSource> _lights = new();
    private readonly List<SoundEvent> _sounds = new();
    private readonly Dictionary<int, double> _lux = new();
    private readonly Dictionary<int, SoundRegion> _regions = new();

    /// <summary>The light sources in the world as of the last tick, for display.</summary>
    public IReadOnlyList<LightSource> Lights => _lights;

    public void Update(Simulation sim)
    {
        bool anyone = false;
        foreach (var body in sim.Bodies)
            anyone |= body.Alive && !body.IsRemains;
        if (!anyone) return;

        GatherLights(sim);
        GatherSounds(sim);
        _lux.Clear();
        foreach (var body in sim.Bodies)
        {
            if (!body.Alive || body.IsRemains) continue;
            Look(sim, body);
            Listen(sim, body);
        }
    }

    // ---------------------------------------------------------------- what there is to see and hear

    private void GatherLights(Simulation sim)
    {
        var grid = sim.Grid;
        var activity = sim.Activity;
        bool fresh = !ReferenceEquals(_gridFor, grid) || _chunkLights.Length != activity.Count;
        if (fresh)
        {
            _gridFor = grid;
            _chunkLights = new List<LightSource>[activity.Count];
            for (int c = 0; c < activity.Count; c++) _chunkLights[c] = new List<LightSource>();
        }
        // A sleeping chunk doesn't change, so its lights are as they were when it went to sleep.
        for (int c = 0; c < activity.Count; c++)
        {
            if (!fresh && !activity.Awake[c]) continue;
            var (x0, y0, z0, x1, y1, z1) = activity.Bounds(c);
            _chunkLights[c].Clear();
            LightField.Gather(grid, x0, y0, z0, x1, y1, z1, _chunkLights[c]);
        }
        _lights.Clear();
        foreach (var list in _chunkLights) _lights.AddRange(list);
        LightField.GatherBolts(sim, _lights);
    }

    /// <summary>The tick's sounds, with the noise of each chunk's fires as one sound from its loudest flame.</summary>
    private void GatherSounds(Simulation sim)
    {
        _sounds.Clear();
        _sounds.AddRange(sim.Sounds);
        double perWatt = 4.0 * Math.PI / (LightField.FlameRadiantFraction * Photometry.Efficacy(LightField.FlameSootK));
        int chunk = -1;
        double joules = 0.0, loudest = 0.0;
        LightSource at = default;
        void Flush()
        {
            if (chunk >= 0 && joules > 0.0) _sounds.Add(new SoundEvent(SoundKind.Fire, at.X, at.Y, at.Z, joules, chunk));
        }
        foreach (var light in _lights)
        {
            if (light.Kind != LightKind.Flame) continue;
            var (x, y, z) = sim.Grid.Coordinates(light.Tile);
            int c = sim.Activity.ChunkAt(x, y, z);
            if (c != chunk) { Flush(); chunk = c; joules = 0.0; loudest = 0.0; }
            double j = Acoustics.FireNoiseEfficiency * light.Candela * perWatt * Simulation.SecondsPerTick;
            joules += j;
            if (j > loudest) { loudest = j; at = light; }
        }
        Flush();
    }

    private double Lux(Simulation sim, int x, int y, int z)
    {
        var grid = sim.Grid;
        if (!grid.InBounds(x, y, z)) return 0.0;
        int i = grid.Index(x, y, z);
        if (_lux.TryGetValue(i, out double lux)) return lux;
        lux = LightField.Illuminance(sim, _lights, x + 0.5, y + 0.5, z + 0.5);
        _lux[i] = lux;
        return lux;
    }

    // ---------------------------------------------------------------- sight

    /// <summary>
    /// The smallest luminance difference an eye adapted to <paramref name="adaptation"/> cd/m²
    /// makes out on a target filling <paramref name="solidAngle"/> sr, cd/m².
    /// </summary>
    public static double ThresholdLuminance(double adaptation, double solidAngle)
    {
        double l = Math.Max(adaptation, DarkestAdaptation);
        double contrast = 0.015 * (1.0 + Math.Sqrt(0.1 / l));
        double ricco = (0.2 + 0.8 / (1.0 + l / 0.01)) * Math.PI / 180.0;
        double size = solidAngle > 0.0 ? Math.Max(1.0, ricco * ricco / solidAngle) : double.PositiveInfinity;
        return l * contrast * size;
    }

    /// <summary>The tile a body sees from: its best eye's, or its head's if it has none.</summary>
    public static int EyeDz(Body body)
    {
        int dz = body.HeightTiles - 1;
        double best = -1.0;
        for (int p = 0; p < body.Anatomy.Parts.Length; p++)
        {
            if (body.Anatomy.Parts[p].Function != Bodies.PartFunction.Eye) continue;
            double h = body.Health(p);
            if (h > best) { best = h; dz = body.Anatomy.Parts[p].Dz; }
        }
        return dz;
    }

    private static int EarDz(Body body)
    {
        int dz = body.HeightTiles - 1;
        double best = -1.0;
        for (int p = 0; p < body.Anatomy.Parts.Length; p++)
        {
            if (body.Anatomy.Parts[p].Function != Bodies.PartFunction.Ear) continue;
            double h = body.Health(p);
            if (h > best) { best = h; dz = body.Anatomy.Parts[p].Dz; }
        }
        return dz;
    }

    /// <summary>Whether a body facing (fx, fy) has a point (dx, dy) away from it within its field of view.</summary>
    public static bool InView(int fx, int fy, double dx, double dy) => InView(fx, fy, dx, dy, HalfFieldOfViewDeg);

    /// <summary>The same, for eyes that see <paramref name="halfFieldDeg"/> to either side (a rabbit's nearly all round).</summary>
    public static bool InView(int fx, int fy, double dx, double dy, double halfFieldDeg)
    {
        if (halfFieldDeg >= 180.0) return true;
        double f = Math.Sqrt(fx * fx + fy * fy), d = Math.Sqrt(dx * dx + dy * dy);
        if (f == 0.0 || d < 1e-9) return true;
        return (fx * dx + fy * dy) / (f * d) >= Math.Cos(halfFieldDeg * Math.PI / 180.0);
    }

    private void Look(Simulation sim, Body body)
    {
        var grid = sim.Grid;
        int eyeZ = body.Z + EyeDz(body);
        double ex = body.X + 0.5, ey = body.Y + 0.5, ez = eyeZ + 0.5;
        double surround = Reflectance * Lux(sim, body.X, body.Y, eyeZ) / Math.PI;
        double vision = body.Vision;
        // Shut eyes see nothing, and settle toward the dark.
        Adapt(body, vision > 0.0 ? surround : 0.0);
        if (!(vision > 0.0)) return;
        double adaptation = Math.Max(body.Adaptation, surround);

        foreach (var other in sim.Bodies)
        {
            if (ReferenceEquals(other, body)) continue;
            if (!InView(body.FacingX, body.FacingY, other.X + 0.5 - ex, other.Y + 0.5 - ey, body.Plan.HalfFieldOfViewDeg)) continue;
            double signal = 0.0, solidAngle = 0.0;
            for (int dz = 0; dz < other.HeightTiles; dz++)
            {
                double area = 0.0;
                foreach (var part in other.Anatomy.Parts)
                    if (!part.Internal && part.Dz == dz) area += part.SkinAreaM2;
                // A convex body shows a quarter of its surface on average (Cauchy).
                area *= 0.25;
                if (!(area > 0.0)) continue;
                int tz = other.Z + dz;
                double t = LightField.Transmittance(grid, ex, ey, ez, other.X + 0.5, other.Y + 0.5, tz + 0.5);
                if (!(t > 0.0)) continue;
                double dx = other.X - body.X, dy = other.Y - body.Y, dzz = tz - eyeZ;
                double metres = Math.Max(0.25, Math.Sqrt(dx * dx + dy * dy + dzz * dzz) * WorldGrid.TileSizeM);
                double omega = area / (metres * metres);
                double luminance = Reflectance * Lux(sim, other.X, other.Y, tz) / Math.PI;
                signal += TargetContrast * luminance * t * omega;
                solidAngle += omega;
            }
            if (!(solidAngle > 0.0)) continue;
            double seen = vision * (signal / solidAngle) / ThresholdLuminance(adaptation, solidAngle);
            if (seen >= 1.0)
                Remember(sim, body, PerceptKind.Body, 0, other.Id, other.X, other.Y, other.Z, seen, signal / solidAngle / TargetContrast);
        }

        // Lights: the clearest of each bolt, and of the flames and the glowing matter in each chunk.
        _best.Clear();
        foreach (var light in _lights)
        {
            double dx = light.X - ex, dy = light.Y - ey, dz = light.Z - ez;
            if (!InView(body.FacingX, body.FacingY, dx, dy, body.Plan.HalfFieldOfViewDeg)) continue;
            double cd = LightField.IntensityToward(light, ex, ey, ez, out double metres);
            if (!(cd > 0.0)) continue;
            // A face seen at a slant shows less of itself, and looks just as bright.
            double shown = light.Face < 0 ? light.AreaM2 : light.AreaM2 * cd / light.Candela;
            double omega = shown / (metres * metres);
            double best = vision * light.Luminance / ThresholdLuminance(adaptation, omega);
            if (best < 1.0) continue;
            var kind = light.Kind == LightKind.Bolt ? PerceptKind.Bolt : light.Kind == LightKind.Flame ? PerceptKind.Fire : PerceptKind.Glow;
            long subject = light.Subject;
            int tx = (int)Math.Floor(light.X), ty = (int)Math.Floor(light.Y), tz = (int)Math.Floor(light.Z);
            if (kind != PerceptKind.Bolt) subject = sim.Activity.ChunkAt(Math.Clamp(tx, 0, grid.Width - 1), Math.Clamp(ty, 0, grid.Height - 1), Math.Clamp(tz, 0, grid.Depth - 1));
            var key = (kind, subject);
            if (_best.TryGetValue(key, out var had) && had.Seen >= best) continue;
            double t = LightField.Transmittance(grid, ex, ey, ez, light.X, light.Y, light.Z);
            double seen = best * t;
            if (seen < 1.0 || (_best.TryGetValue(key, out had) && had.Seen >= seen)) continue;
            _best[key] = (seen, light.Luminance * t, tx, ty, tz);
        }
        foreach (var ((kind, subject), (seen, level, x, y, z)) in SortedBest())
            Remember(sim, body, kind, 0, subject, x, y, z, seen, level);
    }

    private readonly Dictionary<(PerceptKind, long), (double Seen, double Level, int X, int Y, int Z)> _best = new();

    /// <summary>The clearest sights of this look, in a fixed order so memory fills the same way every run.</summary>
    private List<KeyValuePair<(PerceptKind, long), (double Seen, double Level, int X, int Y, int Z)>> SortedBest()
    {
        var list = new List<KeyValuePair<(PerceptKind, long), (double, double, int, int, int)>>(_best);
        list.Sort((a, b) => a.Key.Item1 != b.Key.Item1 ? a.Key.Item1.CompareTo(b.Key.Item1) : a.Key.Item2.CompareTo(b.Key.Item2));
        return list;
    }

    /// <summary>The eyes settle toward the luminance around them, quickly toward light and slowly toward dark.</summary>
    private static void Adapt(Body body, double surround)
    {
        double target = Math.Max(surround, DarkestAdaptation);
        if (!(body.Adaptation > 0.0)) { body.Adaptation = target; return; }
        double tau = target > body.Adaptation ? LightAdaptSeconds : DarkAdaptSeconds;
        double k = 1.0 - Math.Exp(-Simulation.SecondsPerTick / tau);
        body.Adaptation = Math.Exp(Math.Log(body.Adaptation) + (Math.Log(target) - Math.Log(body.Adaptation)) * k);
    }

    // ---------------------------------------------------------------- hearing

    private readonly List<(int Event, double R)> _candidates = new();
    private double[] _intensity = Array.Empty<double>();

    private void Listen(Simulation sim, Body body)
    {
        double hearing = body.Hearing;
        if (!(hearing > 0.0) || _sounds.Count == 0) return;
        var grid = sim.Grid;
        double loss = -10.0 * Math.Log10(hearing);
        int earZ = Math.Clamp(body.Z + EarDz(body), 0, grid.Depth - 1);
        double ex = body.X + 0.5, ey = body.Y + 0.5, ez = earZ + 0.5;
        const int bands = Acoustics.Bands;
        double dt = Simulation.SecondsPerTick;

        // Sounds too faint to be heard even in the open with the best echo are passed over.
        _candidates.Clear();
        int reach = 0;
        for (int e = 0; e < _sounds.Count; e++)
        {
            var s = _sounds[e];
            if (s.Kind == SoundKind.Footstep && s.Subject == body.Id) continue;
            double dx = s.X - ex, dy = s.Y - ey, dz = s.Z - ez;
            double r = Math.Max(0.5, Math.Sqrt(dx * dx + dy * dy + dz * dz) * WorldGrid.TileSizeM);
            var spectrum = Acoustics.Spectrum(s.Kind);
            bool maybe = false;
            for (int b = 0; b < bands; b++)
            {
                double open = Acoustics.ToDb(s.Joules * spectrum[b] / dt / (4.0 * Math.PI * r * r));
                if (open + 30.0 > Acoustics.HearingThresholdDb[b] + loss) maybe = true;
            }
            if (!maybe) continue;
            _candidates.Add((e, r));
            reach = Math.Max(reach, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))));
        }
        if (_candidates.Count == 0) return;

        if (!_regions.TryGetValue(body.Id, out var region)) _regions[body.Id] = region = new SoundRegion();
        // Out far enough for every sound worth hearing, and on until the room the ear is in is
        // closed in or the search reaches its limit; an ear that hasn't moved starts from last time's.
        int radius = Math.Clamp(reach + 8, 8, MaxHearingRadius);
        if (region.EarX == body.X && region.EarY == body.Y && region.EarZ == earZ) radius = Math.Max(radius, region.Radius);
        region.Build(grid, body.X, body.Y, earZ, radius);
        // A space already a quarter open to the sky holds no echo, however far it goes.
        while (!region.Bounded && radius < MaxHearingRadius && region.OpenM2 < 0.25 * region.SurfaceM2)
        {
            radius = Math.Min(MaxHearingRadius, radius * 2);
            region.Build(grid, body.X, body.Y, earZ, radius);
        }
        Span<double> roomConstant = stackalloc double[bands];
        Span<double> reverberation = stackalloc double[bands];
        double enclosure = region.Enclosure;
        for (int b = 0; b < bands; b++)
        {
            roomConstant[b] = region.RoomConstant(b);
            reverberation[b] = region.ReverberationSeconds(b);
        }

        if (_intensity.Length < _candidates.Count * bands) _intensity = new double[_candidates.Count * bands];
        Span<double> total = stackalloc double[bands];
        Span<double> walls = stackalloc double[bands];
        for (int c = 0; c < _candidates.Count; c++)
        {
            var (e, r) = _candidates[c];
            var s = _sounds[e];
            var spectrum = Acoustics.Spectrum(s.Kind);
            bool clear = WallLoss(grid, ex, ey, ez, s.X, s.Y, s.Z, walls);
            int sx = (int)Math.Floor(s.X), sy = (int)Math.Floor(s.Y), sz = (int)Math.Floor(s.Z);
            double around = region.DistanceTo(sx, sy, sz) * WorldGrid.TileSizeM;
            bool sameAir = !double.IsPositiveInfinity(around);
            for (int b = 0; b < bands; b++)
            {
                double watts = s.Joules * spectrum[b] / dt;
                double air = Math.Pow(10.0, -Acoustics.AirAbsorptionDbPerM[b] * r / 10.0);
                double direct = watts / (4.0 * Math.PI * r * r) * air;
                double heard;
                if (clear) heard = direct;
                else
                {
                    heard = direct * Math.Pow(10.0, -walls[b] / 10.0);
                    if (sameAir)
                    {
                        // Maekawa: a detour of δ round an edge costs 10·log10(3 + 20N), N = 2δ/λ.
                        double g = Math.Max(around, r);
                        double fresnel = 2.0 * (g - r) * Acoustics.BandHz[b] / Acoustics.SpeedOfSound;
                        double diffraction = Math.Min(Acoustics.MaxDiffractionDb, 10.0 * Math.Log10(3.0 + 20.0 * fresnel));
                        double bent = watts / (4.0 * Math.PI * g * g) * Math.Pow(10.0, -(Acoustics.AirAbsorptionDbPerM[b] * g + diffraction) / 10.0);
                        heard = Math.Max(heard, bent);
                    }
                }
                // The echo, less what has died away by the time sound has come this far (Barron's revised theory).
                if (sameAir && enclosure > 0.0 && !double.IsPositiveInfinity(roomConstant[b]) && reverberation[b] > 0.0)
                    heard += enclosure * 4.0 * watts / roomConstant[b] * Math.Exp(-13.82 * Math.Max(around, r) / (Acoustics.SpeedOfSound * reverberation[b]));
                _intensity[c * bands + b] = heard;
                total[b] += heard;
            }
        }

        for (int c = 0; c < _candidates.Count; c++)
        {
            var s = _sounds[_candidates[c].Event];
            double margin = double.NegativeInfinity, weighted = 0.0, weightedBackground = 0.0;
            for (int b = 0; b < bands; b++)
            {
                double heard = _intensity[c * bands + b];
                double background = total[b] - heard + Acoustics.FromDb(Acoustics.QuietBackgroundDb[b]);
                double floor = Acoustics.FromDb(Acoustics.HearingThresholdDb[b] + loss) + background;
                margin = Math.Max(margin, Acoustics.ToDb(heard) - Acoustics.ToDb(floor));
                double a = Math.Pow(10.0, Acoustics.AWeightDb[b] / 10.0);
                weighted += heard * a;
                weightedBackground += background * a;
            }
            if (!(margin > 0.0)) continue;
            double dba = Acoustics.ToDb(weighted);
            if (body.Asleep)
            {
                if (dba < WakeDbA || dba - Acoustics.ToDb(weightedBackground) < WakeOverBackgroundDb) continue;
                body.Asleep = false;
            }
            int tx = Math.Clamp((int)Math.Floor(s.X), 0, grid.Width - 1);
            int ty = Math.Clamp((int)Math.Floor(s.Y), 0, grid.Height - 1);
            int tz = Math.Clamp((int)Math.Floor(s.Z), 0, grid.Depth - 1);
            Remember(sim, body, PerceptKind.Sound, (byte)s.Kind, s.Subject, tx, ty, tz, margin, dba);
        }
    }

    /// <summary>
    /// Sound reduction in each band of the walls on the straight line from a to b, dB: each run of
    /// solid tiles is one wall, its mass per square metre the mass met along the line through it.
    /// True when nothing is in the way.
    /// </summary>
    private static bool WallLoss(WorldGrid grid, double ax, double ay, double az, double bx, double by, double bz, Span<double> loss)
    {
        loss.Clear();
        var walk = new RayWalk(ax, ay, az, bx, by, bz);
        bool first = true, clear = true;
        double run = 0.0;
        while (walk.Next(out int x, out int y, out int z, out double inside, out bool last))
        {
            bool wall = !first && !last && grid.InBounds(x, y, z) && Acoustics.Blocks(grid, grid.Index(x, y, z));
            first = false;
            if (wall)
            {
                int i = grid.Index(x, y, z);
                run += grid.Mass[i] / WorldGrid.TileVolumeM3 * inside * WorldGrid.TileSizeM;
                clear = false;
                continue;
            }
            if (run > 0.0)
            {
                for (int b = 0; b < loss.Length; b++) loss[b] += Acoustics.MassLawDb(run, Acoustics.BandHz[b]);
                run = 0.0;
            }
        }
        if (run > 0.0)
            for (int b = 0; b < loss.Length; b++) loss[b] += Acoustics.MassLawDb(run, Acoustics.BandHz[b]);
        return clear;
    }

    // ---------------------------------------------------------------- memory

    private static void Remember(Simulation sim, Body body, PerceptKind kind, byte detail, long subject,
        int x, int y, int z, double strength, double level)
    {
        var memory = body.Percepts;
        for (int k = 0; k < memory.Count; k++)
        {
            var p = memory[k];
            if (p.Kind != kind || p.Detail != detail || p.Subject != subject) continue;
            p.X = x; p.Y = y; p.Z = z;
            p.LastTick = sim.Tick;
            p.Strength = strength;
            p.Peak = Math.Max(p.Peak, strength);
            p.Level = level;
            memory[k] = p;
            return;
        }
        if (memory.Count >= MemoryCapacity)
        {
            int stalest = 0;
            for (int k = 1; k < memory.Count; k++)
                if (memory[k].LastTick < memory[stalest].LastTick) stalest = k;
            memory.RemoveAt(stalest);
        }
        memory.Add(new Percept
        {
            Kind = kind, Detail = detail, Subject = subject, X = x, Y = y, Z = z,
            FirstTick = sim.Tick, LastTick = sim.Tick, Strength = strength, Peak = strength, Level = level,
        });
    }
}
