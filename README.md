# Magischool

A systemic magic-school sandbox: Stardew Valley's life sim on top of a
Dwarf Fortress-depth physics simulation. The simulation is the game.

- Decisions so far: [docs/DECISIONS.md](docs/DECISIONS.md)
- Physics build order: [docs/ROADMAP.md](docs/ROADMAP.md)
- Open questions: [docs/OPEN_QUESTIONS.md](docs/OPEN_QUESTIONS.md)

## Layout

| Path | What it is |
| --- | --- |
| `sim/Magischool.Sim` | The world simulation. Plain C#, no engine code. |
| `sim/Magischool.Sim.Tests` | Headless tests, including determinism checks. |
| `tools/Magischool.Cli` | Console runner that prints a world slice as text. |
| `game/` | Godot 4.7 (.NET) project: a bare viewer that draws the sim. |

## Running

Requires the .NET 8 SDK. For the viewer, the **.NET build** of Godot 4.7.

```sh
dotnet test                                    # run all sim tests
dotnet run --project tools/Magischool.Cli -- --seed moon --z 1
```

Viewer: open `game/project.godot` in Godot 4.7 .NET and press Play.

| Key | Action |
| --- | --- |
| Space / Enter | Wait 1 tick / wait 1 second (10 ticks) |
| Q / E | Down / up one z-level |
| Tab | Toggle the heat overlay |
| 1 to 9 | Pick a material brush |
| Left click | Place the brush material (costs 1 tick) |
| Right click | Heat a tile to 1000 K (Shift: chill to 100 K) |
| F5 / F9 | Save / load |
| R | New random seed |

## Simulation rules

- The world only changes in whole ticks of 0.1 s, and only when an action
  spends them. Same start + same actions = the same world, every time.
- All outside input goes through `WorldCommand`s. Never edit the grid directly
  from game code.
- All randomness comes from `Simulation.Rng` or a stream derived with
  `SeedHash.Derive`. Never use `System.Random` or `string.GetHashCode()`.
- World data lives in flat arrays on `WorldGrid`, one per property.
