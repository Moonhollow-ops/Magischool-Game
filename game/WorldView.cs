using System;
using System.IO;
using Godot;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Ecology;
using Magischool.Sim.Fluids;
using Magischool.Sim.Magic;
using Magischool.Sim.Materials;
using Magischool.Sim.Perception;
using Magischool.Sim.Persistence;
using Magischool.Sim.Structure;
using Magischool.Sim.WorldGen;

namespace Magischool.Game;

/// <summary>
/// Thin viewer over the simulation: one coloured square per tile of the
/// current z-slice. The world only advances when an action spends ticks.
///
/// Space: wait 1 tick · Enter: wait 1 s · W: wait 1 min · H: wait 10 min · Q/E: z down/up
/// Tab: heat overlay · A: air currents · S: stress · M: ether · 1-9: pick material · Left click: place it
/// Right click: set tile to 1000 K (Shift: 100 K), a debug tool that ignores conservation
/// Spells (all drawn from the ether around the caster): C: stand the caster on the hovered tile ·
/// F: fireball at the hovered tile · X: freeze it · V: heat it · G: pour charge into it ·
/// B: conjure 5 kg of the brush material into it
/// Bodies: P: a student stands on the hovered tile (and becomes the one you steer) ·
/// arrow keys: they step · T: they eat or drink from the hovered tile · Z: they sleep or wake ·
/// O: they turn to face the hovered tile
/// Perception: L: light view (how brightly each tile is lit) · N: day, full moon, starlight ·
/// sounds of the last action ring where they were made
/// F5: save · F9: load · R: new seed · Hover a tile to read its contents
/// </summary>
public partial class WorldView : Node2D
{
    private const int TileSize = 20;
    private const int TicksPerWaitAction = 1;
    private const int TicksPerLongWaitAction = Simulation.TicksPerSecond;
    private const int TicksPerMinute = 60 * Simulation.TicksPerSecond;
    private const string SavePath = "user://world.snap";

    private readonly MaterialTable _materials = BuiltInMaterials.CreateTable();
    private int SmokeGas => _materials.GasIndexOf(_materials.Id(BuiltInMaterials.Smoke));
    private Simulation _sim = null!;
    private int _z = TestRoomGenerator.FloorZ;
    private ushort _brush = 1;
    private bool _heatOverlay;
    private bool _airOverlay;
    private bool _stressOverlay;
    private bool _etherOverlay;
    private bool _lightOverlay;
    /// <summary>Sounds made during the last action, loudest kept, to draw where they were.</summary>
    private readonly System.Collections.Generic.List<SoundEvent> _heard = new();
    private string _status = "";
    private int _hoverX = -1, _hoverY = -1;
    private int _casterX = 2, _casterY = 2, _casterZ = TestRoomGenerator.FloorZ;

    private const double FireballJoules = 2_000_000;
    private const double FreezeJoules = 20_000_000;
    private const double HeatJoules = 5_000_000;
    private const double ChargeJoules = 20_000_000;
    private const double ConjureKg = 5.0;
    private const double MouthfulKg = 0.25;

    /// <summary>The body the arrow keys steer: the last one placed.</summary>
    private int _bodyId = -1;

    public override void _Ready()
    {
        NewWorld("moonhollow");
    }

    private void NewWorld(string seed)
    {
        _sim = TestRoomGenerator.Create(seed, _materials, systems: StandardSystems.Create());
        TestRoomGenerator.Populate(_sim);
        _z = Math.Min(TestRoomGenerator.FloorZ, _sim.Grid.Depth - 1);
        _status = $"New world '{seed}'";
        QueueRedraw();
    }

    /// <summary>Every player action spends world time. Commands queued by the action apply on the first tick.</summary>
    private void SpendTicks(int ticks)
    {
        _heard.Clear();
        for (int t = 0; t < ticks; t++)
        {
            _sim.Step();
            // Every step, thud, crack and bang (a fire's own roar is heard but not drawn).
            foreach (var sound in _sim.Sounds)
                if (_heard.Count < 200) _heard.Add(sound);
        }
        QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true } key)
            HandleKey(key);
        else if (@event is InputEventMouseButton { Pressed: true } click)
            HandleClick(click);
        else if (@event is InputEventMouseMotion motion)
        {
            Vector2 local = ToLocal(motion.Position);
            _hoverX = (int)MathF.Floor(local.X / TileSize);
            _hoverY = (int)MathF.Floor(local.Y / TileSize);
            QueueRedraw();
        }
    }

    private void HandleKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Space: SpendTicks(TicksPerWaitAction); break;
            case Key.Enter: SpendTicks(TicksPerLongWaitAction); break;
            case Key.W: SpendTicks(TicksPerMinute); break;
            case Key.H: SpendTicks(10 * TicksPerMinute); break;
            case Key.Q: _z = Math.Max(0, _z - 1); break;
            case Key.E: _z = Math.Min(_sim.Grid.Depth - 1, _z + 1); break;
            case Key.Tab: _heatOverlay = !_heatOverlay; break;
            case Key.A: _airOverlay = !_airOverlay; break;
            case Key.S: _stressOverlay = !_stressOverlay; break;
            case Key.M: _etherOverlay = !_etherOverlay; break;
            case Key.L: _lightOverlay = !_lightOverlay; break;
            case Key.N:
                double sky = _sim.SkyIlluminanceLux > 1.0 ? LightField.FullMoonLux
                    : _sim.SkyIlluminanceLux > 0.01 ? LightField.StarlightLux : LightField.OvercastDayLux;
                _sim.Enqueue(new SetSkyLight(sky));
                SpendTicks(TicksPerWaitAction);
                _status = sky >= 1000 ? "Day" : sky > 0.01 ? "Full moon" : "Starlight";
                break;
            case Key.O when Steered() is { } turner && _sim.Grid.InBounds(_hoverX, _hoverY, _z):
                _sim.Enqueue(new FaceBody(turner.Id, Math.Sign(_hoverX - turner.X), Math.Sign(_hoverY - turner.Y)));
                SpendTicks(TicksPerWaitAction);
                break;
            case Key.R: NewWorld($"seed-{Time.GetTicksMsec()}"); break;
            case Key.C when _sim.Grid.InBounds(_hoverX, _hoverY, _z):
                (_casterX, _casterY, _casterZ) = (_hoverX, _hoverY, _z);
                _status = $"Caster at ({_casterX},{_casterY},{_casterZ})";
                break;
            case Key.F: Cast(new CastFireball(_casterX, _casterY, _casterZ, _hoverX - _casterX, _hoverY - _casterY, _z - _casterZ, FireballJoules)); break;
            case Key.X: Cast(new CastFreeze(_casterX, _casterY, _casterZ, _hoverX, _hoverY, _z, FreezeJoules)); break;
            case Key.V: Cast(new CastHeat(_casterX, _casterY, _casterZ, _hoverX, _hoverY, _z, HeatJoules)); break;
            case Key.G: Cast(new CastCharge(_casterX, _casterY, _casterZ, _hoverX, _hoverY, _z, ChargeJoules)); break;
            case Key.B: Cast(new CastConjure(_casterX, _casterY, _casterZ, _hoverX, _hoverY, _z, _brush, ConjureKg)); break;
            case Key.P when _sim.Grid.InBounds(_hoverX, _hoverY, _z):
                int id = _sim.NextBodyId;
                _sim.Enqueue(new SpawnBody(_hoverX, _hoverY, _z, BodyPlans.Human.Key));
                SpendTicks(TicksPerWaitAction);
                if (_sim.NextBodyId > id) _bodyId = id;
                _status = _sim.NextBodyId > id ? $"Student {id} stands at ({_hoverX},{_hoverY},{_z})" : "No room to stand there";
                break;
            case Key.K when _sim.Grid.InBounds(_hoverX, _hoverY, _z):
                bool bare = !_sim.Grid.HasCondensed(_sim.Grid.Index(_hoverX, _hoverY, _z));
                _sim.Enqueue(new PlantSeedling(_hoverX, _hoverY, _z, BuiltInMaterials.Grass));
                SpendTicks(TicksPerWaitAction);
                _status = bare && _sim.Grid.HasCondensed(_sim.Grid.Index(_hoverX, _hoverY, _z))
                    ? $"Grass planted at ({_hoverX},{_hoverY},{_z})" : "Grass needs open air over soil";
                break;
            case Key.J when _sim.Grid.InBounds(_hoverX, _hoverY, _z):
                int rabbit = _sim.NextBodyId;
                _sim.Enqueue(new SpawnBody(_hoverX, _hoverY, _z, BodyPlans.Rabbit.Key, BodyPlans.DefaultClo(BodyPlans.Rabbit.Key)));
                SpendTicks(TicksPerWaitAction);
                _status = _sim.NextBodyId > rabbit ? $"Rabbit {rabbit} at ({_hoverX},{_hoverY},{_z})" : "No room for a rabbit there";
                break;
            case Key.Left: Steer(-1, 0); break;
            case Key.Right: Steer(1, 0); break;
            case Key.Up: Steer(0, -1); break;
            case Key.Down: Steer(0, 1); break;
            case Key.T when Steered() is { } eater && _sim.Grid.InBounds(_hoverX, _hoverY, _z):
                _sim.Enqueue(new EatFood(eater.Id, _hoverX, _hoverY, _z, MouthfulKg));
                _sim.Enqueue(new DrinkWater(eater.Id, _hoverX, _hoverY, _z, MouthfulKg));
                SpendTicks(TicksPerWaitAction);
                break;
            case Key.Y when Steered() is { } grown:
                // Grows another eye on the student's head, from the caster's ether.
                Cast(new CastGraft(_casterX, _casterY, _casterZ, grown.Id, "left eye", "head", $"eye {grown.Anatomy.Parts.Length}"));
                break;
            case Key.Z when Steered() is { } sleeper:
                _sim.Enqueue(new SetAsleep(sleeper.Id, !sleeper.Asleep));
                SpendTicks(TicksPerWaitAction);
                break;
            case Key.F5: Save(); break;
            case Key.F9: Load(); break;
            case >= Key.Key1 and <= Key.Key9:
                ushort material = (ushort)(key.Keycode - Key.Key1);
                if (material < _materials.Count)
                {
                    _brush = material;
                    _status = $"Brush: {_materials[material].Name}";
                }
                break;
            default: return;
        }
        QueueRedraw();
    }

    private Body? Steered()
    {
        foreach (var body in _sim.Bodies)
            if (body.Id == _bodyId) return body;
        return null;
    }

    /// <summary>A step takes half a second; the view follows the body's feet.</summary>
    private void Steer(int dx, int dy)
    {
        if (Steered() is not { } body) return;
        _sim.Enqueue(new MoveBody(body.Id, dx, dy));
        SpendTicks(Simulation.TicksPerSecond / 2);
        _z = body.Z;
    }

    /// <summary>Casting takes a tick, like any action.</summary>
    private void Cast(WorldCommand spell)
    {
        if (!_sim.Grid.InBounds(_hoverX, _hoverY, _z)) return;
        _sim.Enqueue(spell);
        _status = spell.GetType().Name;
        SpendTicks(TicksPerWaitAction);
    }

    private void HandleClick(InputEventMouseButton click)
    {
        Vector2 local = ToLocal(click.Position);
        int x = (int)MathF.Floor(local.X / TileSize);
        int y = (int)MathF.Floor(local.Y / TileSize);
        if (!_sim.Grid.InBounds(x, y, _z)) return;

        if (click.ButtonIndex == MouseButton.Left)
            _sim.Enqueue(new SetMaterial(x, y, _z, _brush));
        else if (click.ButtonIndex == MouseButton.Right)
            _sim.Enqueue(new SetTemperature(x, y, _z, click.ShiftPressed ? 100f : 1000f));
        else
            return;

        SpendTicks(TicksPerWaitAction);
    }

    private void Save()
    {
        using var file = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Write);
        file.StoreBuffer(Snapshot.ToBytes(_sim));
        _status = $"Saved tick {_sim.Tick}";
    }

    private void Load()
    {
        if (!Godot.FileAccess.FileExists(SavePath))
        {
            _status = "No save yet (F5 to save)";
            return;
        }
        try
        {
            _sim = Snapshot.FromBytes(Godot.FileAccess.GetFileAsBytes(SavePath), _materials, StandardSystems.Create());
            _z = Math.Min(_z, _sim.Grid.Depth - 1);
            _status = $"Loaded tick {_sim.Tick}";
        }
        catch (InvalidDataException e)
        {
            _status = $"Load failed: {e.Message}";
        }
    }

    public override void _Draw()
    {
        var grid = _sim.Grid;
        var lights = _lightOverlay ? LightField.AllSources(_sim) : null;
        for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
        {
            int i = grid.Index(x, y, _z);
            // Plants show however little of the tile they fill.
            ushort shownMaterial = grid.HasCondensed(i) && _materials[grid.Material[i]].IsPlant ? grid.Material[i] : grid.DisplayMaterial(i);
            var color = _heatOverlay ? HeatColor(grid.Temperature[i]) : MaterialColor(_materials[shownMaterial]);
            if (!_heatOverlay && SmokeGas >= 0) color = color.Lerp(SmokeColor, SmokeShade(grid, i));
            if (_stressOverlay && grid.HasCondensed(i) && _materials[grid.Material[i]].Phase == Phase.Solid) color = StressColor(grid, i);
            if (_etherOverlay) color = EtherColor(grid.EtherPotential(i));
            if (lights != null) color = Lit(color, LightField.Illuminance(_sim, lights, x + 0.5, y + 0.5, _z + 0.5));
            DrawRect(new Rect2(x * TileSize, y * TileSize, TileSize - 1, TileSize - 1), color);
            if (grid.Flame[i] != 0)
                DrawCircle(new Vector2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize), TileSize * 0.22f, FlameColor);
        }
        if (_casterZ == _z)
            DrawArc(new Vector2((_casterX + 0.5f) * TileSize, (_casterY + 0.5f) * TileSize), TileSize * 0.4f, 0f, MathF.Tau, 16, CasterColor, 2f);
        foreach (var body in _sim.Bodies)
        {
            if (_z < body.Z || _z >= body.Z + body.HeightTiles) continue;
            var centre = new Vector2((body.X + 0.5f) * TileSize, (body.Y + 0.5f) * TileSize);
            bool wild = body.Plan.Instinctive;
            var color = !body.Alive ? new Color(0.5f, 0.5f, 0.5f) : !body.Conscious ? new Color(0.95f, 0.5f, 0.3f)
                : wild ? new Color(0.62f, 0.5f, 0.38f) : new Color(0.95f, 0.8f, 0.65f);
            var anatomy = body.Anatomy;
            bool head = anatomy.BrainPart >= 0 && _z - body.Z == anatomy.Parts[anatomy.OuterOf(anatomy.BrainPart)].Dz;
            DrawCircle(centre, TileSize * (body.IsRemains ? 0.2f : wild ? 0.25f : head ? 0.3f : 0.4f), color);
            if (body.Id == _bodyId) DrawArc(centre, TileSize * 0.48f, 0f, MathF.Tau, 16, Colors.White, 1.5f);
            // Which way its eyes look.
            if (!body.IsRemains && body.Alive)
                DrawLine(centre, centre + new Vector2(body.FacingX, body.FacingY).Normalized() * TileSize * 0.5f, Colors.Black, 2f);
        }
        foreach (var bolt in _sim.Projectiles)
            if ((int)Math.Floor(bolt.Z) == _z)
                DrawCircle(new Vector2((float)bolt.X * TileSize, (float)bolt.Y * TileSize), TileSize * 0.3f, FlameColor);
        if (_airOverlay) DrawAir(grid);
        DrawSounds();

        var font = ThemeDB.FallbackFont;
        float hudY = grid.Height * TileSize + 22;
        string mode = (_etherOverlay ? "ether" : _stressOverlay ? "stress" : _heatOverlay ? "heat" : "materials") + (_airOverlay ? " + air" : "") + (_lightOverlay ? " + light" : "");
        DrawString(font, new Vector2(4, hudY),
            $"seed '{_sim.Seed}'   tick {_sim.Tick} ({_sim.ElapsedSeconds:0.0} s)   z {_z}/{grid.Depth - 1}   view {mode}   sky {_sim.SkyIlluminanceLux:0.###} lux   brush {_materials[_brush].Name}",
            fontSize: 16);
        DrawString(font, new Vector2(4, hudY + 22),
            "Space 1 tick | Enter 1 s | W 1 min | H 10 min | Q/E z | Tab heat | A air | S stress | M ether | L light | N day/moon/stars | C caster F fireball X freeze V heat G charge B conjure | P student, arrows step, O face, T eat/drink, Z sleep, Y grow an eye | K grass J rabbit | 1-9 brush | LMB place | RMB 1000 K (Shift 100 K) | F5 save | F9 load | R new seed",
            fontSize: 13, modulate: new Color(0.7f, 0.7f, 0.7f));
        if (_status.Length > 0)
            DrawString(font, new Vector2(4, hudY + 42), _status, fontSize: 13, modulate: new Color(1f, 0.85f, 0.4f));
        if (grid.InBounds(_hoverX, _hoverY, _z))
            DrawString(font, new Vector2(4, hudY + 62), Describe(grid, grid.Index(_hoverX, _hoverY, _z)), fontSize: 13);
        var shown = BodyAt(_hoverX, _hoverY) ?? Steered();
        if (shown != null)
        {
            DrawString(font, new Vector2(4, hudY + 82), DescribeBody(shown), fontSize: 13, modulate: new Color(0.95f, 0.85f, 0.75f));
            DrawString(font, new Vector2(4, hudY + 102), DescribeSenses(shown), fontSize: 13, modulate: new Color(0.75f, 0.85f, 0.95f));
        }
    }

    /// <summary>One line on what a tile holds: its solid or liquid, its gases, temperature and pressure.</summary>
    private string Describe(WorldGrid grid, int i)
    {
        var (x, y, z) = grid.Coordinates(i);
        string held = grid.HasCondensed(i)
            ? $"{_materials[grid.Material[i]].Name} {grid.Mass[i]:0.###} kg ({grid.FillFraction(i):P0} full)"
            : "no solid or liquid";
        var gases = new System.Text.StringBuilder();
        for (int s = 0; s < _materials.GasCount; s++)
        {
            double m = grid.GasMass[s][i];
            if (m > 0) gases.Append($", {_materials[_materials.GasIds[s]].Name} {m * 1000:0.##} g");
        }
        double celsius = grid.Temperature[i] - 273.15;
        string air = grid.HasTwoTemperatures(i) ? $" (gas {grid.GasTemperature[i] - 273.15:0.0} °C)" : "";
        string burning = grid.Flame[i] != 0 ? "  burning" : "";
        string soil = EcologySystem.SoilVolume(grid, i) > 0.0 ? $"  soil water {EcologySystem.Moisture(grid, i):P0} by volume" : "";
        string plant = grid.HasCondensed(i) && _materials[grid.Material[i]].IsPlant
            ? $"  {grid.Mass[i] * EcologySystem.DryShare(_materials[grid.Material[i]]) * 1000:0} g dry{(grid.PlantStress[i] > 0.01 ? $", stressed {grid.PlantStress[i]:P0}" : "")}"
            : "";
        string structure = (grid.Structure[i] & WorldGrid.LooseBit) == 0 || !grid.HasCondensed(i) ? ""
            : grid.FallSpeed[i] > 0.0 ? "  falling" : "  loose";
        float load = Structure()?.UtilisationAt(i) ?? 0f;
        string stress = load > 0f ? $"  load {load:P0} of strength" : "";
        string ether = $"  ether {grid.EtherPotential(i) / 1e6:0.##} MJ/m³";
        double lux = LightField.Illuminance(_sim, LightField.AllSources(_sim), x + 0.5, y + 0.5, z + 0.5);
        return $"({x},{y},{z})  {held}{gases}  {celsius:0.0} °C{air}  {FluidSystem.PressureAt(grid, i) / 1000:0.###} kPa{burning}{soil}{plant}{structure}{stress}{ether}  light {lux:0.###} lux";
    }

    private Body? BodyAt(int x, int y)
    {
        foreach (var body in _sim.Bodies)
            if (body.X == x && body.Y == y && _z >= body.Z && _z < body.Z + body.HeightTiles) return body;
        return null;
    }

    /// <summary>One line on how a body is: its core and skin, what it feels, and the worst of its injuries.</summary>
    private string DescribeBody(Body b)
    {
        var parts = b.Anatomy.Parts;
        if (b.IsRemains)
        {
            string names = string.Join(", ", Array.ConvertAll(parts, part => part.Name));
            return $"Remains ({names})  {Physiology.Kg(b):0.00} kg  {b.Temperature[b.Anatomy.Inner(0)] - 273.15:0.0} °C";
        }
        string state = !b.Alive ? $"dead ({b.Cause})" : b.HeartStopped ? "heart stopped" : b.Asleep ? "asleep" : b.Conscious ? "awake" : "unconscious";
        int worst = 0;
        double worstHurt = 0.0;
        for (int p = 0; p < parts.Length; p++)
        {
            double hurt = Math.Max(1.0 - b.Health(p), b.Necrosis(b.Anatomy.LayerStart[p]));
            if (hurt > worstHurt) { worstHurt = hurt; worst = p; }
        }
        string injury = worstHurt > 0.05 ? $"  worst hurt: {parts[worst].Name} {Math.Min(worstHurt, 1.0):P0}" : "";
        int missing = b.Plan.Parts.Length - parts.Length;
        string body = (missing > 0 ? $"  {missing} parts missing" : missing < 0 ? $"  {-missing} extra parts" : "")
                      + (b.Vision < 0.99 && b.Conscious ? $"  sight {b.Vision:P0}" : "") + (b.Toxins > 0.01 ? $"  poisoned {b.Toxins:P0}" : "");
        string who = b.Plan.Instinctive ? $"{b.Plan.Name} {b.Id}" : $"Student {b.Id}";
        if (b.Plan.Instinctive && b.Conscious) state = InstinctSystem.Fright(_sim, b, out _, out _) ? "fleeing" : b.Hunger >= InstinctSystem.GrazeHunger ? "grazing" : "awake";
        return $"{who} {state}  core {b.BloodTemperature - 273.15:0.0} °C  skin {b.MeanSkinTemperature - 273.15:0.0} °C  " +
               $"{b.MetabolicW:0} W  oxygen {b.OxygenFraction:P0}  blood {b.BloodFraction:P0}  hunger {b.Hunger:P0}  thirst {b.Thirst:P0}  fatigue {b.Fatigue:P0}" +
               $"{(b.ShiverW > 1 ? "  shivering" : "")}{(b.SweatKgPerS > 1e-6 ? "  sweating" : "")}{injury}{body}";
    }

    /// <summary>One line on what a body has noticed lately: the last few things it saw or heard, newest first.</summary>
    private string DescribeSenses(Body b)
    {
        if (b.IsRemains) return "";
        var recent = new System.Collections.Generic.List<Percept>(b.Percepts);
        recent.Sort((p, q) => q.LastTick.CompareTo(p.LastTick));
        var text = new System.Text.StringBuilder($"Sight {b.Vision:P0}, hearing {b.Hearing:P0}, eyes adapted to {b.Adaptation:0.####} cd/m².  Noticed: ");
        int shown = 0;
        foreach (var p in recent)
        {
            if (shown++ == 5) break;
            double ago = (_sim.Tick - p.LastTick) * Simulation.SecondsPerTick;
            string what = p.Kind switch
            {
                PerceptKind.Body => $"saw body {p.Subject}",
                PerceptKind.Bolt => "saw a bolt fly",
                PerceptKind.Fire => "saw fire",
                PerceptKind.Glow => "saw a glow",
                _ => $"heard {((SoundKind)p.Detail).ToString().ToLowerInvariant()} {p.Level:0} dB(A)",
            };
            text.Append($"{what} at ({p.X},{p.Y},{p.Z}) {ago:0.#} s ago; ");
        }
        if (shown == 0) text.Append("nothing");
        return text.ToString();
    }

    /// <summary>Rings where the last action's sounds were made, wider for louder, on this level.</summary>
    private void DrawSounds()
    {
        foreach (var sound in _heard)
        {
            if ((int)Math.Floor(sound.Z) != _z) continue;
            // Sound power level, dB re 1 pW: a footstep about 70, a fireball's strike about 170.
            double level = 10.0 * Math.Log10(Math.Max(sound.Joules / Simulation.SecondsPerTick, 1e-12) / 1e-12);
            float radius = TileSize * (float)Math.Clamp((level - 50.0) / 25.0, 0.3, 6.0);
            DrawArc(new Vector2((float)sound.X * TileSize, (float)sound.Y * TileSize), radius, 0f, MathF.Tau, 24, new Color(1f, 1f, 1f, 0.7f), 1.5f);
        }
    }

    /// <summary>A tile's colour dimmed by how brightly it is lit: full at 1000 lux and above, near black by a thousandth.</summary>
    private static Color Lit(Color color, double lux)
    {
        float f = (float)Math.Clamp((Math.Log10(Math.Max(lux, 1e-6)) + 3.0) / 6.0, 0.04, 1.0);
        return new Color(color.R * f, color.G * f, color.B * f);
    }

    private StructureSystem? Structure()
    {
        foreach (var system in _sim.Systems)
            if (system is StructureSystem structure) return structure;
        return null;
    }

    /// <summary>Solids by how close their most loaded bond is to breaking: green at rest, yellow at half, red at breaking; rubble grey.</summary>
    private Color StressColor(WorldGrid grid, int i)
    {
        if ((grid.Structure[i] & WorldGrid.LooseBit) != 0) return new Color(0.45f, 0.45f, 0.45f);
        float load = Math.Clamp(Structure()?.UtilisationAt(i) ?? 0f, 0f, 1f);
        return load < 0.5f
            ? new Color(0.1f, 0.6f, 0.2f).Lerp(new Color(0.95f, 0.85f, 0.1f), load / 0.5f)
            : new Color(0.95f, 0.85f, 0.1f).Lerp(new Color(0.9f, 0.1f, 0.1f), (load - 0.5f) / 0.5f);
    }

    /// <summary>Ether potential on a log scale: near-black at 0.1 MJ/m³, violet at 3, white by 100 and above.</summary>
    private static Color EtherColor(double potential)
    {
        float t = (float)Math.Clamp((Math.Log10(Math.Max(potential, 1.0)) - 5.0) / 3.0, 0.0, 1.0);
        return t < 0.5f
            ? new Color(0.03f, 0.02f, 0.08f).Lerp(new Color(0.55f, 0.2f, 0.85f), t / 0.5f)
            : new Color(0.55f, 0.2f, 0.85f).Lerp(new Color(1f, 0.95f, 1f), (t - 0.5f) / 0.5f);
    }

    private static readonly Color SmokeColor = new(0.15f, 0.15f, 0.15f);
    private static readonly Color FlameColor = new(1f, 0.55f, 0.1f);
    private static readonly Color CasterColor = new(0.85f, 0.6f, 1f);

    /// <summary>How dark to draw a tile's smoke: none at 0, nearly black by 2 g of soot in the tile.</summary>
    private float SmokeShade(WorldGrid grid, int i)
    {
        double grams = grid.GasMass[SmokeGas][i] * 1000.0;
        return (float)(0.85 * grams / (grams + 0.5));
    }

    /// <summary>
    /// Air currents in this slice: a line from each tile's centre along its sideways flow
    /// (full tile length = 1 m/s), and a dot that is orange for rising air and blue for sinking.
    /// </summary>
    private void DrawAir(WorldGrid grid)
    {
        double[] u = grid.AirVelocity[0], v = grid.AirVelocity[1], w = grid.AirVelocity[2];
        int plane = grid.Width * grid.Height;
        for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
        {
            int i = grid.Index(x, y, _z);
            // Average each tile's two faces to get the flow at its centre.
            double cu = 0.5 * (u[i] + (x > 0 ? u[i - 1] : 0.0));
            double cv = 0.5 * (v[i] + (y > 0 ? v[i - grid.Width] : 0.0));
            double cw = 0.5 * (w[i] + (_z > 0 ? w[i - plane] : 0.0));
            var centre = new Vector2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize);
            if (cu != 0.0 || cv != 0.0)
                DrawLine(centre, centre + new Vector2((float)cu, (float)cv) * TileSize, new Color(0.9f, 0.9f, 1f), 1.5f);
            if (Math.Abs(cw) > 0.01)
                DrawCircle(centre, 1.5f + (float)Math.Min(Math.Abs(cw), 1.0) * 3f,
                    cw > 0 ? new Color(1f, 0.6f, 0.2f) : new Color(0.3f, 0.6f, 1f));
        }
    }

    private static Color MaterialColor(MaterialDef def) =>
        new(((def.Color >> 16) & 0xFF) / 255f, ((def.Color >> 8) & 0xFF) / 255f, (def.Color & 0xFF) / 255f);

    /// <summary>Blue at 0 °C and below, through black at room temperature, to red, then white-hot at 1500 K.</summary>
    private static Color HeatColor(double kelvin)
    {
        const double cold = 273.15, room = BuiltInMaterials.RoomTemperatureK, hot = 1500.0;
        if (kelvin <= room)
        {
            float t = (float)Math.Clamp((room - kelvin) / (room - cold + 100.0), 0.0, 1.0);
            return new Color(0.05f, 0.05f, 0.1f).Lerp(new Color(0.3f, 0.6f, 1f), t);
        }
        float h = (float)Math.Clamp((kelvin - room) / (hot - room), 0.0, 1.0);
        return h < 0.6f
            ? new Color(0.05f, 0.05f, 0.1f).Lerp(new Color(1f, 0.15f, 0.05f), h / 0.6f)
            : new Color(1f, 0.15f, 0.05f).Lerp(Colors.White, (h - 0.6f) / 0.4f);
    }
}
