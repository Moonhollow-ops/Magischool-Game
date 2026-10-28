using System;
using System.IO;
using Godot;
using Magischool.Sim.Core;
using Magischool.Sim.Fluids;
using Magischool.Sim.Magic;
using Magischool.Sim.Materials;
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
    private string _status = "";
    private int _hoverX = -1, _hoverY = -1;
    private int _casterX = 2, _casterY = 2, _casterZ = TestRoomGenerator.FloorZ;

    private const double FireballJoules = 2_000_000;
    private const double FreezeJoules = 20_000_000;
    private const double HeatJoules = 5_000_000;
    private const double ChargeJoules = 20_000_000;
    private const double ConjureKg = 5.0;

    public override void _Ready()
    {
        NewWorld("moonhollow");
    }

    private void NewWorld(string seed)
    {
        _sim = TestRoomGenerator.Create(seed, _materials, systems: StandardSystems.Create());
        _z = Math.Min(TestRoomGenerator.FloorZ, _sim.Grid.Depth - 1);
        _status = $"New world '{seed}'";
        QueueRedraw();
    }

    /// <summary>Every player action spends world time. Commands queued by the action apply on the first tick.</summary>
    private void SpendTicks(int ticks)
    {
        _sim.Step(ticks);
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
            case Key.F5: Save(); break;
            case Key.F9: Load(); break;
            case >= Key.Key1 and <= Key.Key9:
                ushort id = (ushort)(key.Keycode - Key.Key1);
                if (id < _materials.Count)
                {
                    _brush = id;
                    _status = $"Brush: {_materials[id].Name}";
                }
                break;
            default: return;
        }
        QueueRedraw();
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
        for (int y = 0; y < grid.Height; y++)
        for (int x = 0; x < grid.Width; x++)
        {
            int i = grid.Index(x, y, _z);
            var color = _heatOverlay ? HeatColor(grid.Temperature[i]) : MaterialColor(_materials[grid.DisplayMaterial(i)]);
            if (!_heatOverlay && SmokeGas >= 0) color = color.Lerp(SmokeColor, SmokeShade(grid, i));
            if (_stressOverlay && grid.HasCondensed(i) && _materials[grid.Material[i]].Phase == Phase.Solid) color = StressColor(grid, i);
            if (_etherOverlay) color = EtherColor(grid.EtherPotential(i));
            DrawRect(new Rect2(x * TileSize, y * TileSize, TileSize - 1, TileSize - 1), color);
            if (grid.Flame[i] != 0)
                DrawCircle(new Vector2((x + 0.5f) * TileSize, (y + 0.5f) * TileSize), TileSize * 0.22f, FlameColor);
        }
        if (_casterZ == _z)
            DrawArc(new Vector2((_casterX + 0.5f) * TileSize, (_casterY + 0.5f) * TileSize), TileSize * 0.4f, 0f, MathF.Tau, 16, CasterColor, 2f);
        foreach (var bolt in _sim.Projectiles)
            if ((int)Math.Floor(bolt.Z) == _z)
                DrawCircle(new Vector2((float)bolt.X * TileSize, (float)bolt.Y * TileSize), TileSize * 0.3f, FlameColor);
        if (_airOverlay) DrawAir(grid);

        var font = ThemeDB.FallbackFont;
        float hudY = grid.Height * TileSize + 22;
        string mode = (_etherOverlay ? "ether" : _stressOverlay ? "stress" : _heatOverlay ? "heat" : "materials") + (_airOverlay ? " + air" : "");
        DrawString(font, new Vector2(4, hudY),
            $"seed '{_sim.Seed}'   tick {_sim.Tick} ({_sim.ElapsedSeconds:0.0} s)   z {_z}/{grid.Depth - 1}   view {mode}   brush {_materials[_brush].Name}",
            fontSize: 16);
        DrawString(font, new Vector2(4, hudY + 22),
            "Space 1 tick | Enter 1 s | W 1 min | H 10 min | Q/E z | Tab heat | A air | S stress | M ether | C caster F fireball X freeze V heat G charge B conjure | 1-9 brush | LMB place | RMB 1000 K (Shift 100 K) | F5 save | F9 load | R new seed",
            fontSize: 13, modulate: new Color(0.7f, 0.7f, 0.7f));
        if (_status.Length > 0)
            DrawString(font, new Vector2(4, hudY + 42), _status, fontSize: 13, modulate: new Color(1f, 0.85f, 0.4f));
        if (grid.InBounds(_hoverX, _hoverY, _z))
            DrawString(font, new Vector2(4, hudY + 62), Describe(grid, grid.Index(_hoverX, _hoverY, _z)), fontSize: 13);
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
        string structure = (grid.Structure[i] & WorldGrid.LooseBit) == 0 || !grid.HasCondensed(i) ? ""
            : grid.FallSpeed[i] > 0.0 ? "  falling" : "  loose";
        float load = Structure()?.UtilisationAt(i) ?? 0f;
        string stress = load > 0f ? $"  load {load:P0} of strength" : "";
        string ether = $"  ether {grid.EtherPotential(i) / 1e6:0.##} MJ/m³";
        return $"({x},{y},{z})  {held}{gases}  {celsius:0.0} °C{air}  {FluidSystem.PressureAt(grid, i) / 1000:0.###} kPa{burning}{structure}{stress}{ether}";
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
