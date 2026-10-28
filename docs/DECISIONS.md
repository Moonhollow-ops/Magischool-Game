# Design decisions

The running record of what has been decided, newest first. Anything here is
settled until the owner says otherwise; open questions live in
[OPEN_QUESTIONS.md](OPEN_QUESTIONS.md).

## 2026-10-28: Where spell energy comes from

From the owner: spell energy exists in the environment, and different places
hold different amounts of potential. A caster gathers magic from the
environment to cast. Ordinary casting does not use that magic up; only large
ritual magic does.

How the ether field (layer 7) honours this while still conserving energy:

- Every place has an ether potential set by its surroundings (materials,
  geography, ley lines and the like), so some places are rich in magic and
  others poor.
- Casting draws charge from the field around the caster, and that charge is
  what becomes the spell's heat, force or matter, counted joule for joule.
- The field refills from a deep reservoir beneath the world, the way the open
  sky refills the air. Each place refills toward its own potential at a
  limited rate. Everything that crosses that boundary is counted, so the
  total of world plus reservoir never changes.
- An ordinary spell takes far less than a place refills, so the place's
  magic is effectively never used up.
- A large ritual draws faster than the place can refill, so it drains the
  local field, and that place stays depleted until it recovers.

## 2026-10-27: Magic is thermodynamics

From the owner: spells are never scripted effects. A spell is energy and
matter put into the physics that already exists, and nothing else.

- A fireball is a fast projectile that raises the temperature of the
  half-metre tiles it hits. Things catch fire because they pass their own
  ignition points, and the heat rises on the air currents like any other heat.
  There is no "50 fire damage".
- A freeze spell is a heat sink that pulls joules out of a tile. Water freezes
  through its latent heat of fusion, expands about 9%, and whether the pipe or
  the stone around it cracks depends on that material's tensile strength.
- Mass and energy are conserved absolutely, spells included. A spell moves
  energy into or out of the world through the ether field (layer 7), which is
  counted in the same audit, so the total never changes.

## 2026-10-27: Target hardware

The owner develops and tests on an RTX 4070 Ti Super (16 GB) with a 12-core CPU. Physics stays on the
CPU for now (exact determinism, double precision); GPU compute is the
reserved option if a layer such as large-scale fluids outgrows the CPU.

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
- **Fluids and gases (layer 4):** a tile holds at most one solid or liquid
  (with its mass) plus a gas mixture (a mass per gas) in whatever volume is
  left. Gas pressure comes from the ideal gas law. Liquids fall, then spread
  sideways when resting on something, slowed by viscosity, until they are a
  5 mm film. Liquid deeper down is held 1% overfull per tile of depth as a
  stand-in for hydrostatic pressure, which pushes it up and sideways, so
  connected vessels level out. Gas flows down pressure differences allowing for
  its weight, rises by trading places when lighter, and mixes slowly.
  Different liquids don't yet displace each other, boiling points don't yet
  depend on pressure, and puddles don't yet evaporate below boiling.
- **Air currents (layer 4b):** air has a velocity on every tile face (a
  staggered grid, saved with the world). Each tick, air lighter than still sky
  air at the same pressure accelerates upward, drag slows every current with a
  2 s time constant, and a pressure solve (20 red-black over-relaxation sweeps,
  only near air that is out of balance) makes as much air leave each tile as
  enters it. Currents carry gas, and the heat and mixture in it, from the
  upwind tile. Between two open-air tiles the thermal layer now uses plain
  conduction, because the currents do the carrying the old convection
  stand-in used to fake.
- **Open sky:** the top of the world opens onto endless still air at room
  temperature; the sides and floor are sealed. Without this, warming any air
  raised the pressure of the whole sealed world and woke every chunk.
  `Atmosphere.Balance` lays down still air at the right pressure for its height
  so a new world starts at rest. Lab tests seal the sky to count every joule.
- **Combustion (layer 5):** air is a fixed mixture, 23.14% oxygen by mass,
  and the rest is nitrogen; nitrogen, carbon dioxide and smoke are gases of
  their own. A fuel lists its heat of combustion, the oxygen it needs per
  kilogram and its products (wood burns as cellulose: 15 MJ/kg, 1.17 kg of
  oxygen, giving carbon dioxide, steam and 1.5% smoke). The energy audit
  gives each fuel a chemical offset, so the heat of combustion is already in
  the total before it is released. Burning is computed per pair of fuel tile
  and gas tile, identically from both sides, at 12 g/(m²·s) in full air,
  slowing as the oxygen thins and stopping below 14.5% by mass. Each fuel
  face has a 1 cm surface layer with its own temperature, warmed by
  convection (10 W/(m²·K)) and radiation from its gas and by 25 kW/m² from any
  flame in that gas, and cooled by conduction into the fuel; the heat it takes
  in comes out of the gas. A face burns while that surface is at or above the
  ignition point. Flames and surface temperatures are saved and hashed.
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
