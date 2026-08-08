# Boss Encounter: THE SUNDERER (duo-mechanics proving ground)

*Designed 2026-07-05 against docs/duo-mechanics-framework.md (the adopted grammar). This encounter IS
the counterplay-hook half of the four founding mechanics' Card debt: each phase contests some pair
channels and is vulnerable to exactly one mechanic. Branch: exp/duo-abilities.*

> **STATUS — SHIPPED + REVIEWED (2026-07-05), NOT merged to main.** All four phases (BOSS-1..4) are
> implemented, gated, and independently reviewed on `exp/duo-abilities`; the live **duo feel-test is
> the remaining gate.** The sections below are the ORIGINAL PLAN — read the **"Shipped deltas"**
> section at the bottom for where the build diverged (build-time rulings + post-feel-test tuning).
> Anything in the plan that the deltas contradict, the deltas win.

## Fiction (one line)

A dungeon construct that feeds on bonds — its whole kit tries to pull the pair apart, and it dies
only to the pair executing together.

## Framework derivation

| Phase | Contests (attacks on the pair) | Vulnerable to (the ONE gate verb) |
|---|---|---|
| P1 Husk | B1 intersection (interposer drone blocks crossings), S2 segment | **Fusion skillshot** (shatters plating) |
| P2 Sunder | S1 distance (Repel/Bind fields), T1 sync (Echo Lash pressure) | **Tether orbit-sweep** (splinter ring) + **Unison shield** (Echo Lash check) |
| P3 Core | S3 midpoint (knockback pulses while you aim it) | **Midpoint detonation** (breaks the Core Ward) |

One-knob law across phases: each phase has ONE gate verb; fusion does not break the P3 ward, the
detonation does not shatter P1 plating — the phases keep their verbs distinct.

## The room

- **24×24 exterior, 1-tile wall ring → 22×22 playable**, authored into the 384×384 map in a far
  corner away from town/forest (implementer picks exact coords in the stamp program; flat floor, no
  nodes inside — the arena is masked out of the node catalog).
- Authored-stamp walls (the established shared-map collision path: both sides predict them for
  free). genVersion bump ⇒ re-pin ContentHash and NodeCatalog CatalogHash from the first gate run
  (the M3 F1 pattern — pins follow implementation, never the reverse).
- Tether geometry check: sweet band 8–12u fits comfortably; max diagonal ~31u so overstretch is
  reachable (the room can punish carelessness) but the sweet band is easy to hold.

## Trigger + lifecycle

- **`/boss` chat command**: stores the issuer's return position, teleports the issuer AND their duo
  partner (if paired and online — partner gets a chat line, no consent ceremony on this branch) to
  fixed arena entry tiles, 3s countdown in chat, boss spawns at center.
- `/boss` while inside = leave: teleport back to the stored return position (and if the arena
  empties, the encounter resets after 10s — boss despawns, full heal, adds cleared).
- Failure: all participants dead → normal death rules apply, encounter resets.
- Victory: boss death → chat fanfare, adds despawn, players walk out via `/boss` (no auto-eject).
- Teleport rides the normal snapshot stream; the continuous predictor treats the jump as a hard
  reconcile snap (verify: SNAP, not a cross-map lerp — this is BOSS-1's highest-risk seam).

## Boss stats (all data-driven via the monster manifest where the framework allows)

- HP **1200 duo / 700 solo** (scaled at spawn by participant count). Walk 2.2u/s chase (gnoll-style).
- Baseline melee: **Cleave** — 130° wedge telegraph, radius 2.8u, 0.8s windup (combat pillar: windup
  > latency + fair dodge window; render = hit test), ~25 damage, every ~5s in melee range.
- **Lunge** every ~8s out of melee range: line telegraph (2u wide, up to 8u), 0.9s windup, dash +
  ~20 damage. Both reuse the existing TelegraphScheduler shapes.

## P1 — HUSK (100→70%)

- **Sundered Plating**: boss takes **75% reduced damage** from everything EXCEPT during a
  vulnerability window. A **fused skillshot** (any tier) shatters plating: **6s full-damage window**
  (Good) / **9s** (Perfect) — the fusion is the DPS gate and the phase's lesson.
- **Interposer drone** (one at a time, 40 HP, 1.6u/s): drifts to the midline of the pair's segment —
  it exists to body-block fusion crossings (B1 contest). Dies to anything; the tether melts it. New
  spawn 6s after the last one dies.
- Solo degradation (Law 2): plating 40% instead of 75%, and shatters on **3 skillshot hits within
  6s** (no fusion possible solo — degrades, never nullifies).

## P2 — SUNDER (70→40%)

- **Repel / Bind fields** (alternating, every ~9s, 1.2s telegraph, resolve is a ring decal around
  each player): **Repel** — players within 6u of each other at resolve are knocked 3u apart + ~15
  damage each; **Bind** — players further than 4u apart at resolve take ~15 each. The tether's home
  geometry (8–12u) sits BETWEEN the two asks — the choreography is leaving home and returning
  (Home-formation law: the tension is the design).
- **Echo Lash** (every ~14s): the shield's echo cue plays, then two unavoidable ~18-damage pulses
  0.5s apart. An upgraded unison shield absorbs both; two solo shields eat one each; no shield = eat
  both (never lethal from full HP — pressure, not a wipe check). Judged by the existing shield
  windows (known Law-11 receipt-tick debt, accepted).
- **Splinter ring**: 6 splinters (15 HP, 1.2u/s) spawn on a radius-7 ring around the boss and creep
  outward toward players; a splinter reaching within 1u of a player pops for ~12. One tether
  orbit-sweep at sweet range clears the ring — the tether's showcase moment (S7 vulnerability).
  Re-rings every ~20s.
- Solo: 3 splinters, Echo Lash is a single pulse, Repel/Bind becomes a single move-out ring.

## P3 — CORE (40→0%)

- Boss roots at center, gains **Core Ward**: immune to ALL damage except during a **burst window**.
- **Ward break**: a midpoint detonation whose blast center lands within **2.5u** of the boss center
  (3.5u solo — receiver-forgives generosity) breaks the ward: **8s burst window**, then it reforms.
  **Duo mode additionally requires** (Fable design-grill CRITICAL-1, S-duo-grill-ward-break-separation):
  a confirmed **Good/Perfect** blast — the degraded solo self-blast no longer qualifies — AND the pair
  **>= 4u apart** at resolve, so a stacked pair (midpoint barely moved by a knockback pulse) or a lone
  V-press can't cheese the gate. Solo mode is unaffected — any resolved blast within radius still
  breaks the ward (degradation-everywhere, Law 2).
- During the phase: a **rotating sweep beam** (line telegraph, full arena radius, ~25 damage, slow
  rotation — walk with it), plus **knockback pulses** every ~10s (radial 3u shove, no damage): the
  S3 contest — the midpoint is aimed with both players' feet while the floor shoves them. Aim the
  charge THROUGH the shove, or time it between pulses.
- Fusion does NOT break the ward (verbs stay distinct); the tether still clears any straggler
  splinters that trickle in below 10% (soft enrage: cleave cadence +30%, splinter trickle).
- Knockback vs prediction: v1 accepts the reconcile snap on a 3u shove (server-authoritative
  displacement, no client prediction of it). If it feels bad live, a predicted-shove telegraph is
  the follow-up, not a blocker.

## Laws checklist (the adopted review gate)

1 Pillar: every gate verb reads pair-state ✓. 2 Degradation: solo modes for every mechanic, weaker
never nullified ✓. 3 Receiver-forgives: fusion any-tier shatters; ward-break radius generous ✓.
4 Expected-beats-correct: plating/ward states need LOUD visual states (see Legibility) ✓.
5 One-knob: one gate verb per phase ✓. 6 Home-formation: Repel/Bind deliberately fight the lace's
home band — bounded, phase-local ✓. 7 Legibility: every ask telegraphed (wedge/line/ring decals
exist); plating = visible armor tint + "shattered" flash; ward = shell visual; echo cue reused ✓.
8 Tiered timing: fusion Good/Perfect drives window length ✓. 9 Burden: P1 initiator-heavy (aimer),
P2 symmetric, P3 symmetric — kit offers both asymmetries ✓. 10 Clip law: P3 double-shove midpoint
aim into ward-break into burst is the 10-second clip ✓. 11 Latency: no new sync windows tighter
than existing ones; shield debt acknowledged ✓. 12 Kit-budget: encounter ASKS for the existing 4,
adds zero new player-facing mechanics ✓.

## Implementation plan (each phase = own todo, own commit, reviewed per policy) — ALL DONE

*(All four shipped + reviewed on `exp/duo-abilities`; todo files deleted on commit. Remaining
LOW/NIT items in `todo/N-boss{1,2,3,4}-review-followups.md` + `todo/N-telegraph-shapes-review-nits.md`.)*

- **BOSS-1 (arena + trigger + lifecycle)**: arena stamp + hash re-pins; `/boss` command (teleport,
  return position, countdown, reset/leave/victory rules); boss monster manifest entry + encounter
  engine scaffold (injected-seam tick engine, TelegraphScheduler pattern) with Cleave+Lunge only.
  Verify teleport = hard snap. Opus implementer; sonnet review.
- **BOSS-2 (P1)**: plating damage modifier at the monster-damage seam; fusion-shatter hook (a
  SkillshotEngine fusion event the encounter subscribes to); interposer drone behavior (monster
  framework midline-seek). Sonnet review (monster damage seam, not the player gate).
- **BOSS-3 (P2)**: Repel/Bind ring telegraphs + resolve; Echo Lash (reads shield state through the
  existing PlayerDamageGate absorb — **Fable review**, player-damage-path); splinter ring behavior.
- **BOSS-4 (P3)**: Core Ward + midpoint-break hook (MidpointDetonationEngine blast event); sweep
  beam; knockback pulse (server displacement); enrage; victory fanfare.

Tuning knobs (HP, damages, cadences, radii, window lengths) live in the boss's manifest entry /
encounter constants — expect a live tuning pass with the F1 Monster tab pattern after the first
full-fight feel-test.

---

## Shipped deltas (2026-07-05) — where the build diverged from the plan above

Build-time rulings and post-feel-test tuning. **These win over the plan when they conflict.**

**Arena / trigger.** Exterior `(356,356)-(379,379)`, 22×22 `DungeonStone` interior (masks the
grass-only node scatter for free); entry tiles `(367,361)`/`(369,361)`, boss spawn `(368,371)`.
`genVersion` was NOT bumped (it selects a generation *algorithm*, not a content revision) — the map
ContentHash + NodeCatalog CatalogHash were re-pinned instead (`BossArena.cs` is the single geometry
source). Teleport verified a hard predictor snap; a remote-partner snap needed the one client fix
(`RemotePositionInterpolator` resets on a >8u jump). **Victory now auto-ejects** lingering victors
to their return tiles after a 15s grace (BOSS-1 review MEDIUM — the shared, non-instanced arena
would otherwise soft-lock for the whole server) — this supersedes the plan's "no auto-eject."

**Cleave / Lunge + telegraph shapes.** The wedge/line telegraph shapes did NOT exist — BOSS-1
shipped Cleave as a **circle slam** + Lunge as a **dash with no telegraph**, deferred to a dedicated
protocol task. That task (protocol **v50**, honest render==hit shared membership in `TelegraphShape`)
then made Cleave a real 130° **wedge** and Lunge a **line** with its damage on the telegraph resolve.
A lunge-type boss also never falls back to the old instant untelegraphed dash (`ChargeEnabled &&
!LungeEnabled`).

**P1 Husk.** As planned. **Legibility (post-feel-test):** the boss steel-tints while plated; hits on
it now render **deflected** — a struck-through grey number for a P1 chip hit, the word **"TURNED"**
on the local predicted swing (plated = reduced, not zero, so no false "IMMUNE" vs the dropping health
bar); a world **teach label** "cross your skillshots to shatter!" floats over the boss. The
spawn-time plating broadcast is state-synced at entity introduction (BOSS-2 review HIGH — the edge
was dropped before any client knew the boss); the client clears plated ids on despawn (recycled
network ids would inherit a stale tint).

**P2 Sunder.** As planned. The Repel/Bind field **ring visual is a damage-0 telegraph drawn at each
player's FIRE-time position**, while the resolve is judged on pair distance 1.2s later — a
render≠hit-test exception (BOSS-3 review MEDIUM) **flagged for the feel-test / user decision** (the
honest alternative: draw the ring around the PARTNER at the rule radius). **Splinters TUNED** after
the feel-test ("waves too slow/weak"): move-speed multiplier `0.3→0.65` (~2.6 u/s) and pop damage
`12→18`. Displacement rides the new wall-swept `Zone.DisplaceResolved` (direct Zone tests added in
BOSS-4). Known Law-11 shield receipt-tick debt still accepted.

**P3 Core.** The **ward rides `BossPlatingMessage`** (ward up = plating-true = steel tint; zero new
protocol — the chat lines carry the semantic difference; teach label "detonate at its heart!" chosen
client-side by HP fraction). The boss **roots with dormant melee** (its brain is skipped while rooted
— cleave/lunge are silent; the beam/shove/aim are the P3 contest) — fork ruling, v1. The
**ward-breaking blast is a KEY, not damage** (it deals 0 to the boss; opens the window next tick) —
fork ruling, feel-test watch item. Root moved to `BossArena.CoreRootTile` (368,368) = the true
interior centre (BOSS-4 review MEDIUM — the spawn tile sat 3 tiles north, leaving a south beam-safe
band) and the **sweep beam grew 11u→16u** to cover the farthest corner. The rooted boss is excluded
from the monster-separation pass so trickle splinters can't walk it off-centre. **Enrage scales the
BEAM only** (not cleave — melee is dormant while rooted).

**Verification / model policy.** BOSS-1/2 sonnet-reviewed; BOSS-3/4 + the telegraph-shapes task
Fable-reviewed (protocol + player-damage-path high-risk band). The unkillable-boss class was
explicitly verified clean in duo, degraded-duo, and solo. Legibility layer is a client-cosmetic
self-reviewed change (the live playtest is its gate). See `.shared/memory/sunderer-encounter-status.md`.
