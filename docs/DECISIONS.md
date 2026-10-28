# Design decisions

The running record of what has been decided, newest first. Anything here is
settled until the owner says otherwise; open questions live in
[OPEN_QUESTIONS.md](OPEN_QUESTIONS.md).

## 2026-10-28: Bodies are living tissue

The owner chose the most detailed of three options for layer 9: bodies are
built of real tissue with its real physical properties, and food powers a
metabolism. Injuries come from a tissue failing under heat, cold or force,
never from a damage number. The owner defers the physics to Claude, because
the deep simulated world matters most.

- A body is concentric layers of skin, fat, muscle, bone and organs in each
  part (head, torso, arms, thighs, legs), with the tissue constants of the
  Fiala and Gagge thermal-physiology models. Heat conducts between layers,
  and blood carries it between every layer and the core (Pennes).
- Regulation follows Gagge's 1971 model. Skin blood flow opens when the body
  is warm and closes when the skin is cold. Shivering answers a cold core
  and cold skin together, up to 350 W. Sweat answers the mean body
  temperature, and evaporates only as fast as the vapour-pressure drop to the
  air allows (Lewis relation; clothing passes vapour a third as well as
  heat).
- Metabolism burns glycogen and fat with their real chemistry, using the
  oxygen the blood holds. Breathing takes the tile's gas as it is. The blood
  binds oxygen by the haemoglobin curve at the alveolar pressure, and in air
  poorer than the blood gives it back within seconds. Exhaled breath is warm,
  saturated and carries the carbon dioxide. Bread is 60% starch and 40% water
  and digests into glycogen; whatever the glycogen can't hold becomes fat.
- Burns use the Henriques damage integral, freezing uses latent heat
  (frostbite is the ice that formed), and tissue over 100 °C boils dry. Falls
  and falling rubble put their kinetic energy into the parts they hit. Past
  250 J/kg a part is broken, and broken parts bleed until they clot.
- The heart stops when the core is below 24 °C or above 44 °C, when half the
  blood is gone, when oxygen runs out, when the chest is crushed or the
  organs cooked, or when there is no fuel or water. The body dies when its
  brain does: five minutes without oxygen, or the brain itself destroyed.
- Everything is counted. A body's energy is m·(c·T + o) per layer, with o set
  so tissue at 37 °C holds what water there does, plus lumps for its stores.
  The world plus every body conserves energy and mass exactly, apart from
  what falls and climbs trade with gravity.
- Spilled blood is thermally water and freezes into frozen blood. It doesn't
  boil yet, because a vapour that condenses back into blood is needed first.
- Not yet: bodies don't take up volume in their tiles, hold no ether, and
  can't cast. A flash heats a part's whole skin layer rather than the face it
  struck. Clothing doesn't burn. Smoke doesn't harm the lungs. Carbon dioxide
  in the air doesn't change breathing.

## 2026-10-28: Conjured matter and etherium

From the owner: all magic is built from the energy of the etheric plane.
Conjured matter is crystallised magical energy, indistinguishable from
regular matter. Actual crystallised etherium exists as well: it adds magic
to an environment it is placed in, and a mage can consume it to add power
to spells.

How the simulation does it:

- Conjuring crystallises ether into matter at 10 MJ per kilogram, plus the
  heat the new matter holds at the temperature of where it appears. Once made
  it is ordinary matter, the same material id as any other.
- Mass and energy stay balanced through one count: the net matter made from
  ether (`Simulation.EtherMatterKg`). The world's mass less that never
  changes, and its energy plus 10 MJ for every kilogram of it never changes
  apart from what crosses to the reservoir.
- Etherium is a material tied to the etheric plane: the tile it lies in
  settles to a potential of 30 MJ/m³, refilled faster the more etherium there
  is, so it enriches the field around it without wearing away.
- When the field within a caster's reach can't supply a spell, the caster
  burns etherium within reach, nearest first, for 10 MJ per kilogram plus
  the heat it held.

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
- **Structure (layer 6):** every pair of touching solid tiles with stiffness
  is bonded by a 3D elastic beam (stretch, bending both ways, shear, twist)
  half a metre long, with the cross-section of the thinner tile (a tile 5%
  full is a post about 11 cm square). The bottom layer of the world is
  bedrock. Once a second, each bonded group in an awake region is solved for
  static equilibrium by preconditioned conjugate gradients, from rest, so the
  answer depends only on the world's state. Loads are the tiles' own weight,
  the rubble and liquid stacked on them, and the pressure of ice packed past
  full with no room to grow (bulk modulus times overfill, capped where that
  pressure would melt the ice). A bond fails in tension, crushing, bending
  or shear against the weaker tile's strength; strength falls linearly with
  the temperature beneath the tile's surface layers (wood from 100 to 300 °C,
  iron from 400 to 1200 °C). The worst bond breaks, the group is re-solved,
  and up to 64 bonds break per group per second. Pieces cut off from bedrock
  become loose rubble that rests on what is below and falls at gravity's
  rate up to one tile per tick; the potential energy lost becomes heat in the
  falling tile. Broken bonds, rubble and fall speeds are saved and hashed.
  Not yet: stiffness lost to heat, impact forces, sideways pressure of
  liquids, potential energy of liquids and gases.
- **Two temperatures per tile:** a tile holding both a solid or liquid and
  more than 0.1 mg of gas keeps a temperature for each. Every face is shared
  between the two by how much of it each covers (a solid its fill on every
  face; a liquid the whole floor, the sides up to its depth and the ceiling
  only when full), giving four paths: solid–solid, solid–gas, gas–solid,
  gas–gas. Inside the tile they trade heat by
  convection (10 W/(m²·K)) and radiation over the surface between them (a
  cube of the solid's volume; a liquid's floor), relaxed exactly so it never
  overshoots. Gas pressure, buoyancy and everything gas carries use the gas
  temperature; melting and boiling use the solid's or liquid's, condensing
  the gas's. Heat put straight into a tile (a spell) goes into its solid or
  liquid. A fuel tile at least half empty (a post, a heap) burns into its
  own gas over its whole surface, catching when that surface reaches its ignition point, and a
  flame in that gas pours 25 kW/m² onto it. So a thin post in a fire now
  catches in two or three minutes and fails after about ten, where before
  hot gas heated it through at once. A fuller tile (a wall, a log) still
  burns on its faces into the gas beside them.
- **Ether field (layer 7):** ether is energy in space, in joules per tile,
  counted in the energy audit. A tile's capacity is its open volume plus its
  solid or liquid's volume times that material's ether permittivity (crystal
  40, water 10, stone 4, iron 0.5, lead 0.2); potential is charge over
  capacity, in J/m³. Ether flows across faces like heat, at each side's ether
  conductivity in m²/s (crystal 2, magma 1, stone 0.4, iron 0.0005, lead
  0.0001; open space 0.05), limited so it never overshoots. Every tile has an
  ambient potential (ordinary places 1 MJ/m³) and a refill rate (1/600 per
  second, 1 per second at a ley node); the reservoir pulls each tile toward
  its ambient potential, relaxed exactly each tick, and everything that
  crosses is counted. The ether stays where it is when matter moves. A solid
  past its breakdown potential (crystal 10 MJ/m³, ice 100, wood 200, stone
  and soil 500; metals, liquids and gases never) ruptures: the share of the
  charge it holds becomes heat in it and all its bonds break. A chunk sleeps
  when every tile is within 1 J/m³ of where its faces and the reservoir pull
  it. The aether crystal is quartz physically and melts at 1986 K into molten
  crystal, which never ruptures. The earlier placeholder arcane constants were
  replaced. Not yet: ether carried with moving matter, arcs through air.
- **Spells (layer 8):** spells are `WorldCommand`s cast from a point (bodies
  come with layer 9). A cast gathers ether from every tile within 3 m in
  proportion to what each holds, taking at most half of it. Heat adds the
  gathered joules to the target tile, its solid or liquid first. Freeze takes
  up to the asked joules (never more than half the tile's heat above 0 K per
  cast) and releases them into the ether around the caster, spread by
  capacity. Charge adds the gathered ether to the target tile's field. A
  fireball is a projectile (`Simulation.Projectiles`, saved and hashed) at
  30 m/s, checked every quarter tile; it stops at the first tile at least 5%
  full of solid or liquid, putting half its energy into that tile (for a
  solid fuel, all of it at first into the surface layer of the face struck)
  and half into the gas of the tile it came from. It gutters into the gas
  where it is after 10 s or at the world's edge (into the ether in a
  vacuum). The energy audit counts bolts in flight. Conjuring puts the
  matter in the target's condensed slot (only if empty or the same
  material, never past full) or its gas. Not yet: force, rituals reaching
  further.
- **Bodies (layer 9):** `Simulation.Bodies` holds each body's full state,
  which is saved and hashed. `BodySystem` runs after spells and before heat,
  one body at a time. A body stands in a column of tiles (a human is four)
  and wakes the chunks around it each tick. Commands spawn a body, step it
  (up a one-tile step if needed), eat, drink and sleep. A body falls when
  nothing solid is under it and floats once its thighs are under water.
  Urine and blood are poured into the tile at its feet.
- **Below absolute zero:** a bead of liquid too small to hold its temperature
  through a tick can be cooled past freezing by the gas around it by more
  than it holds above 0 K as a solid. Rather than flooring it (which made
  energy), the bead and its gas are brought to one temperature.
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
