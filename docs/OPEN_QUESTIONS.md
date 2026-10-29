# Open questions

Each question has a suggested default. Work proceeds on the default until the
owner answers; answered questions move to [DECISIONS.md](DECISIONS.md).

## Needed for layer 2 (heat) and beyond

1. ~~How big is one tile?~~ Answered: 0.5 m (see DECISIONS.md).
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
7b. **Can casting itself be seen or heard?** Default: no. Gathering ether is silent and dark;
   only what a spell does to the world (a bolt's glow, a strike's bang, ice, fire) gives it away.
   Words, gestures or a caster's glow would make casting itself a tell.
8. **How do the hunting wizards arrive?** Default: a hidden suspicion score
   per witness and per place, rising into rumours, then an investigation, then
   the team.

## Needed for bodies and combat (layer 9)

9. ~~How detailed are bodies?~~ Decided: living tissue physiology (see
   DECISIONS.md, 2026-10-28).

## Needed for the city

10. **How big is the slum city?** Default: about 6 x 6 blocks, roughly
    400 x 400 tiles and 12 z-levels at 0.5 m tiles (basement to rooftops).

## Raised by the school day

11. ~~What happens to the world during long waits?~~ Built: quiet chunks
    sleep (see DECISIONS.md). A mostly quiet 400 x 400 x 12 city runs at about
    1 ms per tick; a fully busy one at about 15 ms on 4 cores.
12. **Are test questions grounded in the real sim?** Partly answered: tests are
    tied to classes and professors teach the mechanics. Still open: should the
    questions be generated from the actual material and spell data, so answers
    are always true in this world? Default: yes.
13. **Does the player physically attend class?** Default: yes. The class is a
    place in the world, the PC sits there while time passes, and anything that
    happens elsewhere during those two hours still happens.

## Raised by ecology (layer 11)

14. **Which plants and creatures come next?** Default: the city's own: weeds, moss and trees in
    the yards, rats, pigeons and stray cats, each a body plan or plant material on the same rules.
15. **Seasons and seeds.** Default: they come with world time (layer 12), when days, nights and
    weather drive the light and temperature that plants already respond to.

## Raised by play mode

16. **Should walking only route through places the student has seen?** Default: yes, later. For
    now the path search knows the true world, so it can find a way through unseen rooms.
17. **What does the student see on other levels?** Default: the shown level only, as now; a
    view that shows the highest thing in sight in each column comes with taller places to climb.
18. **Should the sense spell cost more, or reveal less, at range?** Default: one kilojoule
    anywhere in sight, full reading. Range and skill can shape it when classes teach it.
