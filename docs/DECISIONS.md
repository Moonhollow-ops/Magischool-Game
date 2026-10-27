# Design decisions

The running record of what has been decided, newest first. Anything here is
settled until the owner says otherwise; open questions live in
[OPEN_QUESTIONS.md](OPEN_QUESTIONS.md).

## 2026-10-26: Fidelity over everything

**This is the owner's magnum opus. Time and money are not a factor; only the
world matters.** When a choice trades simulation fidelity against development
time or compute cost, choose fidelity, and solve the cost with engineering
(active-region simulation, multithreading, smarter algorithms), not by
simplifying the physics.

**Tiles are 0.5 m cubes.** A person is about 4 tiles tall; a pipe, door or
hinge is about one tile. Chosen over 1 m (too coarse: freezing one tile of
water took as much energy as 80 kg of TNT) and 0.25 m (64× the cost of 1 m).

## 2026-10-26: The school day and learning spells

**Classes follow a real-world schedule.** A player who sleeps at normal times
wakes at 6 am and has a couple of hours before class. Each class takes about
two in-game hours, with 3 to 4 classes per day.

**Spells are learned by passing tests.** Classes end in tests, and a spell is
learned only when its test is passed. A player may skip every class and still
learn spells by passing multiple-choice tests about the magical world.

**Long stretches of time pass through activities.** Study (raises the
attributes you choose to study), Hang out (time with an NPC), and a few other
time-consuming activities. Exploring dungeons and the world also spends ticks.
The world keeps simulating during an activity; the activity is just how the
player spends that time.

**Tests are tied to classes.** Professors teach how the world's mechanics work
as well as the test answers, so attending class is the main route to passing.

**Physics first, story later.** Story beats are irrelevant until there is a
unified theory of the magical and mundane physics of the world.

## 2026-10-26: Direction from the owner

**The simulation is the game.** World physics come before story. The game
should work even with no story at all, as long as the world feels like a place
you can live in. Graphics stay as bare as possible: coloured squares or glyphs.

**Build it one scene at a time.** Each physics layer gets its own bare test
scene and is proven before the next layer goes on (see [ROADMAP.md](ROADMAP.md)).

**Time is driven by actions.** The world advances only when the player acts,
and each action costs ticks (Dwarf Fortress Adventure Mode style). One tick is
0.1 s of world time; physics always steps in whole ticks, so the same start
plus the same actions always gives the same world.

**Combat is magic physics.** No separate combat system. Each semester's spells
add new ways to change the world's physics, and that is how you fight.

**The slum after Chapter 1 is a small simulated city.** No world map. The city
has specific quests that improve life for everyone in the slums.

**Magic must stay secret.** If the wrong people learn of it, a team of wizards
comes to imprison or kill the player character.

**NPCs have an area of awareness** based on their skills and the environment
(light, noise, line of sight, and so on).

**Failure is loose and felt, not scripted.** Failure is not a story branch or a
game-over screen. It is how the player feels about how their run went. No hand
holding.

**Deferred:** economy, specific NPCs.

## 2026-10-26: Technical foundations

- **Engine:** Godot 4.7 (.NET build). The simulation is a separate C# library
  (`sim/Magischool.Sim`) with no Godot dependency. Godot is only a viewer and
  input layer, so the sim can be tested headless and stays deterministic.
- **World grid:** 3D (x, y, z) from day one, stored as flat arrays, one array
  per property. No per-tile objects.
- **Units:** real SI units (kg, K, J, MPa) for mundane physics; invented but
  consistent units for the arcane field.
- **Randomness:** one master seed string. Each subsystem derives its own stream
  (`SeedHash.Derive(seed, "worldgen")`) so adding randomness to one system never
  changes another's rolls. PCG32 only; never `System.Random` or
  `string.GetHashCode()`.
- **Heat (layer 2):** pairwise face conductances, so energy is conserved to
  double precision. Fluids use a natural-convection film (air 10, water
  500 W/(m²·K)) until real fluid flow exists. Temperatures are stored as
  doubles because floats silently lost slow conduction. Radiation is not
  modelled yet.
- **Mass and phase change (layer 3):** every tile stores the mass of its
  material, so melting, freezing and boiling keep mass exactly. A tile may be
  under-full (melted ice) or over-full (frozen water), which the fluids and
  structure layers act on. Latent heat is stored per tile while a transition
  is under way, and `EnergyAudit` counts total energy including latent heat.
- **Active regions:** the world is split into 16 x 16 x 8 tile chunks. A chunk
  sleeps when every temperature difference in and around it is under 1 mK and
  no phase change is under way, and wakes when a neighbour pushes on it or a
  command touches it. Faces to a sleeping chunk carry nothing from either side,
  so energy stays exactly conserved. Sleep state is saved and hashed.
- **Parallelism:** physics loops run per chunk on all cores. Each tile only
  writes itself from the start-of-tick state, so results are identical for any
  thread count (tested).
- **Input:** all outside changes enter the world as `WorldCommand`s applied at
  the start of a tick. That is what makes replays exact.
