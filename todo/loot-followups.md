# N — loot follow-ups (from the P4a review)

P4a (the loot engine) shipped SHIP. Three items the reviewer flagged as latent/coverage-depth — address in **P4b,
before loot content grows beyond the current 1-deep acyclic seed**:

## 1. Construction-time cycle detection for nested tableRefs
`LootTableRegistry` construction validates only that referenced table ids EXIST, not that the nesting graph is
ACYCLIC. An authoring cycle (A→B→A) is caught only at ROLL time (skip+log at the depth-8 guard) — safe (no stack
overflow) but every rolled kill pays up to 8 wasted hops AND emits a warn-spam line. Add an acyclic-graph check at
registry construction so a bad table fails fast at startup, not once per kill. Latent now (the seed is 1-deep,
acyclic); matters the moment more nested pools are authored.

## 2. (coverage) `slime_core` rate test + an explicit independence assertion
The rarest drop (`slime_core`, 0.0008) has no rate test, and the "each drop resolves independently" claim has no
dedicated assertion (it's structurally true — a fresh `NextDouble()` per drop, no shared state — and implicitly
exercised, but not directly asserted). Add both when touching the loot tests in P4b.

## 3. (note) Rare-tail statistical tolerance is loose
`LootTableTests` rare-tail check is ±0.0015 around 0.004 (~±37%): it catches gross errors (2×, inversion) but
would pass a subtle ~30% bias. Fine for a foundation gate; tighten with more rolls if precise calibration ever
matters.

## 4. (P4b nit) No GameServer-level integration test for the loot wiring
The corpse/ledger LOGIC is exhaustively pure-tested, but the live GameServer WIRING — `HandleAttack` ledger hook,
`KillMonster`→`RollAndSpawnCorpse`, `HandleCorpseLoot`, `DecayCorpses` — has no headless integration test (no
codebase precedent for an attack-to-death GameServer harness). The P4b reviewer traced it line-by-line + the human
live-verifies. If a GameServer-level test harness ever lands, add a kill→corpse→loot→despawn + decay integration test.

## 5. (P4c nits, optional polish)
- **No "bags full" feedback on a failed single Take.** When a window `TakeItem` fails (inventory full / unknown key),
  the server just re-sends unchanged contents — the row silently doesn't move. Add an `inventory_full` toast like the
  old grab-all path (`HandleLootAction`, the `Took==false` branch).
- **Unvalidated rarity byte on `ReadCorpseContents` decode** — unlike sibling readers it doesn't range-check the
  `(Rarity)` cast. Server→client only + the window falls back to Common, so no crash; tighten for consistency.

## 6. (P4c deferred) Corpse-on-the-dead-monster visual position
The corpse spawns at the authoritative death TILE (correct), but a fast monster's CLIENT visual lags the tile (interp),
so the corpse can appear ~1 tile off from where the monster visually died. Lowering slime speed to 0.6 shrinks it.
A precise fix means rendering the corpse at the dead entity's LAST RENDERED client position — needs new-Corpse↔dead-Monster
correlation + an interp-offset carry (the renderer keys by network id, no last-position cache). Deferred as not worth the
hack; revisit if it bugs at the slower speed or for faster future monsters.
