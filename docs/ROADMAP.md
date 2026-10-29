# Simulation roadmap

Each layer is proven in its own bare test scene before the next goes on.
A layer is done when its test scene behaves as described **and** headless tests
cover it, including a determinism check.

| # | Layer | Test scene that proves it | Status |
| --- | --- | --- | --- |
| 1 | Grid, materials, seed, ticks, save and replay | Same seed and inputs give identical state after 10,000 ticks | Done |
| 2 | Heat conduction | A hot stone warms the air beside it within seconds; iron spreads heat faster than wood | Done |
| 3 | Phase change with latent heat | An ice block melts into water, then boils into steam | Done |
| 4 | Fluids and gases | A room floods through a door; steam rises and fills the ceiling | Done (first pass, see below) |
| 4b | Air currents | Warm air and smoke pool under a ceiling and spill out of a doorway; a draught through a window | Done |
| 5 | Combustion and oxygen | Fire spreads through a wooden hut and dies when the air runs out | Done |
| 6 | Structure and load | A burning support beam fails and the ceiling collapses | Done |
| 7 | Ether field | Arcane charge flows through stone and ruptures an overloaded crystal | Done |
| 8 | Spells as field inputs | Spells only add heat, force, charge or matter; the frozen pipe bursts with no special code | Done (heat, charge and matter; force later) |
| 9 | Bodies | A creature with body temperature, wounds and needs reacts to all of the above | Done (first pass, see below) |
| 10 | Perception | NPC awareness from sight, light, sound and skill; someone notices a spell or its traces | Done (light and sound; skill later) |
| 11 | Ecology | Plants grow, die and rot; simple creatures feed and wander | Done (grass and rabbits; more kinds later) |
| 12 | World time | Day and night, weather, and the city changing while the player is away | Next |

Perception (10) was added because magic must stay secret: being seen, heard,
or leaving evidence is a core consequence of using magic.

Layer 4 moves liquids and gases with conserved mass and energy: water falls,
pools, floods through doorways and finds its level; gas follows pressure and
its own weight; an open sky lets heat and steam leave the world. Layer 4b
gives air a velocity on every tile face, driven by buoyancy, slowed by drag,
and balanced each tick by a pressure solve, so a stove sends up a plume, warm
air collects under the ceiling and spreads down, and warm air spills out of
the top of a doorway while cool air comes in along the floor. Velocity isn't
yet carried along by the flow itself (no momentum advection), so currents
stop soon after whatever drives them stops; wind and coasting draughts come
with weather.

Layer 5 adds oxygen and fire. Air is 23% oxygen by mass; wood burns where its
surface is past its ignition point, taking oxygen from the air beside it and
giving back carbon dioxide, steam and smoke, and its heat of combustion is
counted in the energy audit so burning conserves energy exactly. Each fuel
face keeps a thin surface temperature, warmed by the hot gas and by any flame
in that gas, so a fire keeps its own face lit and spreads to the wood around
it over minutes. In the test hut a smouldering log sets the corner walls
alight, and the fire goes out once it has drawn the oxygen down to about 15%.
Charring, smouldering, carbon monoxide and wet wood come later.

Layer 6 holds the world up. Touching solids are bonded, and each bond is a
short elastic beam, so a structure is solved for its stresses under its own
weight and whatever rests on it, down to the bedrock at the bottom of the
world. A bond breaks when its stress passes the weaker tile's strength,
which falls with heat and with a cross-section thinned by burning; the most
overloaded bond goes first and the load shifts onto the rest. Anything no
longer held to bedrock is rubble and falls, and its lost potential energy
becomes heat. A stone cantilever is stressed within 8% of beam theory and
snaps at the length theory predicts (8 m); posts burning under a stone
roof give way and the roof comes down; ice frozen with no room to grow
bursts a thin iron box but not a thick one, and pushes less near 0 °C
because pressure melts ice. Since then a tile holding both a solid or
liquid and gas keeps a temperature for each, so a thin post in a fire heats
through its surface the way a real one does (see DECISIONS.md).

Layer 7 is the ether: energy that pervades space, which casters will draw on
in layer 8. Every tile holds some, counted in the energy audit, and every
place has an ambient potential it settles to, refilled from a deep reservoir
under the world over about ten minutes (within a second at a ley node). Ether
flows from high potential to low through stone, earth and water and all but
stops at cold iron and lead. An aether crystal holds forty times what open
space does, but past its breakdown potential it ruptures: the charge it holds
becomes heat in it and it shatters, and if the field keeps it overloaded it
heats until it melts. In the test scene a crystal 3 m along a stone kerb from
a ley node ruptures within two minutes; behind a wall of cold iron it stays
at the ordinary potential. A great ritual that drains a place dry sees the
hollow close from its surroundings within seconds and the reservoir make up
the loss over half an hour. The test room now has an ordinary field, and a
ley node and crystal outside the room's top wall (M shows the ether).

Layer 8 casts spells into that physics. A caster gathers ether from
everything within 3 m, at most half of it per cast, so in a poor place a
spell comes out weaker; an ordinary place holds about a hundred megajoules
within reach, so a 1 MJ spell takes about one percent and the field flows
back within seconds. Heat puts the joules into a tile, its solid or liquid
first. Freeze pulls heat out of a tile and hands it to the ether around the
caster. Charge pours ether into another place. A fireball is a bolt that
flies at 30 m/s and, where it strikes, puts half its energy into the thin
surface layer of the face it hit and half into the air in front: 2 MJ sets
a wooden wall alight and 200 kJ only warms it. Freezing the water in a
sealed lead pipe makes ice that splits the pipe along its length, with no
code that knows about pipes; charge poured into a crystal from 4 m away
ruptures it. Bolts in flight are saved, hashed and replayed. Conjuring
crystallises ether into ordinary matter at 10 MJ a kilogram, so a cast in
an ordinary place makes about a bucket of water, which then falls and
spreads like any other. Etherium, crystallised ether left as itself,
enriches the field around it without wearing away, and a caster burns it
for whatever the field can't give. Force comes later: it needs the kinetic
energy of air and rubble counted first. In the viewer, C places the caster;
F, X, V and G cast at the hovered tile, and B conjures the brush material
there.

Layer 9 gives the world bodies of living tissue. A student is skin, fat,
muscle, bone and organs in each part, each layer with its own temperature,
and blood carrying heat between them. They stand in a column of four tiles,
breathe the gas of the tile their head is in, and trade heat with whatever
is around them. Clothed in a 22 °C room their core holds at 37 °C. In 5 °C
water they shiver as hard as a body can and still lose about a degree an
hour. In 45 °C dry air they sweat about 0.3 L an hour and their core
settles just above 37 °C. With their head under water they faint after about
two minutes and die after eight; in pure nitrogen they faint within fifteen
seconds. A 300 kJ fireball burns through the skin it strikes but leaves the
organs untouched. A 1 m fall bruises and a 10 m fall breaks the legs. A
stone block falling 4 m onto a head kills. They burn glycogen and fat with
oxygen into carbon dioxide and water, eat bread and drink water, make urine,
bleed onto the floor, and need sleep. Every joule and gram is counted with
the world's. In the viewer, P places a student, the arrow keys walk them, T
eats or drinks from the hovered tile, and Z puts them to sleep.

Bodies are then made of parts instead of one skin over a few blocks. A
student is a tree of 27 parts: limbs down to hands and feet, a head with two
eyes and teeth, and organs inside (heart, two lungs, liver, gut, two kidneys,
brain), each with its own tissue and its own job. A part works as well as its
tissue is whole, so injuries are lost functions: with one lung a student
breathes harder and is fine, with none they faint in about two minutes; a
broken neck stops the breath and the legs; failed kidneys build up poisons
over days until the heart stops; without teeth they eat a fifth as fast; with
both eyes gone they are blind. A blow is shared by mass with the organs under
it, so crushing a skull kills through the brain and crushing a chest through
the heart. A part torn off (25 times what breaks it) becomes remains, a body
of its own with its tissue and energy, which cools on the floor while the
stump bleeds; a head cut off takes the life with it. Parts are data in each
body, so a mutation can change a layer's tissue, and CastGraft grows a new
part from ether at 10 MJ a kilogram: a third eye sees when the other two are
gone. In the viewer, Y grows an eye on the student from the caster.

Layer 10 gives bodies senses. Light comes from flames, glowing matter, bolts in flight and the
sky, all from real physics: hot matter glows by Planck's law seen through the eye's luminosity
curve, a flame shines with the soot its burning makes, and light falls off with distance, stops
at walls and fades in smoke and water. A burning log face lights a face 2 m away to about 9 lux;
iron at 800 K is a faint glow only in the dark; under a roof with one doorway the light dies
away from the door. Bodies see with their eyes the way they face, by the contrast what they look
at makes against what their eyes are adapted to, so two students see each other across a field
by day but not in a dark room until a fire is lit, and a student who walks out of daylight into
a dark room is blind for minutes. Sound comes from fire, footsteps, falling rubble, cracking
beams, shattering crystals and spells, and reaches an ear straight through walls by the mass
law, round corners by diffraction and by echo in closed rooms: a footstep 3.5 m off is about
45 dB(A) and a stone wall silences it, a bang behind the end of a wall is 30 dB quieter than in
the open, and in a stone room it rings 20 dB louder. A fire masks footsteps, a fireball strike
15 m away wakes a sleeper and a footstep does not, and a student with one ear hears less. A
witness who sees a fireball fly remembers it, with where and when. In the viewer, L shows the
light, N turns day to moonlight to starlight, O turns the student, sounds ring where they were
made, and the line under a student lists what they last saw and heard.

Layer 11 brings the ground to life, every ten seconds for the time since, over the whole world.
Soil holds water in its pores, drains to field capacity in a day or two by Darcy's law through
its own suction, soaks up puddles lying on it, and dries as fast as the light on it can pay the
latent heat. Grass in a tile over soil catches the light on its leaves, fixes carbon dioxide from
its air into carbohydrate with water from its roots and gives the oxygen back, up to about 30
µmol per square metre a second in full sun; it respires a quarter of what it fixes and 1.5% of
itself a day, so it shrinks in the dark, draws down the carbon dioxide of a closed room, and
seeds the ground beside it about once a week. Two weeks of drought, a night of hard frost or an
hour of heat kill it into dead grass, which rots by its own burning chemistry when damp and
warm, a few percent a week; dead bodies rot away to bone. Light carries no energy in the world,
so what plants store and leaves breathe out is counted as coming in from light, and with that
the world's energy and mass stay exact. The open air now has carbon dioxide and humidity, and
the sky beyond it the same. Rabbits (1.9 kg of living tissue, eyes that see all round but
straight behind) graze when hungry, drink when thirsty, sleep when tired, amble about, and bolt
from people, fire, bolts and loud noises, all through the same commands a student uses, so a
rabbit's run is paid for from its own fuel. The test room now has damp soil, grass on most of
it and three rabbits; in the viewer K plants grass and J lets a rabbit loose.
