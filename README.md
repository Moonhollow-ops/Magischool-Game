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
dotnet run --project tools/Magischool.Cli -- --ticks 600 --heat   # temperatures after 1 min
dotnet run --project tools/Magischool.Cli -c Release -- --size 400x400x12 --bench [--no-sleep]
```

Viewer: open `game/project.godot` in Godot 4.7 .NET and press Play.

It opens in **play mode**: you are a student standing outside the old schoolroom, and you see
only what they can see. Tiles in sight are drawn as they are, tiles seen before are drawn dim as
you remember them, and the rest is dark. Looking never tells you a temperature; touch tells warm
from cold within arm's reach, and the sense spell reads a tile (and anyone in it) in full.

| Play mode | Action |
| --- | --- |
| Left click | Walk there (a step each half second of world time; Esc stops) |
| Right click | Act on the tile: look, walk, eat, drink, cast sense / heat / freeze / fireball, sleep, wait |
| Arrow keys | One step |
| Space / Enter | Wait 1 second / 1 minute |
| Z | Sleep or wake |
| Q / E | Look a level down / up |
| Tab | Log panel: everything, or the battle log only |
| F5 / F9 | Save / load |
| F1 | Switch to the debug view below, which shows everything |

The log down the right side says what happened, as the student knows it: their own hurts with
what they could tell caused them (fire beside them, a fireball bursting close, a fall), bodies
coming into view, fire, crashes and blasts by direction, and in red the battle log: spells cast,
where fireballs burst, and the hurts of anyone the student can see.

| Debug view | Action |
| --- | --- |
| Space / Enter | Wait 1 tick / wait 1 second (10 ticks) |
| W / H | Wait 1 minute / wait 10 minutes |
| Q / E | Down / up one z-level |
| Tab | Toggle the heat overlay |
| A | Toggle air currents (lines: sideways flow, dots: rising orange, sinking blue) |
| S | Toggle stress: solids from green (unloaded) through yellow to red (breaking); rubble grey |
| 1 to 9 | Pick a material brush |
| Left click | Place the brush material (costs 1 tick) |
| Right click | Set a tile to 1000 K (Shift: 100 K). Debug only: ignores energy conservation |
| F5 / F9 | Save / load |
| R | New random seed |
| P | A student stands on the hovered tile and becomes the one you steer (arrows step, T eat/drink, Z sleep) |
| L | Toggle the light view: tiles dimmed by how brightly they are lit (full at 1000 lux, near black by 0.001) |
| N | Sky: day (10,000 lux), full moon, starlight |
| O | The steered student turns to face the hovered tile |
| K | Plant a grass seedling on the hovered tile (open air over soil) |
| J | Let a rabbit loose on the hovered tile |
| Hover | Read a tile: its solid or liquid, gases, temperature, pressure, whether it is burning, loose or falling, its load, its soil water and its grass |

Smoke darkens the tiles it is in, and an orange dot marks wood that is burning. A black line shows
which way each student and rabbit faces (rabbits are the small brown dots), white rings show the sounds the last action made, and the last line
of the readout lists what the hovered or steered student last saw and heard.

## Simulation rules

- The world only changes in whole ticks of 0.1 s, and only when an action
  spends them. Same start + same actions = the same world, every time.
- All outside input goes through `WorldCommand`s. Never edit the grid directly
  from game code.
- All randomness comes from `Simulation.Rng` or a stream derived with
  `SeedHash.Derive`. Never use `System.Random` or `string.GetHashCode()`.
- World data lives in flat arrays on `WorldGrid`, one per property.
