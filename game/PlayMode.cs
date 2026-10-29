using System;
using System.Collections.Generic;
using Godot;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Magic;
using Magischool.Sim.Materials;
using Magischool.Sim.Perception;

namespace Magischool.Game;

/// <summary>
/// Play mode: you are one student, and the view shows only what they know. Tiles they can see
/// now are drawn as they are; tiles they saw before are drawn dim, as they were then; the rest is
/// dark. Looking tells you what a thing is, never how hot it is: touch tells warm from cold within
/// reach, and a sense spell reads a tile (and anyone in it) in full.
///
/// Left click: walk there · Right click: what to do there (look, eat, drink, cast, sleep, wait) ·
/// Arrow keys: one step · Space: wait a second · Enter: wait a minute · Z: sleep or wake ·
/// Q/E: look a level down/up · Esc: stop walking · Tab: whole log or battle only ·
/// F5 save · F9 load · F1 debug view
/// </summary>
public partial class WorldView
{
    private bool _debug;
    private long _actionStartTick;

    /// <summary>Tiles of the shown level the student sees now, and when that was worked out.</summary>
    private bool[] _seen = Array.Empty<bool>();
    private (long Tick, int Z, int Body) _seenFor = (-1, -1, -1);

    /// <summary>What the student last saw in each tile, as its shown material plus one: zero for never.</summary>
    private ushort[] _memory = Array.Empty<ushort>();

    /// <summary>What the last sense spell read, until the next one.</summary>
    private string _reading = "";

    private List<(int X, int Y, int Z)>? _path;
    private (int X, int Y) _goal;
    private double _stepClock;
    private int _stuck;

    /// <summary>Real time between steps of a walk, s; each step is half a second of world time.</summary>
    private const double StepEverySeconds = 0.12;
    private const int TicksPerStep = Simulation.TicksPerSecond / 2;

    private PopupMenu _menu = null!;
    private int _menuX, _menuY, _menuZ;

    private enum Act { Look, Walk, Eat, Drink, Sense, Heat, Freeze, Fireball, Sleep, Wait }

    private void SetUpMenu()
    {
        _menu = new PopupMenu();
        AddChild(_menu);
        _menu.IdPressed += id => DoAct((Act)id);
    }

    private void ForgetSights()
    {
        _memory = new ushort[_sim.Grid.Count];
        _seenFor = (-1, -1, -1);
        _reading = "";
        _path = null;
        ForgetLog();
    }

    // ---------------------------------------------------------------- input

    private void HandlePlayKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Left: _path = null; PlayStep(-1, 0); break;
            case Key.Right: _path = null; PlayStep(1, 0); break;
            case Key.Up: _path = null; PlayStep(0, -1); break;
            case Key.Down: _path = null; PlayStep(0, 1); break;
            case Key.Space: _path = null; SpendTicks(Simulation.TicksPerSecond); break;
            case Key.Enter: _path = null; DoAct(Act.Wait); break;
            case Key.Z: DoAct(Act.Sleep); break;
            case Key.Q: _z = Math.Max(0, _z - 1); break;
            case Key.E: _z = Math.Min(_sim.Grid.Depth - 1, _z + 1); break;
            case Key.Escape: _path = null; _status = "You stop."; break;
            case Key.Tab: _battleOnly = !_battleOnly; break;
            case Key.F5: Save(); break;
            case Key.F9: Load(); break;
            default: return;
        }
        QueueRedraw();
    }

    private void HandlePlayClick(InputEventMouseButton click)
    {
        Vector2 local = ToLocal(click.Position);
        int x = (int)MathF.Floor(local.X / TileSize), y = (int)MathF.Floor(local.Y / TileSize);
        if (!_sim.Grid.InBounds(x, y, _z)) return;
        (_menuX, _menuY, _menuZ) = (x, y, _z);
        if (click.ButtonIndex == MouseButton.Left) DoAct(Act.Walk);
        else if (click.ButtonIndex == MouseButton.Right) OpenMenu(click.Position);
    }

    private void OpenMenu(Vector2 at)
    {
        var me = Steered();
        bool alive = me is { Alive: true, IsRemains: false };
        bool awake = alive && me!.Conscious && !me.Asleep;
        bool seen = Sees(_menuX, _menuY, _menuZ);
        bool reach = me != null && InReach(me, _menuX, _menuY, _menuZ);
        bool self = me != null && _menuX == me.X && _menuY == me.Y;
        _menu.Clear();
        Add("Look", Act.Look, true);
        Add("Walk here", Act.Walk, awake && !self);
        Add("Eat", Act.Eat, awake && reach);
        Add("Drink", Act.Drink, awake && reach);
        Add("Cast: sense", Act.Sense, awake && (seen || reach));
        Add("Cast: heat", Act.Heat, awake && seen);
        Add("Cast: freeze", Act.Freeze, awake && seen);
        Add("Cast: fireball", Act.Fireball, awake && seen && !self);
        Add(me is { Asleep: true } ? "Wake up" : "Sleep", Act.Sleep, alive);
        Add("Wait a minute", Act.Wait, alive);
        _menu.ResetSize();
        _menu.Popup(new Rect2I((Vector2I)at, Vector2I.Zero));

        void Add(string label, Act act, bool enabled)
        {
            _menu.AddItem(label, (int)act);
            _menu.SetItemDisabled(_menu.ItemCount - 1, !enabled);
        }
    }

    private void DoAct(Act act)
    {
        var me = Steered();
        if (me == null) { _status = "You have no body to act with."; QueueRedraw(); return; }
        int x = _menuX, y = _menuY, z = _menuZ;
        int eyeZ = Math.Min(me.Z + PerceptionSystem.EyeDz(me), _sim.Grid.Depth - 1);
        switch (act)
        {
            case Act.Look:
                _status = LookAt(x, y, z);
                break;
            case Act.Walk:
                WalkTo(x, y);
                break;
            case Act.Eat:
                double gut = me.GutCarbKg;
                _sim.Enqueue(new EatFood(me.Id, x, y, z, MouthfulKg));
                SpendTicks(Simulation.TicksPerSecond * 5);
                _status = me.GutCarbKg > gut ? "You eat a mouthful." : "There's nothing there you can eat.";
                break;
            case Act.Drink:
                double water = me.GutWaterKg;
                _sim.Enqueue(new DrinkWater(me.Id, x, y, z, MouthfulKg));
                SpendTicks(Simulation.TicksPerSecond * 3);
                _status = me.GutWaterKg > water ? "You drink." : "There's nothing there you can drink.";
                break;
            case Act.Sense:
                Face(me, x, y);
                var sense = new CastSense(me.X, me.Y, eyeZ, x, y, z, CastSense.DefaultJoules);
                _sim.Enqueue(sense);
                SpendTicks(TicksPerWaitAction);
                if (sense.Gathered > 0.0)
                {
                    var grid = _sim.Grid;
                    var body = BodyAt(x, y, z);
                    _reading = $"Sensed at {Clock()}: " + Describe(grid, grid.Index(x, y, z))
                               + (body != null ? "\n" + DescribeBody(body) : "");
                    _status = "Your sense reaches out and comes back.";
                }
                else _status = "You reach for the ether, but nothing answers.";
                break;
            case Act.Heat:
                Face(me, x, y);
                CastFromHead(new CastHeat(me.X, me.Y, eyeZ, x, y, z, HeatJoules), "Heat floods into it.");
                break;
            case Act.Freeze:
                Face(me, x, y);
                CastFromHead(new CastFreeze(me.X, me.Y, eyeZ, x, y, z, FreezeJoules), "You draw the warmth out of it.");
                break;
            case Act.Fireball:
                Face(me, x, y);
                _boltsAreMine = true;
                // Loosed from arm's length toward the target, so it doesn't fly down through your own body.
                int lx = me.X + Math.Sign(x - me.X), ly = me.Y + Math.Sign(y - me.Y);
                var g = _sim.Grid;
                if (!g.InBounds(lx, ly, eyeZ) || (g.HasCondensed(g.Index(lx, ly, eyeZ)) && g.FillFraction(g.Index(lx, ly, eyeZ)) >= 0.5)
                    || (lx == x && ly == y && eyeZ == z)) (lx, ly) = (me.X, me.Y);
                CastFromHead(new CastFireball(lx, ly, eyeZ, x - lx, y - ly, z - eyeZ, FireballJoules), "A fireball leaps from your hands.");
                break;
            case Act.Sleep:
                bool wake = me.Asleep;
                _sim.Enqueue(new SetAsleep(me.Id, !wake));
                SpendTicks(TicksPerWaitAction);
                _status = wake ? "You wake." : "You lie down to sleep. (Z or Wait to pass the time; Z again to wake.)";
                break;
            case Act.Wait:
                SpendTicks(TicksPerMinute);
                _status = "A minute passes.";
                break;
        }
        QueueRedraw();
    }

    private void CastFromHead(WorldCommand spell, string done)
    {
        Log(done, LogKind.Battle);
        _sim.Enqueue(spell);
        SpendTicks(TicksPerWaitAction);
        _status = done;
    }

    private void Face(Body me, int x, int y)
    {
        int fx = Math.Sign(x - me.X), fy = Math.Sign(y - me.Y);
        if (fx != 0 || fy != 0) _sim.Enqueue(new FaceBody(me.Id, fx, fy));
    }

    private static bool InReach(Body body, int x, int y, int z) =>
        Math.Abs(x - body.X) <= 1 && Math.Abs(y - body.Y) <= 1 && z >= body.Z - 1 && z <= body.Z + body.HeightTiles;

    // ---------------------------------------------------------------- walking

    private void PlayStep(int dx, int dy)
    {
        if (Steered() is not { } me) return;
        _sim.Enqueue(new MoveBody(me.Id, dx, dy));
        SpendTicks(TicksPerStep);
        _z = me.Z;
    }

    private void WalkTo(int x, int y)
    {
        if (Steered() is not { } me) return;
        var path = Pathfinder.Find(_sim, me, x, y);
        if (path == null) { _status = "You can't see a way there."; return; }
        _path = path;
        _goal = (x, y);
        _stuck = 0;
        _stepClock = StepEverySeconds;
        _status = path.Count == 0 ? "You're already there." : "You walk.";
    }

    public override void _Process(double delta)
    {
        if (_debug || _path == null) return;
        _stepClock += delta;
        if (_stepClock < StepEverySeconds) return;
        _stepClock = 0.0;
        var me = Steered();
        if (me == null || !me.Conscious || me.Asleep || _path.Count == 0)
        {
            if (_path.Count == 0) _status = "You arrive.";
            _path = null;
            QueueRedraw();
            return;
        }
        var next = _path[0];
        _sim.Enqueue(new MoveBody(me.Id, next.X - me.X, next.Y - me.Y));
        SpendTicks(TicksPerStep);
        _z = me.Z;
        if ((me.X, me.Y, me.Z) == next)
        {
            _path.RemoveAt(0);
            _stuck = 0;
        }
        else
        {
            // Something changed under the walk: find the way again, and give up if there's none.
            _path = ++_stuck > 2 ? null : Pathfinder.Find(_sim, me, _goal.X, _goal.Y);
            if (_path == null) _status = "Something is in the way.";
        }
    }

    // ---------------------------------------------------------------- what the student knows

    private bool Sees(int x, int y, int z)
    {
        var grid = _sim.Grid;
        if (!grid.InBounds(x, y, z)) return false;
        if (z == _z) { RefreshSight(); return _seen[y * grid.Width + x]; }
        return Steered() is { } me && Sight.Sees(_sim, LightField.AllSources(_sim), me, x, y, z);
    }

    /// <summary>Works out what the student sees on the shown level, once per tick, and remembers it.</summary>
    private void RefreshSight()
    {
        var grid = _sim.Grid;
        var key = (_sim.Tick, _z, _bodyId);
        if (_seenFor == key && _seen.Length == grid.Width * grid.Height) return;
        _seenFor = key;
        if (_seen.Length != grid.Width * grid.Height) _seen = new bool[grid.Width * grid.Height];
        if (Steered() is { } me) Sight.Level(_sim, LightField.AllSources(_sim), me, _z, _seen);
        else Array.Clear(_seen);
        for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
            if (_seen[y * grid.Width + x])
            {
                int i = grid.Index(x, y, _z);
                _memory[i] = (ushort)(ShownMaterial(grid, i) + 1);
            }
    }

    /// <summary>What a tile looks like from above: what fills it, or through open air the ground under it.</summary>
    private ushort ShownMaterial(WorldGrid grid, int i)
    {
        if (grid.HasCondensed(i)) return _materials[grid.Material[i]].IsPlant ? grid.Material[i] : grid.DisplayMaterial(i);
        int below = i - grid.Width * grid.Height;
        return below >= 0 && grid.HasCondensed(below) ? grid.Material[below] : grid.DisplayMaterial(i);
    }

    private Body? BodyAt(int x, int y, int z)
    {
        foreach (var body in _sim.Bodies)
            if (body.X == x && body.Y == y && z >= body.Z && z < body.Z + body.HeightTiles) return body;
        return null;
    }

    /// <summary>What looking at a tile tells: what is there, never how hot; touch adds warm or cold within reach.</summary>
    private string LookAt(int x, int y, int z)
    {
        var grid = _sim.Grid;
        if (!grid.InBounds(x, y, z)) return "";
        int i = grid.Index(x, y, z);
        var me = Steered();
        if (!Sees(x, y, z))
            return _memory[i] != 0 ? $"You remember {_materials[(ushort)(_memory[i] - 1)].Name.ToLowerInvariant()} there." : "You can't see there.";

        var said = new List<string>();
        var body = BodyAt(x, y, z);
        if (body != null && body.Id != _bodyId) said.Add(Appearance(body));
        if (grid.HasCondensed(i))
        {
            var def = _materials[grid.Material[i]];
            double fill = grid.FillFraction(i);
            string amount = def.IsPlant ? "" : fill < 0.1 ? "a little " : fill < 0.5 ? "some " : fill < 0.95 ? "a lot of " : "solid ";
            if (def.Phase == Phase.Liquid) amount = fill < 0.1 ? "a puddle of " : fill < 0.6 ? "shallow " : "deep ";
            said.Add(amount + def.Name.ToLowerInvariant());
        }
        else if (z > 0 && grid.HasCondensed(i - grid.Width * grid.Height))
            said.Add($"open ground ({_materials[grid.Material[i - grid.Width * grid.Height]].Name.ToLowerInvariant()})");
        else said.Add("open air");
        if (grid.Flame[i] != 0) said.Add("on fire");
        else if (SmokeGas >= 0 && SmokeShade(grid, i) > 0.2f) said.Add("thick with smoke");
        string text = (body != null && body.Id == _bodyId ? "You stand in " : "You see ") + string.Join(", ", said) + ".";
        if (me != null && me.Conscious && InReach(me, x, y, z))
            text += " " + Feel(grid.HasCondensed(i) ? grid.Temperature[i] : grid.GasTemperature[i], grid.HasCondensed(i));
        return text;
    }

    /// <summary>What a body looks like from outside: its kind, and whether it stands, flees, sleeps or lies still.</summary>
    private string Appearance(Body b)
    {
        string who = b.Plan.Instinctive ? $"a {b.Plan.Name.ToLowerInvariant()}" : "a student";
        if (b.IsRemains) return $"the remains of {who}";
        if (!b.Alive || !b.Conscious) return $"{who}, lying still";
        if (b.Asleep) return $"{who}, asleep";
        if (b.Plan.Instinctive && InstinctFlees(b)) return $"{who}, running";
        return b.Plan.Instinctive && b.Hunger >= Magischool.Sim.Ecology.InstinctSystem.GrazeHunger ? $"{who}, grazing" : $"{who}, standing";
    }

    private bool InstinctFlees(Body b) => Magischool.Sim.Ecology.InstinctSystem.Fright(_sim, b, out _, out _);

    /// <summary>How a temperature feels to the hand, in words.</summary>
    private static string Feel(double kelvin, bool solid)
    {
        double c = kelvin - 273.15;
        string thing = solid ? "It" : "The air";
        return c < -10 ? $"{thing} is bitterly cold." : c < 5 ? $"{thing} feels cold." : c < 15 ? $"{thing} feels cool."
            : c < 30 ? $"{thing} feels neither warm nor cold." : c < 45 ? $"{thing} feels warm." : c < 60 ? $"{thing} feels hot."
            : $"{thing} is far too hot to touch.";
    }

    /// <summary>How the student feels, in words, from what their body is doing.</summary>
    private string Feelings(Body b)
    {
        if (b.IsRemains || !b.Alive) return "You are dead.";
        if (!b.Conscious) return "You are unconscious.";
        var words = new List<string>();
        if (b.Asleep) words.Add("asleep");
        if (b.ShiverW > 1) words.Add("shivering with cold");
        else if (b.MeanSkinTemperature > 0.0 && b.MeanSkinTemperature < 273.15 + 26) words.Add("chilly");
        if (b.SweatKgPerS > 1e-6) words.Add("sweating");
        if (b.Hunger > 0.7) words.Add("starving"); else if (b.Hunger > 0.35) words.Add("hungry");
        if (b.Thirst > 0.7) words.Add("parched"); else if (b.Thirst > 0.35) words.Add("thirsty");
        if (b.Fatigue > 0.8) words.Add("exhausted"); else if (b.Fatigue > 0.5) words.Add("tired");
        if (b.OxygenFraction < 0.9) words.Add("short of breath");
        if (b.BloodFraction < 0.85) words.Add("bleeding badly");
        if (b.Toxins > 0.1) words.Add("sick");
        string feel = words.Count == 0 ? "You feel fine." : "You are " + string.Join(", ", words) + ".";
        var parts = b.Anatomy.Parts;
        int worst = -1;
        double worstHurt = 0.2;
        for (int p = 0; p < parts.Length; p++)
        {
            double hurt = Math.Max(1.0 - b.Health(p), b.Necrosis(b.Anatomy.LayerStart[p]));
            if (hurt > worstHurt) { worstHurt = hurt; worst = p; }
        }
        if (worst >= 0) feel += $" Your {parts[worst].Name} {(worstHurt > 0.7 ? "is badly hurt" : "hurts")}.";
        if (b.Vision < 0.5) feel += " You can barely see.";
        return feel;
    }

    /// <summary>What the student heard during the last action, by direction.</summary>
    private string Heard(Body b)
    {
        var heard = new List<string>();
        foreach (var p in b.Percepts)
        {
            if (p.Kind != PerceptKind.Sound || p.LastTick < _actionStartTick) continue;
            string what = ((SoundKind)p.Detail).ToString().ToLowerInvariant();
            string text = $"a {what} {Direction(p.X - b.X, p.Y - b.Y)}";
            if (!heard.Contains(text)) heard.Add(text);
            if (heard.Count == 3) break;
        }
        return heard.Count == 0 ? "" : "You hear " + string.Join(", ", heard) + ".";
    }

    private static string Direction(int dx, int dy)
    {
        if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) return "close by";
        string ns = dy < -Math.Abs(dx) / 2 ? "north" : dy > Math.Abs(dx) / 2 ? "south" : "";
        string ew = dx > Math.Abs(dy) / 2 ? "east" : dx < -Math.Abs(dy) / 2 ? "west" : "";
        return "to the " + ns + ew;
    }

    private string Clock()
    {
        long s = (long)_sim.ElapsedSeconds;
        return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
    }

    // ---------------------------------------------------------------- drawing

    private void DrawPlay()
    {
        var grid = _sim.Grid;
        RefreshSight();
        for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
        {
            int i = grid.Index(x, y, _z);
            var rect = new Rect2(x * TileSize, y * TileSize, TileSize - 1, TileSize - 1);
            if (_seen[y * grid.Width + x])
            {
                var color = MaterialColor(_materials[ShownMaterial(grid, i)]);
                if (SmokeGas >= 0) color = color.Lerp(SmokeColor, SmokeShade(grid, i));
                DrawRect(rect, color);
                if (grid.Flame[i] != 0)
                    DrawCircle(new Vector2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize), TileSize * 0.22f, FlameColor);
            }
            else if (_memory[i] != 0)
            {
                var c = MaterialColor(_materials[(ushort)(_memory[i] - 1)]);
                float grey = (c.R + c.G + c.B) / 3f;
                DrawRect(rect, new Color(0.4f * (c.R + grey) / 2f, 0.4f * (c.G + grey) / 2f, 0.45f * (c.B + grey) / 2f));
            }
            else DrawRect(rect, new Color(0.02f, 0.02f, 0.03f));
        }
        foreach (var body in _sim.Bodies)
        {
            if (_z < body.Z || _z >= body.Z + body.HeightTiles) continue;
            if (body.Id != _bodyId && !_seen[body.Y * grid.Width + body.X]) continue;
            var centre = new Vector2((body.X + 0.5f) * TileSize, (body.Y + 0.5f) * TileSize);
            bool wild = body.Plan.Instinctive;
            var color = !body.Alive || !body.Conscious ? new Color(0.55f, 0.5f, 0.45f)
                : wild ? new Color(0.62f, 0.5f, 0.38f) : new Color(0.95f, 0.8f, 0.65f);
            DrawCircle(centre, TileSize * (body.IsRemains ? 0.2f : wild ? 0.25f : 0.38f), color);
            if (body.Id == _bodyId) DrawArc(centre, TileSize * 0.48f, 0f, MathF.Tau, 16, Colors.White, 1.5f);
            if (!body.IsRemains && body.Alive)
                DrawLine(centre, centre + new Vector2(body.FacingX, body.FacingY).Normalized() * TileSize * 0.5f, Colors.Black, 2f);
        }
        foreach (var bolt in _sim.Projectiles)
        {
            int bx = (int)Math.Floor(bolt.X), by = (int)Math.Floor(bolt.Y);
            if ((int)Math.Floor(bolt.Z) == _z && grid.InBounds(bx, by, _z) && _seen[by * grid.Width + bx])
                DrawCircle(new Vector2((float)bolt.X * TileSize, (float)bolt.Y * TileSize), TileSize * 0.3f, FlameColor);
        }
        if (_path != null)
            foreach (var (px, py, _) in _path)
                DrawCircle(new Vector2((px + 0.5f) * TileSize, (py + 0.5f) * TileSize), 2f, new Color(1f, 1f, 1f, 0.5f));

        var font = ThemeDB.FallbackFont;
        float hudY = grid.Height * TileSize + 22;
        var me = Steered();
        string level = me == null ? $"level {_z}" : _z == me.Z ? "at your feet" : _z > me.Z ? $"{_z - me.Z} up" : $"{me.Z - _z} down";
        string sky = _sim.SkyIlluminanceLux >= 1000 ? "day" : _sim.SkyIlluminanceLux > 0.01 ? "moonlight" : "starlight";
        DrawString(font, new Vector2(4, hudY), $"{Clock()}   {sky}   looking {level}", fontSize: 16);
        DrawString(font, new Vector2(4, hudY + 22),
            "LMB walk | RMB act (look, eat, drink, cast sense/heat/freeze/fireball, sleep, wait) | arrows step | Space wait 1 s | Enter wait 1 min | Z sleep | Q/E level | Esc stop | Tab log: all/battle | F5 save | F9 load | F1 debug view",
            fontSize: 13, modulate: new Color(0.7f, 0.7f, 0.7f));
        if (me != null)
            DrawString(font, new Vector2(4, hudY + 42), Feelings(me) + " " + Heard(me), fontSize: 13, modulate: new Color(0.95f, 0.85f, 0.75f));
        if (_status.Length > 0)
            DrawString(font, new Vector2(4, hudY + 62), _status, fontSize: 13, modulate: new Color(1f, 0.85f, 0.4f));
        if (grid.InBounds(_hoverX, _hoverY, _z))
            DrawString(font, new Vector2(4, hudY + 82), LookAt(_hoverX, _hoverY, _z), fontSize: 13);
        if (_reading.Length > 0)
            DrawMultilineString(font, new Vector2(4, hudY + 102), _reading, fontSize: 13, modulate: new Color(0.75f, 0.85f, 0.95f));
        DrawLog();
    }
}
