# S — P2-A: practice room + scripted dummy (server infrastructure)

Part A of the P2 demo (`docs/duo-p2-demo-plan.md`): a bounded space a pair enters to rehearse the 4
duo verbs against a non-aggressive dummy BEFORE the real run. This task is the SERVER-SIDE
infrastructure (headless-testable); the client entry UI + the teaching hints are P2-B (follow-on).

All seams already exist (per the client map) — mirror the boss-arena pattern:

1. **`PracticeRoom` pocket** — a new static mirroring `src/Mmo.Shared/Domain/BossArena.cs`: a second
   SEALED authored pocket (pick empty tiles away from town + the NE boss arena), a 1-tile wall ring,
   floor authored as `DungeonStone`, fixed entry tiles (one per partner) + a `DummySpawnTile`, and a
   `ContainsInterior(tile)` membership test.
2. **Stamp + carve-out** — stamp its walls/floor in `AuthoredMaps.BuildTownAndFloor1` (alongside the
   arena stamp), and carve it out of the reachability invariant exactly as the arena is (it's a sealed
   pocket, not walkable-from-town).
3. **`"dummy"` monster type** — add to `src/Mmo.Server/Content/monsters.json`: `aggroRadius 0` (the
   aggro test `Distance <= 0` never fires → never chases/attacks — verified in `BasicRoamerBehavior`),
   a large-ish `maxHealth`, no loot. If roaming is unwanted, a trivial `StationaryBehavior` (mirror
   the tiny `SplinterBehavior`/`InterposerBehavior`) keeps it fully immobile — surface which as a fork.
4. **Enter / leave path** — a `/practice` chat command (dev + the future menu action) that teleports
   the issuer (+ their `/pair` partner when paired AND online) to the practice entry tiles and spawns
   the dummy via `SpawnMonsterCore` at `DummySpawnTile`; leaving (`/practice off`, or the same command
   toggling) teleports back to town (`_zone.NextSpawnTile()`) and despawns the dummy (own the lifetime
   — it's not a spawner). Reuse the `_zone.Teleport` + `SpawnMonsterCore`/`DespawnBossEntity` seams.

## Guardrails / forks to surface (don't guess)
- **Interaction with the run loop:** a player IN a run must not `/practice` mid-run, and a player in
  the practice room must not be counted as a run participant. Decide + state the rule (e.g. `/practice`
  only from `RunPhase.Lobby`; refuse otherwise with a message). Surface as a fork if ambiguous.
- **Dummy lifetime with two sessions:** despawn the dummy when the LAST occupant leaves (partner may
  leave separately); don't leak a dummy or despawn it under a still-practicing partner.
- No protocol/wire change is expected (chat command + existing teleport/spawn). If you need one, STOP
  and surface it (docs/protocol.md drift gate).

## Acceptance (headless, `ClearSpawnersIntegrationTests`/`RunLoopSessionIntegrationTests` style)
- `/practice` teleports a solo caller into the room (`PracticeRoom.ContainsInterior` true) and spawns a
  dummy; `/practice off` returns them to town and despawns it.
- A paired caller brings the online partner; the dummy despawns only when both have left.
- The dummy is non-aggressive: it never targets/attacks a player standing next to it.
- `/practice` is refused during an active run (whatever rule you land); a practice occupant is not an
  `IsRunParticipant`.
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd`; delete this file in the landing commit.
