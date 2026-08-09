# The base-camp reframe (Hades / Ember-Knights structure)

**Status: DECIDED (user, 2026-08-09, after feel-testing), scoped by a Fable review.** Reframes the P2
demo from "menu → straight into the fight" to a **Hades/Ember-Knights hub loop**: landing (presence
gate) → a small **base camp** → runs launched from a **portal you interact with** → win or lose →
back to the base camp.

## Why this is scope REMOVAL, not addition (the key finding)
The doctrine's "menu, not a place" (`docs/duo-living-tower.md`) was **never actually built** — what a
stranger duo lands in today is the full **384×384 MMO town** (houses, pond, harvest nodes, ecology
wings of slimes/gnolls). That is the worst case: all the hub cost PLUS MMO noise that contaminates the
exact signal P2 measures. Shrinking to a one-screen base camp makes the kill-test **cleaner, not
later**. This closes `duo-living-tower.md`'s open question with the early feel-evidence it deferred to.

## The flow (one commit beat, at the portal)
Title/landing = **presence gate only** (auto-pair + duo-card reveal; "waiting for your partner") →
both spawn into **base camp** → walk to the **run portal** together → **interact with it to ready**
(both ready → run) → clear/wipe → summary over the camp (`returnPlayer` already returns bodies to the
camp spawn tiles) → back in camp. The **practice doorway** is a second marker you may take or skip
before the portal. NO second ready gate on the landing — the landing is automatic presence; the portal
is the one commit beat with stakes.

## The minimal base camp (anything more is GILDING, banned before the gate)
- One small authored map (~48×48): a ~16×16 **non-grass** camp island (non-grass masks node scatter
  for free — the BossArena trick), clustered spawn anchors (pair lands facing each other), a **run
  portal** (interactable → ready), a **practice doorway** marker, + the arena and practice room as
  sealed teleport-only pockets re-homed onto this map.
- Ready = **interact with the portal object** (reuse the interact system + the RunEngine ready path),
  gated to "at the portal with your partner". BANNED: NPCs, shops, meta-progression, decoration beyond
  alphabet tiles, camera work, minimap rework, extract/haul, more than one screen.

## Map tone-down: author NEW (genVersion 3), shrink NOTHING, delete NOTHING
Bump the generator to **genVersion 3** with a new `AuthoredMaps.BaseCamp` stamp; `CurrentGenVersion`
2→3 flips the live world. `TownAndFloor1` + its whole test file stay in the tree UNTOUCHED (they test an
artifact that still exists — prune-on-friction = park, not delete). Ecology/spawner/harvest/AOI code and
tests reference the OLD map explicitly and keep passing. Do NOT surgically shrink TownAndFloor1 (every
coord is interdependent + pinned).

## The 3 commits (combat interior never destabilized)
1. **Shared + tests, world STILL v2:** `BaseCamp` stamp + the v3 generator branch + re-homed
   `BossArena`/`PracticeRoom` consts (KEEP the 24×24 interior shape identical — do not touch the arena
   interior geometry, that's the gated combat) + `BaseCampMapTests` + the ContentHash/CatalogHash pins.
   Old world still live → full suite green proves coexistence. **(This todo: `S-reframe-base-camp-map`.)**
2. **The flip:** `CurrentGenVersion` → 3; `ServerOptions` v3 dims derivation (it hard-throws on
   mismatch, `ServerOptions.cs:~150`); ecology/spawner demo-config guard (their rects are on the old
   map). Nothing else in this commit — full gate + a live Sunderer run.
3. **Client flow:** landing = presence gate + duo-card (drop "press B to begin"); the **interactable run
   portal** in camp (interact → ready, gated to the portal); the practice-doorway marker. Feel-gated.

## Landmines (each verified in-tree by the review)
- ContentHash pin (`TownAndFloor1MapTests`) + NodeCatalog CatalogHash: NEW pins for the new map via the
  M3-F1 process (orchestrator runs the gate, pastes the computed value — never guessed).
- `ecology.json` region rects + `RegionSpawnPlanner`: literal rects on the old map → empty/guarded
  config on v3 (disable-by-config, don't delete). Check `EcologyRegistry` boot-validation.
- `ServerOptions` hard-throws on dims mismatch → v3 derivation + `ServerOptionsTests` v3 case.
- `MinimapTransformTests` pins `MapSize = 384` (client) — old-map test; minimap likely hidden on a 48×48
  camp (post-gate polish).
- Pocket consts move; entry/spawn/core tiles derive; **keep the 24×24 interior identical** so the
  tether/beam geometry (the `BossSpawnTile` sweet-band note) is unchanged.

## Doctrine to amend (when landing the reframe)
`docs/duo-living-tower.md`: P2 directive "menu, not a base camp" → "a **pocket base camp, not a
town**"; close the open question as DECIDED 2026-08-09 (early feel-evidence); note the commit-together
beat now has a physical form (co-present at the portal). `docs/duo-p2-demo-plan.md` workstream C → the
flow above.
