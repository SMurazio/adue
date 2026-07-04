# Duo Mechanics Framework
*A generative grammar for pair-function co-op mechanics. Any mechanic = INPUT × TRANSFORM × OUTPUT, filtered through the Laws, documented as a Card.*

> **ADOPTED 2026-07-04** as the project's mechanical framework for the duo-mechanics exploration
> (user decision — the game's final shape is intentionally unresolved; this grammar governs the
> exploration). The ten Laws are the standing review checklist for every duo mechanic — design
> AND implementation review (empirical basis: all three bugs from the first live co-op session,
> 2026-07-04, were Law violations — Laws 2, 4, 7). See the **Adoption Addendum** at the bottom
> for the project-specific amendments (Latency law, Pillar scoping, Kit-budget law).

---

## The Pillar

> **No ability resolves from one player's state alone.**
> Every mechanic must read something about the *relationship* — where we are, when we act, what we share.

If a proposed mechanic works identically when the partner is AFK, it's not a duo mechanic. Cut it or rewire it.

---

## Axis 1 — INPUT CHANNELS (what pair-state does the mechanic read?)

### Spatial (the pair as geometry)
| Channel | What it reads | Seed example |
|---|---|---|
| S1. Distance | How far apart we are | Laser lace: powerful at 8m, hurts past 12m |
| S2. Segment | The line between us as a physical object | Lace damages enemies crossing it |
| S3. Midpoint | The point halfway between us | Detonation that lands at our midpoint — aimed by both repositioning |
| S4. Axis angle | Orientation of our pair-line | Broadside cone fires perpendicular to the pair axis |
| S5. Relative facing | Facing each other / same way / back-to-back | Back-to-back grants 360° parry |
| S6. Enclosed area | The ellipse/zone with us as foci | Enemies inside our ellipse are slowed |
| S7. Relative motion | Approaching, separating, orbiting, mirroring | Orbiting partner sweeps the lace like a blade |
| S8. Occlusion | Line of sight between partners | Lace blocked by walls; some buffs need clear LoS |

### Temporal (the pair as rhythm)
| Channel | What it reads | Seed example |
|---|---|---|
| T1. Simultaneity | Same input within a window | Unison press → upgraded shield |
| T2. Sequence | A acts, B confirms within a window | A telegraphs, B detonates it (call-and-response) |
| T3. Alternation | Strict turn-taking rhythm | Alternating hits build a combo meter; doubling up drops it |
| T4. Phase offset | Fixed delay copy | Echo: B re-casts A's spell 1s later on the same path |
| T5. Overlap | Both channeling at once | Two channeled beams held simultaneously fuse |

### Ballistic (the pair through objects in flight)
| Channel | What it reads | Seed example |
|---|---|---|
| B1. Intersection | Two trajectories crossing | Skillshots fuse at the crossing point → resultant blast (bisector) |
| B2. Relay | Catching and re-sending partner's projectile | Volleyball: each relay adds a stack |
| B3. Partner-as-target | Aiming AT your partner on purpose | Shoot partner's shield to charge it; hit their blade to enchant it |
| B4. Interception-defense | Partner's projectile blocks a threat | B's shot can destroy the projectile flying at A |

### Resource (the pair as economy)
| Channel | What it reads | Seed example |
|---|---|---|
| R1. Shared pool | One health/mana/gold bar for both | Shared HP: the unison shield protects *us* |
| R2. Charge transfer | One banks, the other spends | One player is the magazine, the other the trigger |
| R3. State swap | Exchanging positions/buffs/HP | Swap places instantly — repositions the lace in one input |
| R4. Complementary states | Opposite charges that react | One is fire-charged, one frost-charged; contact = burst |

### Informational (the pair as perception)
| Channel | What it reads | Seed example |
|---|---|---|
| I1. Asymmetric info | One sees what the other can't | Only A sees weak points; B has the sniper |
| I2. Telegraph-as-instruction | My wind-up tells you where to be | A's cast circle shows B the required standing spot |

## Axis 2 — TRANSFORMS (how inputs map to effect)

| Transform | Logic | Feel |
|---|---|---|
| X1. Gate | Effect only fires if condition met | Binary, punchy — use with generous conditions |
| X2. Scale | Effect strength is a curve over the input | Continuous mastery; needs a visible meter |
| X3. Fuse | Two things become one bigger thing | The spectacle transform — clip material |
| X4. Redirect | Partner changes the position/direction of my effect | High agency for the receiver |
| X5. Amplify | My effect upgrades when passing through/near partner | Turns the partner's *body* into a lens |
| X6. Convert | My output becomes partner's resource | Builds economies between players |
| X7. Reflect | Partner is a bounce surface | Trick-shot geometry |
| X8. Sustain | Effect persists only while both maintain input | Tension and commitment; vulnerable channel |

## Axis 3 — OUTPUTS (what the pair produces)

Damage · Control (stun/slow/pull) · Defense (shield/parry/cleanse) · Mobility (launch/swap/pull partner) · Healing · Economy (resource gen) · Information (reveal/mark) · Terrain (walls, zones, trails)

---

## The Recipe

**Pick 1+ input channel × 1 transform × 1 output. That's a mechanic.**
~19 channels × 8 transforms × 8 outputs ≈ 1,200 raw combinations. Most are junk; the Laws below are the filter.

Worked examples (the four founding mechanics, decomposed):
- Laser lace = **S1+S2 × X2/X8 × Damage+Terrain** (distance-scaled, sustained line)
- Fusion skillshot = **B1+T1 × X3 × Damage** (intersection + release-sync, fused)
- Unison shield = **T1 × X1 × Defense** (simultaneity-gated)
- Positioning cast = **S3/S5 × X1 × any** (formation-gated ability)

Fresh rolls to show the generator working:
- **S7 × X2 × Damage**: "Cyclone" — while orbiting each other, the pair emits expanding shockwaves; orbit speed scales power.
- **B3 × X5 × Control**: "Anvil" — shoot your partner's hammer mid-swing to add a stun proc to that swing.
- **R2 × X8 × Terrain**: "Kiln" — one player channels heat into the other, who walks a burning trail while receiving.
- **I1 × X4 × Damage**: "Spotter" — A marks a point only they can see through walls; B's next shot curves to it.
- **T3 × X2 × Economy**: "Metronome" — perfectly alternating basic attacks generate shared mana; breaking rhythm dumps it.

---

## The Laws (filters — a mechanic must pass all of these)

1. **Pillar law.** Reads pair-state, or it's cut.
2. **Degradation law.** Failure degrades to a weaker solo effect; it never nullifies. Backfire (self-damage) is reserved for high-power, always-on systems (the lace) — never for attempted combos.
3. **Receiver-forgives law.** Where two actors participate, the second actor must be able to rescue the first actor's imperfect execution. Errors become saves; saves are the best co-op moments.
4. **Expected-beats-correct law.** The rule players assume is the right rule (bisector, not vector sum). If a playtester can't predict the outcome after 15 minutes, simplify the rule or amplify the feedback — in that order of suspicion: feedback first.
5. **One-knob law.** Each pair-state axis powers at most one thing at a time. Angle steers fusion; timing scales its power; distance belongs to the lace. Never double-book an axis across simultaneously active systems.
6. **Home-formation law.** Always-on systems (lace) define the pair's default geometry ("home"). Active abilities may momentarily pull the pair *away* from home — that tension is the choreography — but two always-on systems must never disagree about where home is.
7. **Legibility law.** Every ask is drawn in the world: distance rings, ghost markers, live resultant previews, audio metronomes for sync windows. The preview is part of the mechanic, not polish. Mastery = no longer needing the preview.
8. **Tiered-timing law.** Windows have Good/Perfect tiers, never pass/fail binaries. Generosity at the floor, bragging rights at the ceiling.
9. **Burden-declaration law.** Every mechanic states who carries execution: initiator-heavy, receiver-heavy, or symmetric. The kit overall must offer both asymmetries so a stronger player can voluntarily carry the harder half.
10. **Clip law.** If the Perfect version wouldn't be legible in a 10-second compressed video, redesign the presentation.

---

## The Mechanic Card (template — fill one per mechanic)

```
NAME:
INPUT CHANNEL(S):        (S/T/B/R/I codes)
TRANSFORM:               (X code)
OUTPUT:
RULE (one sentence):     "When ___, then ___."
FAILURE BEHAVIOR:        (what the degraded solo version does)
BURDEN:                  initiator / receiver / symmetric
LEGIBILITY:              (what is drawn in the world, for whom)
TIMING TIERS:            (Good window / Perfect window, if any)
COUNTERPLAY HOOK:        (how a boss/enemy checks or attacks this mechanic)
LOOT HOOKS:              (3+ couple-item ideas that modify it)
CLIP TEST:               (describe the 10-second highlight)
```

---

## Inversions (the framework generates more than abilities)

**Boss/encounter generator.** Every input channel is also an attack on the pair. Design enemies that *contest* a channel:
- S1: forced-spread or forced-stack zones · S2: enemies that sever/deflect the lace · S8: LoS-cutting walls
- T1: staggered mechanics that forbid sync · T3: attacks that break rhythm
- B1: bodies that block the intersection point · R1: damage that drains the shared pool asymmetrically
A boss = 2–3 contested channels + one channel it's vulnerable to. The duet writes itself.

**Loot generator (couple-itemization).** Items modify a component, never a stat:
- Widen/shift a window (T) · lengthen/reshape geometry (S) · change an output type (fusion now heals)
- Add a rider ("lace leaves burning trail when swept") · chain two mechanics ("fusion blast spawns a 2s mini-lace")
Identity is personal (character, base verbs); power is shared (all drops modify the pair).

**Skill-ceiling audit.** For each shipped mechanic, ask: what does the top-1% duo do with this that a new duo can't? If the answer is "nothing, they just press it more reliably," the mechanic needs a Scale (X2) dimension or a steering input.

---

## Session checklist (using this doc)

1. Roll or pick: channel × transform × output.
2. Write the one-sentence rule.
3. Run the 10 Laws. Fix or kill.
4. Fill the Card (counterplay + loot hooks are mandatory — a mechanic without them is a demo, not a system).
5. Grey-box test: two players, one room, 15 minutes. Pass = they attempt the Perfect version *unprompted* and laugh when it lands.

---

## Adoption Addendum (project amendments, 2026-07-04)

Amendments from the orchestrator's review at adoption, grounded in shipping the four founding
mechanics on `exp/duo-abilities` and the first live two-player session.

**Law 11 — Latency law.** The framework's channels assume both players share one clock; they
don't. Timing windows (T1/T3/T5) are judged on client-authored timestamps with server
validation, or widened to absorb worst-case jitter between BOTH players' routes to the server —
never judged purely on server receipt order, which turns "Perfect" into a ping contest. Any
channel read from replicated state (S7 relative motion, B1 intersection) must state its
staleness budget, and its preview must be honest about server authority (the preview shows what
the server will rule, not what the local client hopes). *Status: the shipped Unison Shield
judges by server receipt tick — a known Law-11 debt to revisit if the mechanic graduates.*

**Pillar scoping.** The Pillar governs **duo abilities**, not base verbs. Movement, dodge,
jump, charge, basic attack, and harvest remain solo — they are the vocabulary the duo grammar
composes over. A base verb may gain a pair-read *rider* (Law 5 permitting), but must stay fully
functional solo.

**Law 12 — Kit-budget law.** The 19 channels are a design space, not a kit. A duo tracks at
most ONE always-on system (the home-formation holder, per Law 6) plus 2–3 active mechanics
before the choreography turns to noise. Adding a mechanic past the budget requires retiring or
merging one.

**Card debt.** The four shipped mechanics (fusion skillshot, unison shield, laser tether,
midpoint detonation) predate adoption and have no Cards — specifically no counterplay hooks or
loot hooks. Fill their Cards before designing the next mechanic; the Card debt is the cheapest
source of the next design decisions (per the Inversions section, their counterplay hooks are
the first boss).
