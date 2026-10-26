using System;
using System.IO;
using Godot;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.Persistence;
using Magischool.Sim.WorldGen;

namespace Magischool.Game;

/// <summary>
/// Thin viewer over the simulation: one coloured square per tile of the
/// current z-slice. The world only advances when an action spends ticks.
///
/// Space: wait 1 tick · Enter: wait 1 s · W: wait 1 min · H: wait 10 min · Q/E: z down/up
/// Tab: heat overlay · 1-9: pick material · Left click: place it
/// Right click: set tile to 1000 K (Shift: 100 K), a debug tool that ignores conservation
/// F5: save · F9: load · R: new seed
/// </summary>
public partial class WorldView : Node2D
{
    private const int TileSize = 20;
    private const int TicksPerWaitAction = 1;
    private const int TicksPerLongWaitAction = Simulation.TicksPerSecond;
    private const int TicksPerMinute = 60 * Simulation.TicksPerSecond;
    private const string SavePath = "user://world.snap";

    private readonly MaterialTable _materials = BuiltInMaterials.CreateTable();
    private Simulation _sim = null!;
    private int _z = TestRoomGenerator.FloorZ;
    private ushort _brush = 1;
    private bool _heatOverlay;
    private string _status = "";

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
            case Key.R: NewWorld($"seed-{Time.GetTicksMsec()}"); break;
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
            var color = _heatOverlay ? HeatColor(grid.Temperature[i]) : MaterialColor(_materials[grid.Material[i]]);
            DrawRect(new Rect2(x * TileSize, y * TileSize, TileSize - 1, TileSize - 1), color);
        }

        var font = ThemeDB.FallbackFont;
        float hudY = grid.Height * TileSize + 22;
        string mode = _heatOverlay ? "heat" : "materials";
        DrawString(font, new Vector2(4, hudY),
            $"seed '{_sim.Seed}'   tick {_sim.Tick} ({_sim.ElapsedSeconds:0.0} s)   z {_z}/{grid.Depth - 1}   view {mode}   brush {_materials[_brush].Name}",
            fontSize: 16);
        DrawString(font, new Vector2(4, hudY + 22),
            "Space 1 tick | Enter 1 s | W 1 min | H 10 min | Q/E z | Tab heat | 1-9 brush | LMB place | RMB 1000 K (Shift 100 K) | F5 save | F9 load | R new seed",
            fontSize: 13, modulate: new Color(0.7f, 0.7f, 0.7f));
        if (_status.Length > 0)
            DrawString(font, new Vector2(4, hudY + 42), _status, fontSize: 13, modulate: new Color(1f, 0.85f, 0.4f));
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
