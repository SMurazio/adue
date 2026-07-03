# Game Direction — The Living Tower

**Status: DECIDED direction (user + orchestrator, 2026-07-03).** This is the thesis the project builds toward.
It fuses the UO-values read, three under-served market gaps, and the tower structure into one coherent game.
Supersedes the generic framing in `feature-roadmap.md` (which remains as the engineering-phase ledger).

**One line:** *UO's stakes with modern action hands — a living tower your community climbs, harvests, and can
know by name.*

---

## 1. The pillars

1. **The economy is the content engine.** Gather → craft → venture in gear you can lose → lose it → re-gear.
   Player-made items, real loss (full/mostly-full loot in risky strata). A loss-driven economy generates its own
   content forever — no quest treadmill, no content-cadence war a small team can't win. (EVE/Albion/OSRS lesson.)
2. **Action combat is the differentiator.** Dodge-roll i-frames, free-aim, telegraphs, movement actions — the
   thing already built and feel-confirmed. Nobody in the UO-descendant niche has genuinely modern action combat:
   Albion chose MOBA, the nostalgia shards chose clunk-as-authenticity. "UO's stakes with Hades' hands."
3. **Risk is a gradient, not a switch — and the gradient is the TOWER.** Floors = risk/reward tiers, legible in
   the world's own architecture. Safety at the base, escalating danger and richer resources climbing. Avoids
   both UO's gank-exodus and Trammel's dead safety. (Albion's zone lesson, made literal.)
4. **Community-scale worlds.** One world = one tower = one community (~200 concurrent ≈ 1.5–3k members). The
   social magic of old MMOs (reputation, recognizable names, "that guy") only functions below Dunbar-ish scale;
   above it players become scenery. Massive is a legacy assumption, not a requirement — private shards, group
   ironman, fresh-server rushes all show players choosing smaller worlds where they matter. Our 200-client
   single-process server is not a limitation on the road to "real" scale; it IS the product.
5. **A living ecology as the PvE engine.** Monster populations grow when unhunted, thin when farmed, migrate,
   respond. The road UO built and cut at launch, abandoned by the industry since. The PvE twin of pillar 1:
   simulation generates content. Also the POPULATION BALANCER — see §3.
6. **Session-shaped persistence.** A complete, consequential loop — gear up, climb, win or lose something real,
   log off with a story — fits in ~45 minutes. The audience that loves persistent worlds is 30–45 with jobs.

## 2. The tower (structure = design = infrastructure)

- **Floors are discrete bounded maps.** Fits the engine room exactly: bounded tile-grid terrain, single-process
  zones, and later the natural zone-per-process split (a hot floor becomes its own process — the Albion
  hot-cluster move, surgical because floors are already discrete).
- **The tower is ALIVE (flavor = mechanics).** Not a building — an organism the base settlement harvests. This
  one conceit makes the simulation canonical: populations regrow (it heals), overharvested floors wither (you
  wound it), it FIGHTS BACK against slaughter (surge events), and it GROWS new floors over seasons (content
  cadence, diegetically). What the tower is = the long-game mystery behind world-firsts.
- **Shape follows load:** low floors WIDE (multiple wings/root-caverns/parallel hunting grounds — the launch
  crowd splits by geography), high floors narrow (three parties at once is the intended density).
- **Expeditions are the session:** climb, push one floor past wisdom, extract or lose the haul on the descent.
  Extraction-run shape inside a persistent shared world — no lobby structure.
- Tone: charming-with-teeth (the Cato direction) — cozy base, hostile heights; the contrast is the fantasy.

## 3. Worlds, population, and the ecology-as-balancer

- **No channels.** Channels fork the ecology (which channel's floor 3 got overhunted?) and launder reputation
  (ganker on ch2, merchant on ch5) — they quietly kill pillars 4 and 5. One persistent instance per world.
- **The ecology redistributes players.** Overfarmed floors deplete visibly → players drift where the loot went;
  neglected floors overgrow (danger + riches) → a reason to go where nobody is; town noticeboard bounties on
  overrun floors = emergent content AND load-balancing in one mechanic. The same forces spread server load
  across future floor-processes. One system, both jobs.
- **LEGIBILITY IS LOAD-BEARING** (the #1 stress-test): depletion/overgrowth must be readable BEFORE a wasted
  session — weathered visuals, rumor boards, scout reports. Illegibility is where UO's original ecology died.
- **Between towers:** conservative. One-way emigration with real cost (skills travel, empires don't); settler
  incentives on young towers; **seasonal fresh towers** (OSRS-league / WoW-fresh energy) as the population
  lifecycle — a tower is founded, races, matures into legacy or merges its diaspora. No free transfers.
- **Perception of small:** never show global population; show local bustle. Size the base town deliberately
  small so thirty concurrent reads as a crowd. Market the scale ("a world where your name matters") — never
  apologize for it.
- **Launch plan:** many towers filled by waves; groups RESERVE TOGETHER (the launch failure mode is split
  friends, not queues); honest world browser (young/growing/mature); overflow towers spun up diegetically as
  new founding charters. Launch crowd on low floors → the tower-fights-back surge events turn crowding into
  content; contribution-ledger loot (built) means crowds don't kill-steal.

## 4. What each pillar implies next (bending the backlog)

| Arc | Pillar | Builds on (exists) | Status |
|---|---|---|---|
| **Telegraphed abilities** (windup → ground shape → resolve; dodge through it) | 2 | shared action executor, i-frames, `docs/ability-telegraph-sync-design.md` | NEXT combat arc (user-inclined; design doc needs re-grounding vs current code) |
| **Projectiles + a ranged kiter monster type** | 2 | collision resolver, snapshot replication (linear motion = best case for extrapolate-to-now), behavior manifest | after telegraphs |
| **Hit-reaction lite** (brief slow/stagger on hit) | 2 | MovementSpeedChanged replication end-to-end | cheap, any time |
| **Ecology v1** (regional populations + hunting pressure + regrowth + legible depletion) | 5, 3 | monster framework P0–P6, spawners, dormancy design (`todo/monster-ai-dormancy.md`) | needs its own design doc + the legibility stress-test |
| **Crafting/economy loop** (sinks, faucets, crafting mattering; death rules) | 1, 6 | gather/inventory/loot/corpse+eligible-looter systems | THE design-heavy arc; economy design discipline required |
| **Floor/strata world structure** (risk tiers, expedition shape, base town) | 3, 6 | seed-based terrain, zone design docs | design doc when map work resumes |
| **World lifecycle** (browser, waves, seasonal fresh, emigration) | 4 | single-process world, persistence | latest; needs nothing yet |

Explicitly NOT doing: classes/level treadmill; faithful-UO interface nostalgia (Outlands owns it); channels or
floor instancing (even "just for launch"); LLM-chatbot NPCs (the defensible AI is the simulation); distance-based
send-rate LOD (no far-tier in top-down — see `todo/N-tick-profile-at-density.md`); lag-compensation rewind (for
now); VR/mobile.

## 5. The two riskiest claims + cheap validation

1. **Ecology legibility** (pillar 5): players must read the world's state pre-session or the simulation feels
   like RNG theft. Validate cheap: ecology v1 on ONE floor with over-instrumented signals (visual wear states,
   a rumor board with real data), feel-test with stress bots simulating hunting pressure before any live test.
2. **World-size economics** (pillar 4): does a 1.5–3k-member community sustain a market deep enough for crafting
   to matter? Validate on paper first (sink/faucet model at 200 concurrent), then watch the first tower like a
   hawk. Fallback lever if thin: raise concurrent capacity (the parked density levers make 500/world plausible)
   rather than merging worlds.

## 6. Engineering posture (unchanged, now with a why)

The platform bets already made all point at this game: shared-C# deterministic sim (action feel), single-process
bounded zones (towers/floors), data-driven monsters (ecology), contribution loot (crowds), seed-based terrain
(floors), 200-client headroom with parked levers (`todo/N-tick-profile-at-density.md`). Godot client stays (see
the engine assessment: the view layer is thin; the hard logic is engine-agnostic and headlessly tested).
