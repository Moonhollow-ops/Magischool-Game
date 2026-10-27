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
| 4b | Air currents | Warm air and smoke pool under a ceiling and spill out of a doorway; a draught through a window | Next |
| 5 | Combustion and oxygen | Fire spreads through a wooden hut and dies when the air runs out | |
| 6 | Structure and load | A burning support beam fails and the ceiling collapses | |
| 7 | Ether field | Arcane charge flows through stone and ruptures an overloaded crystal | |
| 8 | Spells as field inputs | Spells only add heat, force, charge or matter; the frozen pipe bursts with no special code | |
| 9 | Bodies | A creature with body temperature, wounds and needs reacts to all of the above | |
| 10 | Perception | NPC awareness from sight, light, sound and skill; someone notices a spell or its traces | |
| 11 | Ecology | Plants grow, die and rot; simple creatures feed and wander | |
| 12 | World time | Day and night, weather, and the city changing while the player is away | |

Perception (10) was added because magic must stay secret: being seen, heard,
or leaving evidence is a core consequence of using magic.

Layer 4's first pass moves liquids and gases with conserved mass and energy:
water falls, pools, floods through doorways and finds its level; gas follows
pressure and its own weight, warm and light gas rises by mixing upward, and an
open sky lets heat and steam leave the world. What it can't do yet is carry
momentum. Gas has no velocity, so there are no currents: warm air mixes upward
instead of pooling as a layer under the ceiling, and steam fills a room evenly
instead of from the top down. Layer 4b adds a velocity field for air with a
pressure solve each tick (the standard incompressible-flow method), which gives
plumes, layers and draughts. It comes before combustion because smoke filling
a room from the ceiling down is how fires kill.
