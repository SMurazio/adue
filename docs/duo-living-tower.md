# The Tower, After the Fork — duo doctrine

**Status: DECIDED for the fork (user + orchestrator, 2026-08-09), informed by a Fable design
consult.** Resolves what happens to the MMO's "Living Tower" direction (`docs/game-direction.md`)
now that Adue is a standalone two-player co-op roguelite. `game-direction.md` remains the **MMO's**
decided direction, untouched; this doc governs **Adue**.

**One line:** *Keep the tower as the shape of the run and its content grammar; ship the P2 demo as a
menu, not a place; retire the phrase "living tower." Let the stranger duos decide whether it ever
gets walls.*

---

## The decision: "A in doctrine, B in the build"

- **In doctrine (free, on paper now):** the run **is** a climb. Floors = bounded, escalating duo
  encounters; the boss floor is the top. The P1 chassis is already floor-shaped, so this costs a
  label, not a rearchitecture.
- **In the build (P2):** the between-runs space is **UI, not a walkable hub.** Title → pair → run →
  end screen → again. See the P2 directive below.
- **Retired:** the word **"living"** and any *reactive/adaptive* tower. That's the MMO's ecology
  ghost in new clothes and a **Law 7 (legibility) trap** — if the tower silently adapts to you,
  players can't tell an authored contest from RNG, which is exactly where UO's ecology died.

## What transfers vs what parks (the ~60%-survives split, made specific)

| Tower logic (MMO, `game-direction.md`) | In Adue |
|---|---|
| Floors = discrete bounded maps = risk tiers (pillar 3) | **Transfers** — the run's spine. Generic roguelite structure, but competent and already built. |
| "Climb, push past wisdom, extract or lose the haul" (pillar 6) | **Transfers as the one duo-native META** — see below. Needs stakes; **post-P2.** |
| Charming-with-teeth tone; safe base, hostile heights | **Transfers** — tonal target unchanged. |
| The tower is ALIVE — regrowing/withering/fighting-back ecology a crowd hunts (pillars 2/5) | **Parks** — needs a community; and reactivity is a legibility trap. Not Adue. |
| One world = one ~200-person community "known by name" (pillar 4) | **Parks** — incompatible with a 2-player session game. |
| Loss-driven **player-market** economy (pillar 1) | **Parks** — needs a crowd trading. Collapses to the within-run + meta component-modifier loot the duo plan already specifies (P3). |
| Ecology-as-population-balancer; seasonal world lifecycle | **Parks** — pure MMO. |

## The one thing the tower adds that a plain lobby-roguelite cannot

Floors-as-risk-tiers is furniture — Hades and Slay the Spire have it too; it is not differentiation.
The differentiator is that **the push-or-extract decision is itself a relationship input.** A solo
roguelite has a risk *dial*; a duo game has a *negotiation* — two people who must **agree** to gamble
the haul (one bold, one wise, the argument at the top of floor 6). That passes the framework's core
test — *no ability resolves from one player's state alone*, including the ability to go home. **No
lobby-roguelite gets this for free.** But it requires stakes (a haul / economy), so it is **strictly
post-P2.** Keep the skeleton now precisely to hold the seam for a mechanic we can't build yet.

## The defensible thesis: authored, not reactive

Not "a tower that reads and reacts to the pair" (rejected above). The real, already-written thesis
is in the **Inversions** section of `docs/duo-mechanics-framework.md`, generalized from bosses to
floors: **every floor is an authored *argument* with your relationship** — a designed contest of
specific 12-Law channels. LoS-cutting geometry contests S8; forced-spread rooms contest S1; stagger
mechanics contest T1; a boss = 2–3 contested channels. This is a genuine content generator that needs
**zero simulation** and stays legible. Authored contests, hand-placed. Reactivity is a P3+
evidence-gated experiment, if ever.

## P2 build directive (what the demo actually presents)

P2's only job: *do stranger duos attempt the Perfect version unprompted, and do they laugh?* Nothing
about the tower may touch the demo beyond a label.

- **A menu, not a base camp.** No walkable hub. A hub is art + camera + navigation-tutorializing +
  ~90 extra seconds between two strangers and the combat being tested — and it is the #1 scope-leak
  vector in the phase whose art is committed-low-fi placeholder.
- **The entire tower commitment in P2** = numbered floors (`Floor 1… Floor 2… SUNDERER`) + an
  end-screen strip showing how high the pair got. An evening of work, fully reversible — if the tower
  dies later, delete a label.
- **No extract/haul language anywhere.** There is nothing to extract yet; the words prime testers to
  hunt for persistence/loot/a bank, and that missing-feature noise contaminates the only signal P2
  exists to measure.
- **The pitch surface says "you two," not "the tower."** Lead the demo with the fusion moment
  (Law 10), not architecture.

## OPEN QUESTION — the between-runs space (user, 2026-08-09)

The user finds a bare menu **"slightly impersonal"** for a *relationship* game — a correct instinct,
recorded here rather than closed. The menu is a **P2 scope expedient, not a committed final form.**
Fable deferred the hub for the demo's art budget, *not* because a shared space is wrong. The leading
post-P2 candidate is a small **base camp / commit-together space** — a place two players physically
stand and agree to begin — precisely *because* it is more personal and reinforces the couch/duo
fantasy (and it plays great once couch mode exists, one clock). **Gated on the P2 result**: if the
kill-test passes, revisit the hub as the first between-runs investment; the menu is the floor, not the
ceiling.

## How this was decided

Fable consult (2026-08-09) over `game-direction.md`, `duo-standalone-plan.md`,
`duo-mechanics-framework.md`. Verdict: "A in doctrine, B in the build." The tower question is real but
**post-P2** — the kill-test's outcome does not depend on it, and nothing in either option should touch
the demo beyond a floor counter.
