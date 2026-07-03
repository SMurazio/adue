# Ecology v1 — Design (PROPOSED, orchestrator, 2026-07-03)

**Status: PROPOSED — forks decided by the orchestrator, awaiting user sign-off on the §2 decisions.**
Pillar 5 of `docs/game-direction.md` (living ecology as the PvE engine) + pillar 3 (the ecology
redistributes players), scoped to the §5 riskiest-claim validation: ONE zone, over-instrumented,
legibility first. Written implementation-ready for lower-capability implementers: every fork is decided
with its why; tasks in §8 carry file pointers and testable acceptance criteria.

**One line:** regions own a slowly-regrowing population *stock* per monster type; killing draws the
stock down, neglect grows it past normal; the world state is readable before you walk there.

## 1. Ground truth this builds on (verified 2026-07-03)

- Spawning today is `/monster` admin-command only — a runtime `MonsterSpawner` at the sender's tile,
  ≤1 live monster, fixed `respawnMs` timer (`GameServer.cs` ~2366/2432/2622, `MonsterSpawner.cs`).
  There is NO authored spawn content and NO population notion. Ecology v1 replaces this substrate.
- No kill aggregation exists (ContributionLedger is per-monster damage→looter-eligibility, destroyed on
  death — do not touch it). No region/area concept exists (one flat 128×128 seeded `Zone` "sandbox").
  SQLite persists characters/items only; world state regenerates from seed.
- Monster types (slime, gnoll) are data-driven via `Content/monsters.json` + `MonsterTypeRegistry`
  (clamped knobs, F1 live-apply via `AdminSetTuning`, explicit `SaveMonsterTuning` writes the file).
  The ecology follows this exact pattern: authored JSON + clamped registry + admin live-tuning.
- Monster AI dormancy (`todo/monster-ai-dormancy.md`) is designed but unimplemented; its stated trigger
  ("when spawners populate many monsters") is exactly this arc — it becomes prerequisite task E0.
- Stress bots cannot attack (move/connect/chat only) — hunting-pressure simulation needs new tooling (E7).

## 2. Decisions (the forks, each with its why — veto here, cheaply)

**D1. Population stock, not respawn timers.** Each region×type owns a real-valued stock `S ∈ [S_min, S_max]`.
A kill decrements S by 1 permanently (the timer-respawn model is deleted for ecology types). Live
monsters materialize from the stock (≤ `maxLive` at once, spawning while `liveCount < floor(S)` after a
short per-spawn pacing delay). WHY: a 5s timer makes hunting pressure literally invisible — the stock is
the single number that makes farming *matter*, the core pillar-5 claim.

**D2. Logistic regrowth + depleted-band suppression + decaying pressure.** Per ecology tick (every
10 s): `S += r · S · (1 − S/K) · depletedFactor` where `depletedFactor = min(1, S / 0.25K)` (Allee-style
suppression — REVISED during E1: pure logistic recovery time scales with the LOG of the deficit, capping
the brink-vs-half recovery ratio at ~2.5× no matter the tuning, which fails the intent below; the E1
implementer proved the original ≥10× acceptance bar unreachable). Per-minute rate `r` authored per
region×type; pressure decays `*= 0.98` (≈half-life 5.7 min), `+= 1` per kill. Overgrowth: while
`pressure < pressureIdleThreshold` (default 0.5) growth continues past K at `r/3` (same depletedFactor
applies, inert above 0.25K) up to `S_max = 1.5K`. WHY: a hunted-to-the-brink region crawls through the
DEPLETED band (the wound — overharvesting keeps the region visibly broken for a session) while THIN and
above recover at normal logistic speed; an unhunted region drifts to K and overgrows into the
danger+riches destination. WHY a decaying counter: "recently hunted" needs a memory with a horizon, not
raw tallies.

**D3. No local extinction.** `S_min = max(0.05·K, 0.5)`. WHY: full extinction is dead content with a
near-permanent recovery (logistic growth from 0 is 0); the brink is punishment enough and the rumor
text ("hunted to the brink") carries the story.

**D4. Regions are authored tile rectangles on the one sandbox zone.** New `Content/ecology.json`:
regions with id, display name, rect bounds (tiles), and per-type entries {K, rPerMinute, maxLive,
spawnTiles[] (explicit authored tiles — v1 does NOT scatter-seed; predictable spawn spots are easier to
author, test, and read)}. WHY rectangles-on-one-zone: the floor/strata arc will make a floor = a region
set; regions must exist before floors, and rectangles need zero terrain work. 3 starter regions
(§7) prove the read.

**D5. Five legible states, replicated; fuzzy words, never numbers.** Per region×type, derived from S/K:
DEPLETED (<0.25) / THIN (<0.6) / HEALTHY (<1.0) / RICH (<1.25) / OVERGROWN (≥1.25). Players see
state words and visuals only — exact stocks are admin-only. WHY: legibility is load-bearing but exact
numbers turn the ecology into a spreadsheet and kill rumor-flavored uncertainty.

**D6. v1 legibility surfaces (over-instrumented on purpose, per game-direction §5):**
  (a) minimap region shading by worst-type state (green→amber→red→violet for overgrown) — pre-walk
  readability on an EXISTING surface (`UI/Minimap.cs`);
  (b) `/rumors` chat command, available to ALL players: one flavored line per region ("Gnolls overrun
  the eastern scrubland", "The slime hollow is hunted to the brink");
  (c) a login rumor: the single most extreme region announced as a system line.
  A world-object noticeboard is deliberately NOT in v1 (no interactable-world-object tech exists; it
  arrives with the base-town/strata arc). WHY: two existing surfaces + one command = zero new tech.

**D7. Overgrown = more AND meaner, cosmetically visible.** In OVERGROWN state, `maxLive` +50% (rounded
up) and spawns get +25% maxHealth and +25% renderScale (existing knob — visibly bigger). No new elite
system, no loot changes in v1 (loot richness belongs to the economy arc). WHY: the visible payoff for
"go where nobody is" using only existing per-monster fields.

**D8. Ecology state persists.** New SQLite table `region_populations(region_id TEXT, type_id TEXT,
stock REAL, pressure REAL, updated_at_tick INTEGER, PRIMARY KEY(region_id, type_id))`, written on the
existing character-checkpoint cadence and on shutdown, loaded at boot (missing rows seed at K). WHY: a
restart that heals the world makes hunting pressure a lie; this is the first world-state (non-character)
persistence, and the ecology is exactly the state that must survive.

**D9. Migration is v2.** No inter-region movement of stock in v1. WHY: migration multiplies the
legibility problem (why did MY region change?) before the base read is validated. The stock model makes
v2 migration a pure transfer function later.

**D10. The `/monster` dev command stays** (spawns a spawner OUTSIDE any region — ecology ignores
orphan spawners; their timer respawn keeps working for dev testing). WHY: don't break the dev loop.

## 3. Server model (implementation shape)

- `EcologyState` (new, `src/Mmo.Server/Runtime/`): owns regions (from `Content/ecology.json` via an
  `EcologyRegistry` mirroring `MonsterTypeRegistry`'s load/clamp/save pattern), the per-region×type
  {stock, pressure}, the 10s tick (`EcologyTick(serverTick)` called from `TickCore` every 200 ticks),
  and the spawn-target computation. Headlessly testable: inject a clock/no world dependency for the math.
- Spawning: `RegionSpawner` generalizes `MonsterSpawner` — owns a region×type, its authored spawnTiles,
  live set (`maxLive`), and paces materialization (one spawn per 2 s per region×type while
  `liveCount < floor(S)`), skipping any spawn tile with a player within 6 units (no face-spawns; skip,
  don't queue). Existing single-monster `MonsterSpawner` remains for `/monster` (D10).
- Kill hook: in `KillMonster`, if the dead monster belongs to a region spawner: `stock -= 1` (clamped to
  S_min), `pressure += 1`. ContributionLedger and loot flow UNTOUCHED.
- Wire: `RegionEcologyMessage` (new, next protocol version): region id, rect, per-type {typeId, state
  enum byte} — sent on login and on any state-enum change (state changes are rare; no per-tick traffic).
  Client consumes it for the minimap shading + `/rumors` is SERVER-side text (no client parsing).
- Admin: `/ecology` chat command — no args: dump exact stocks/pressures (admin eyes only); 
  `/ecology set <region> <type> <stock>` and `/ecology pressure <region> <type> <n>`: force state for
  testing (mirrors the F1 knob philosophy: live, no restarts).

## 4. What v1 explicitly does NOT do

No migration (D9), no LLM/behavior changes to individual monsters, no loot-table changes (economy arc),
no noticeboard world object, no per-player rumor personalization, no new zones/floors, no scatter
seeding, no surge events ("tower fights back" — a later arc; the pressure counter is its natural input),
no ecology on non-region `/monster` spawners.

## 5. Acceptance criteria (arc-level)

1. Headless: logistic math converges (S→K from below without overshoot at authored rates; brink recovery
   from S_min takes ≥5× longer than from K/2 via the D2 depleted-band suppression — REVISED from ≥10×,
   which is unreachable under any pure logistic; the wound is real); pressure decays to idle in ~15 min;
   state-enum boundaries exact at 0.25/0.6/1.0/1.25.
2. Headless: killing N monsters in a region drops the stock by exactly N (clamped), and live-count
   convergence follows floor(S) within pacing bounds; no spawn within 6u of a player; the pending/live
   sets never leak on despawn/clearspawners.
3. Restart: stocks/pressures survive a server restart bit-for-bit (modulo one partial ecology tick).
4. Wire: RegionEcologyMessage round-trips; a login receives all regions; a state flip sends exactly one
   update; protocol.md updated (drift test).
5. LIVE (human): with the 3 starter regions, farm one region for ~10 minutes → its minimap shade and
   /rumors line degrade visibly DURING the session and the region is observably emptier; leave it alone
   ~20 minutes → recovery is visible without relogging. The §5 game-direction test: a player who reads
   /rumors + minimap BEFORE walking can predict what they'll find.
6. Perf: with all regions at overgrown maxLive and zero players nearby, tick cost is within noise of
   today's baseline (dormancy E0 doing its job) — verified by the standard 120-client stress gate.

## 6. Validation protocol (riskiest-claim discipline)

Phase A (headless): unit tests above. Phase B (bot pressure): stress client gains a `--hunt` mode (E7:
bots pick the nearest monster, walk into attack range, send the existing attack message) — run 20 bots
hunting one region for 10 min, assert the stock curve + state transitions from the admin dump. Phase C
(human feel-test): §5.5 above — the ONLY pass/fail that matters is "I could read the world before
walking". If Phase C fails on legibility, STOP and redesign the surfaces (per game-direction:
illegibility is where UO's ecology died) — do not proceed to migration/surges on an illegible base.

## 7. Starter content (ships with E2)

Three regions on the current 128×128 sandbox, chosen for distinct reads: **Slime Hollow** (slimes,
K=10, r=1.0/min — fast, forgiving, near spawn), **Eastern Scrubland** (gnolls, K=8, r=0.4/min — slower,
punishing), **The Verge** (both types, K=6 each, r=0.25/min — far, easily overgrown at the idle
threshold). Numbers are seeds for F1-style live tuning, clamped by EcologyRegistry (K ∈ [1,64],
r ∈ [0.05, 10]/min, maxLive ∈ [1,32]).

## 8. Task decomposition (each = one todo + one commit; sized for a lower-model implementer)

- **E0 — dormancy + monster index** (prereq; `todo/monster-ai-dormancy.md` promotes to S): per-monster
  brain gating on nearby-player + a monster-only index replacing the O(entities) sweep. Gate: stress
  with ~100 idle far monsters ≈ baseline tickMs.
- **E1 — EcologyState + registry + math** (server-only, no spawning): ecology.json load/clamp/save,
  stock/pressure/state machinery, EcologyTick, `/ecology` admin command. Tests: §5.1.
- **E2 — RegionSpawner + starter content**: materialization from stock, pacing, no-face-spawn rule,
  kill hook (stock/pressure), D7 overgrown modifiers, D10 orphan-spawner coexistence. Tests: §5.2.
- **E3 — persistence**: migration 006 (`region_populations`), checkpoint/load. Tests: §5.3.
- **E4 — wire + minimap + /rumors**: RegionEcologyMessage (protocol bump + docs/protocol.md), minimap
  shading, /rumors + login rumor server-side text. Tests: §5.4 + a minimap state-mapping unit test.
- **E5 — feel-test + tune** (human + orchestrator): §6 Phase C protocol, live knob tuning, verdicts
  recorded back into this doc.
- **E7 — stress `--hunt` mode** (parallel anytime after E2): bot attack-intent for Phase B. 

Order: E0 → E1 → E2 → E3 → E4 → (E7) → E5. E1's math and E4's wire are independently reviewable;
full-rigor review applies to E2 (touches the kill path's surroundings) and E3 (first world persistence).
