# N — loot follow-ups (from the P4a review)

P4a (the loot engine) shipped SHIP. Items #1 (construction-time tableRef cycle detection) and #2 (`slime_core`
rate pin + a direct independence assertion) have SHIPPED and are trimmed from this file. Remaining:

## 3. (note) Rare-tail statistical tolerance is loose
`LootTableTests` rare-tail check is ±0.0015 around 0.004 (~±37%): it catches gross errors (2×, inversion) but
would pass a subtle ~30% bias. Fine for a foundation gate; tighten with more rolls if precise calibration ever
matters.

## 4. (P4b nit) No GameServer-level integration test for the loot wiring
The corpse/ledger LOGIC is exhaustively pure-tested, but the live GameServer WIRING — `HandleAttack` ledger hook,
`KillMonster`→`RollAndSpawnCorpse`, `HandleCorpseLoot`, `DecayCorpses` — has no headless integration test. NOTE:
a live-loopback GameServer harness precedent NOW EXISTS (`MonsterHopPacingIntegrationTests`,
`ClearSpawnersIntegrationTests`) — an attack-to-death kill→corpse→loot→despawn + decay test is now buildable on
that pattern when worth the effort.

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
