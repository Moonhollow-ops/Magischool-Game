# Open questions

Each question has a suggested default. Work proceeds on the default until the
owner answers; answered questions move to [DECISIONS.md](DECISIONS.md).

## Needed for layer 2 (heat) and beyond

1. **How big is one tile?** 1 m cube (default; a door or a pipe is one tile)
   or 0.5 m (finer, a person is about 4 tiles tall, 8x more tiles per room)?
2. **Does the world freeze completely between actions?** Default: yes, nothing
   happens until you act, as in Dwarf Fortress Adventure Mode.
3. **How strict is the physics?** Default: strict conservation. Energy and mass
   are never created or lost except by magic, which is accounted for as its own
   source.

## Needed for actions (soon after layer 2)

4. **How long do actions take?** Default: step one tile = 1 s (10 ticks),
   look = free, a short spell = 1 to 3 s, a long ritual = minutes.
5. **Can the player wait in bulk?** Default: yes, "wait until something
   changes" plus fixed waits (1 s, 1 min, 1 hour).

## Needed for perception and secrecy (layer 10)

6. **What counts as exposing magic?** Default: being seen casting, and evidence
   that can't be explained (ice in summer, a melted lock). Evidence persists
   and can be found later.
7. **Should the player see NPC awareness?** Default: no exact cones, only
   hints (an NPC turns their head, a sound icon), so you learn to read people.
8. **How do the hunting wizards arrive?** Default: a hidden suspicion score
   per witness and per place, rising into rumours, then an investigation, then
   the team.

## Needed for bodies and combat (layer 9)

9. **How detailed are bodies?** Default: Dwarf Fortress style. Body parts with
   tissue layers (skin, fat, muscle, bone), each with its own material, so
   burns, frostbite and fractures come out of the same physics as everything
   else.

## Needed for the city

10. **How big is the slum city?** Default: about 6 x 6 blocks, roughly
    200 x 200 tiles and 6 z-levels (basement to rooftops).

## Raised by the school day

11. **What happens to the world during long waits?** A two-hour class is
    72,000 ticks and a night's sleep is about 290,000. Default: only "active"
    tiles (something changing nearby) are simulated tick by tick; settled areas
    sleep and catch up in one calculated jump when touched. The result is the
    same physics, just skipping work where nothing is happening.
12. **Are test questions grounded in the real sim?** Default: yes. Questions
    are generated from the actual material and spell data ("Which melts first
    in a fire, the lead pipe or the iron gate?"), so studying the world and
    passing the test are the same skill, and the answers are always true in
    this world.
13. **Does the player physically attend class?** Default: yes. The class is a
    place in the world, the PC sits there while time passes, and anything that
    happens elsewhere during those two hours still happens.
