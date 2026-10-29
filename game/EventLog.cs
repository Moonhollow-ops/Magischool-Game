using System;
using System.Collections.Generic;
using Godot;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Perception;

namespace Magischool.Game;

/// <summary>
/// The student's log: what happened to them and around them, tick by tick, as they would know
/// it. Their own body they always feel: a part burned, frozen or struck, a fall, bleeding,
/// blacking out, hunger and tiredness setting in. Everything else is logged only if they
/// noticed it (see <see cref="Percept"/>): a body coming into view, fire, a bolt, a crash, and
/// in the battle log the hurts of bodies they can see and where their own fireballs land. The
/// cause given is the one the student could tell: fire beside the part, a fireball bursting
/// close, the fall they just took.
/// </summary>
public partial class WorldView
{
    private enum LogKind { Event, Battle }

    private const int LogCapacity = 300;

    /// <summary>A part's hurt has to grow by this much (of the part) before the log says so again.</summary>
    private const double HurtStep = 0.05;

    /// <summary>Something noticed again after this long out of mind is logged again, ticks.</summary>
    private const long ForgetTicks = 30 * Simulation.TicksPerSecond;

    private readonly List<(long Tick, string Text, LogKind Kind)> _log = new();
    private bool _battleOnly;

    /// <summary>What each body was like when last logged: its parts' burns, frost and trauma, and its state.</summary>
    private readonly Dictionary<int, Known> _known = new();

    /// <summary>Bolts in flight last tick, where they were and whether the student cast them.</summary>
    private Dictionary<long, (double X, double Y, double Z, bool Mine)> _bolts = new();

    private long _nextBoltId = 1;

    /// <summary>Bolts that appear while this is set are the student's own.</summary>
    private bool _boltsAreMine;

    /// <summary>Where bolts burst this tick, to say what burned or struck someone.</summary>
    private readonly List<(double X, double Y, double Z, bool Mine)> _bursts = new();

    /// <summary>When each thing the student noticed was last noticed, by its percept's last tick.</summary>
    private readonly Dictionary<(PerceptKind, long, byte), long> _noticed = new();

    private sealed class Known
    {
        public readonly Dictionary<string, (double Burn, double Frost, double Trauma)> Parts = new();
        public bool Alive, Conscious, Asleep, Bleeding, Shivering;
        public int Hunger, Thirst, Fatigue;
        public int FallFrom = -1;
    }

    private void ForgetLog()
    {
        _log.Clear();
        _known.Clear();
        _bolts.Clear();
        _nextBoltId = _sim.NextProjectileId;
        _noticed.Clear();
        _boltsAreMine = false;
    }

    private void Log(string text, LogKind kind = LogKind.Event)
    {
        _log.Add((_sim.Tick, text, kind));
        if (_log.Count > LogCapacity) _log.RemoveAt(0);
    }

    /// <summary>Runs after every tick: notes what the student felt and noticed in it.</summary>
    private void Chronicle()
    {
        var me = Steered();
        TrackBolts(me);
        foreach (var body in _sim.Bodies)
        {
            bool mine = me != null && body.Id == me.Id;
            if (!_known.TryGetValue(body.Id, out var known))
            {
                _known[body.Id] = Remember(body, new Known());
                continue;
            }
            if (mine) FeelSelf(body, known);
            else if (me != null && Watching(me, body)) Watch(body, known);
            else Remember(body, known);
        }
        if (me != null) Notice(me);
    }

    // ---------------------------------------------------------------- bolts

    private void TrackBolts(Body? me)
    {
        _bursts.Clear();
        // Bolts made this tick: a fast one can be loosed and burst within the tick.
        var gone = new List<bool>();
        for (long id = _nextBoltId; id < _sim.NextProjectileId; id++)
            if (!_bolts.ContainsKey(id)) _bolts[id] = (double.NaN, 0, 0, _boltsAreMine);
        _nextBoltId = _sim.NextProjectileId;
        var now = new Dictionary<long, (double, double, double, bool)>();
        foreach (var bolt in _sim.Projectiles)
            now[bolt.Id] = (bolt.X, bolt.Y, bolt.Z, _bolts.TryGetValue(bolt.Id, out var was) && was.Mine);
        foreach (var (id, was) in _bolts)
            if (!now.ContainsKey(id)) gone.Add(was.Mine);
        _bolts = now;
        if (gone.Count == 0) return;

        // Where they burst is where the blasts were heard: pair each bolt gone with a blast.
        int k = 0;
        foreach (var sound in _sim.Sounds)
        {
            if (sound.Kind != SoundKind.Blast || k >= gone.Count) continue;
            bool mine = gone[k++];
            _bursts.Add((sound.X, sound.Y, sound.Z, mine));
            if (me == null) continue;
            var hit = BodyNear(sound.X, sound.Y, sound.Z, me);
            int x = (int)Math.Floor(sound.X), y = (int)Math.Floor(sound.Y), z = (int)Math.Floor(sound.Z);
            // Whether a body nearby was struck shows in its hurts, logged with their cause.
            string where = hit != null ? (hit.Id == me.Id ? "right beside you" : "beside " + Name(hit)) : "on " + WhatIsAt(x, y, z);
            if (mine) Log($"Your fireball bursts {where}.", LogKind.Battle);
            else if (Sees(x, y, z)) Log($"A fireball bursts {where}.", LogKind.Battle);
        }
    }

    /// <summary>The body nearest where a bolt burst, if one is within two tiles: the one it may have struck or scorched.</summary>
    private Body? BodyNear(double bx, double by, double bz, Body me)
    {
        Body? best = null;
        double bestD = 2.0;
        foreach (var body in _sim.Bodies)
        {
            double dz = Math.Max(0.0, Math.Max(body.Z - bz, bz - (body.Z + body.HeightTiles)));
            double dx = body.X + 0.5 - bx, dy = body.Y + 0.5 - by;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d < bestD) { bestD = d; best = body; }
        }
        return best;
    }

    private string WhatIsAt(int x, int y, int z)
    {
        var grid = _sim.Grid;
        if (!grid.InBounds(x, y, z)) return "the air";
        // A bolt bursts in front of what stopped it: name the solid it met if there is one beside.
        int i = grid.Index(x, y, z);
        if (grid.HasCondensed(i)) return "the " + _materials[grid.Material[i]].Name.ToLowerInvariant();
        foreach (var (dx, dy, dz) in Around)
        {
            if (!grid.InBounds(x + dx, y + dy, z + dz)) continue;
            int j = grid.Index(x + dx, y + dy, z + dz);
            if (grid.HasCondensed(j) && grid.FillFraction(j) >= 0.5) return "the " + _materials[grid.Material[j]].Name.ToLowerInvariant();
        }
        return "the air";
    }

    private static readonly (int, int, int)[] Around =
        { (0, 0, -1), (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1) };

    // ---------------------------------------------------------------- the student's own body

    private void FeelSelf(Body b, Known k)
    {
        if (k.Alive && !b.Alive) { Log("You die."); Remember(b, k); return; }
        if (!b.Alive) return;

        // Falls: from where the feet left the ground to where they land.
        if (b.FallSpeed > 0.0 && k.FallFrom < 0) k.FallFrom = b.Z;
        bool landed = k.FallFrom >= 0 && b.FallSpeed == 0.0;
        if (landed && k.FallFrom - b.Z >= 2) Log($"You fall {(k.FallFrom - b.Z) * WorldGrid.TileSizeM:0.#} m.");

        foreach (var (part, kind, level) in NewHurts(b, k))
            Log($"Your {part} {HurtWords(kind, level)}{Cause(b, part, kind, landed)}.", LogKind.Battle);
        if (landed) k.FallFrom = -1;

        if (k.Conscious != b.Conscious) Log(b.Conscious ? "You come to." : "You black out.");
        else if (k.Asleep != b.Asleep) Log(b.Asleep ? "You fall asleep." : "You wake.");
        bool bleeding = Bleeding(b);
        if (bleeding && !k.Bleeding) Log("You are bleeding.", LogKind.Battle);
        else if (!bleeding && k.Bleeding) Log("Your bleeding stops.");
        bool shivering = b.ShiverW > 1.0;
        if (shivering && !k.Shivering) Log("You start to shiver.");
        int hunger = Stage(b.Hunger), thirst = Stage(b.Thirst), fatigue = Stage(b.Fatigue, 0.5, 0.8);
        if (hunger > k.Hunger) Log(hunger == 1 ? "You are getting hungry." : "You are starving.");
        if (thirst > k.Thirst) Log(thirst == 1 ? "You are getting thirsty." : "You are parched.");
        if (fatigue > k.Fatigue) Log(fatigue == 1 ? "You are getting tired." : "You are exhausted.");
        k.Conscious = b.Conscious; k.Asleep = b.Asleep; k.Bleeding = bleeding; k.Shivering = shivering;
        k.Hunger = hunger; k.Thirst = thirst; k.Fatigue = fatigue;
    }

    private static int Stage(double v, double some = 0.35, double much = 0.7) => v > much ? 2 : v > some ? 1 : 0;

    private static bool Bleeding(Body b)
    {
        foreach (double rate in b.Bleed)
            if (rate > 1e-5) return true;
        return false;
    }

    /// <summary>What the student can tell hurt a part: fire beside it, a fireball bursting close, the fall.</summary>
    private string Cause(Body b, string partName, Hurt kind, bool fell)
    {
        if (kind == Hurt.Struck && fell) return " in the fall";
        foreach (var burst in _bursts)
        {
            double dx = b.X + 0.5 - burst.X, dy = b.Y + 0.5 - burst.Y;
            if (dx * dx + dy * dy <= 2.5 * 2.5)
                return burst.Mine ? " by your own fireball" : " by a fireball";
        }
        if (kind == Hurt.Burned && FireBeside(b, partName)) return " by the fire";
        return "";
    }

    private bool FireBeside(Body b, string partName)
    {
        var grid = _sim.Grid;
        int dz = 0;
        foreach (var part in b.Anatomy.Parts)
            if (part.Name == partName) { dz = part.Dz; break; }
        for (int z = b.Z + dz - 1; z <= b.Z + dz + 1; z++)
        for (int y = b.Y - 1; y <= b.Y + 1; y++)
        for (int x = b.X - 1; x <= b.X + 1; x++)
            if (grid.InBounds(x, y, z) && grid.Flame[grid.Index(x, y, z)] != 0) return true;
        return false;
    }

    // ---------------------------------------------------------------- other bodies

    /// <summary>Whether the student has just seen this body (within the last second).</summary>
    private bool Watching(Body me, Body other)
    {
        foreach (var p in me.Percepts)
            if (p.Kind == PerceptKind.Body && p.Subject == other.Id) return _sim.Tick - p.LastTick <= Simulation.TicksPerSecond;
        return false;
    }

    private void Watch(Body b, Known k)
    {
        string who = Name(b);
        foreach (var (part, kind, level) in NewHurts(b, k))
            Log($"{Capital(who)}'s {part} {HurtWords(kind, level)}{Cause(b, part, kind, false)}.", LogKind.Battle);
        if (k.Alive && !b.Alive) Log($"{Capital(who)} dies.", LogKind.Battle);
        else if (k.Conscious && !b.Conscious && b.Alive) Log($"{Capital(who)} collapses.", LogKind.Battle);
        else if (!k.Conscious && b.Conscious) Log($"{Capital(who)} stirs.");
        bool bleeding = Bleeding(b);
        if (bleeding && !k.Bleeding && b.Alive) Log($"{Capital(who)} is bleeding.", LogKind.Battle);
        Remember(b, k);
    }

    private static string Name(Body b) =>
        b.IsRemains ? $"the remains of a {b.Plan.Name.ToLowerInvariant()}"
        : b.Plan.Instinctive ? $"the {b.Plan.Name.ToLowerInvariant()}" : "the other student";

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---------------------------------------------------------------- hurts

    private enum Hurt { Burned, Frozen, Struck, Lost }

    /// <summary>
    /// Parts that are worse than when last logged by at least <see cref="HurtStep"/>, or gone, with
    /// what hurt them most; the snapshot moves on for those parts only, so slow harm adds up.
    /// </summary>
    private List<(string Part, Hurt Kind, double Level)> NewHurts(Body b, Known k)
    {
        var hurts = new List<(string, Hurt, double)>();
        var parts = b.Anatomy.Parts;
        var present = new HashSet<string>();
        for (int p = 0; p < parts.Length; p++)
        {
            string name = parts[p].Name;
            present.Add(name);
            var now = PartHurt(b, p);
            if (!k.Parts.TryGetValue(name, out var was)) { k.Parts[name] = now; continue; }
            double burn = now.Burn - was.Burn, frost = now.Frost - was.Frost, trauma = now.Trauma - was.Trauma;
            double worst = Math.Max(burn, Math.Max(frost, trauma));
            if (worst < HurtStep) continue;
            var kind = worst == burn ? Hurt.Burned : worst == frost ? Hurt.Frozen : Hurt.Struck;
            double level = kind == Hurt.Burned ? now.Burn : kind == Hurt.Frozen ? now.Frost : now.Trauma;
            hurts.Add((name, kind, level));
            k.Parts[name] = now;
        }
        var lost = new List<string>();
        foreach (var name in k.Parts.Keys)
            if (!present.Contains(name)) lost.Add(name);
        foreach (var name in lost)
        {
            k.Parts.Remove(name);
            if (b.Alive || b.IsRemains) hurts.Add((name, Hurt.Lost, 1.0));
        }
        return hurts;
    }

    /// <summary>A part's worst burn, worst frostbite (each the dead share of its worst tissue) and its trauma.</summary>
    private static (double Burn, double Frost, double Trauma) PartHurt(Body b, int p)
    {
        int first = b.Anatomy.LayerStart[p], end = b.Anatomy.LayerStart[p + 1];
        double burn = 0.0, frost = 0.0;
        for (int n = first; n < end; n++)
        {
            burn = Math.Max(burn, 1.0 - Math.Exp(-b.Burn[n]));
            frost = Math.Max(frost, b.Frostbite[n]);
        }
        return (burn, frost, Math.Min(1.0, b.Trauma[p]));
    }

    private static string HurtWords(Hurt kind, double level) => kind switch
    {
        Hurt.Lost => "is gone",
        Hurt.Burned => level < 0.3 ? "is scorched" : level < 0.7 ? "is badly burned" : "is charred",
        Hurt.Frozen => level < 0.3 ? "is nipped by frost" : level < 0.7 ? "is frostbitten" : "is frozen through",
        _ => level < 0.3 ? "is bruised" : level < 0.7 ? "is badly hurt" : "is crushed",
    };

    private static Known Remember(Body b, Known k)
    {
        k.Parts.Clear();
        for (int p = 0; p < b.Anatomy.Parts.Length; p++) k.Parts[b.Anatomy.Parts[p].Name] = PartHurt(b, p);
        k.Alive = b.Alive; k.Conscious = b.Conscious; k.Asleep = b.Asleep; k.Bleeding = Bleeding(b);
        k.Shivering = b.ShiverW > 1.0;
        k.Hunger = Stage(b.Hunger); k.Thirst = Stage(b.Thirst); k.Fatigue = Stage(b.Fatigue, 0.5, 0.8);
        return k;
    }

    // ---------------------------------------------------------------- what the student noticed

    private void Notice(Body me)
    {
        foreach (var p in me.Percepts)
        {
            var key = (p.Kind, p.Subject, p.Detail);
            bool known = _noticed.TryGetValue(key, out long last);
            if (known && p.LastTick == last) continue;
            _noticed[key] = p.LastTick;
            if (known && p.LastTick - last <= ForgetTicks) continue;
            // Memories from before the log began aren't news.
            if (!known && _sim.Tick - p.LastTick > Simulation.TicksPerSecond) continue;
            string where = Direction(p.X - me.X, p.Y - me.Y);
            switch (p.Kind)
            {
                case PerceptKind.Body:
                    var other = FindBody((int)p.Subject);
                    if (other != null && other.Id != me.Id) Log($"You see {Appearance(other)} {where}.");
                    break;
                case PerceptKind.Fire: Log($"You see fire {where}."); break;
                case PerceptKind.Glow: Log($"You see something glowing {where}."); break;
                case PerceptKind.Bolt:
                    if (!(_bolts.TryGetValue(p.Subject, out var bolt) && bolt.Mine)) Log($"You see a bolt of fire {where}.", LogKind.Battle);
                    break;
                case PerceptKind.Sound:
                    var kind = (SoundKind)p.Detail;
                    if (kind is SoundKind.Footstep or SoundKind.Fire or SoundKind.Whoosh) break;
                    Log($"You hear a {kind.ToString().ToLowerInvariant()} {where}.");
                    break;
            }
        }
    }

    private Body? FindBody(int id)
    {
        foreach (var body in _sim.Bodies)
            if (body.Id == id) return body;
        return null;
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>The log down the right of the window, newest at the bottom.</summary>
    private void DrawLog()
    {
        var font = ThemeDB.FallbackFont;
        float left = _sim.Grid.Width * TileSize + 10;
        float width = GetViewportRect().Size.X - left - 6;
        if (width < 120) return;
        const int fontSize = 12, lineHeight = 15;
        DrawString(font, new Vector2(left, 16), _battleOnly ? "Battle log (Tab: all)" : "Log (Tab: battle only)", fontSize: 14);
        int perLine = Math.Max(20, (int)(width / (fontSize * 0.55f)));
        var lines = new List<(string, Color)>();
        foreach (var (tick, text, kind) in _log)
        {
            if (_battleOnly && kind != LogKind.Battle) continue;
            long s = tick / Simulation.TicksPerSecond;
            var color = kind == LogKind.Battle ? new Color(1f, 0.6f, 0.5f) : new Color(0.85f, 0.85f, 0.85f);
            bool first = true;
            foreach (var line in Wrap($"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00} {text}", perLine))
            {
                lines.Add((first ? line : "   " + line, color));
                first = false;
            }
        }
        int room = (int)((GetViewportRect().Size.Y - 30) / lineHeight);
        int start = Math.Max(0, lines.Count - room);
        for (int n = start; n < lines.Count; n++)
            DrawString(font, new Vector2(left, 34 + (n - start) * lineHeight), lines[n].Item1, fontSize: fontSize, modulate: lines[n].Item2);
    }

    private static IEnumerable<string> Wrap(string text, int perLine)
    {
        while (text.Length > perLine)
        {
            int cut = text.LastIndexOf(' ', perLine);
            if (cut <= 0) cut = perLine;
            yield return text[..cut];
            text = text[cut..].TrimStart();
        }
        yield return text;
    }
}
