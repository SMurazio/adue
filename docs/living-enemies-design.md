# Living Enemies — Design

Server-authoritative AI monsters (`EntityKind.Monster`) — the adversary that turns combat from "hit a dummy"
into a real loop. Built in phases.

## Phases
- **P1 — Leashed roam (DONE).** A monster anchors to its spawn tile (the leash *home*), mostly stands still,
  occasionally strolls within a roam radius, then re-pauses — believable idle, not a treadmill.
  `MonsterRoamAi` + `GameServer.StepMonsterAi` (in `TickCore`, paced off the step cooldown). Hittable, shows an
  HP bar, does not self-heal.
- **P2 — Fight back (in progress).** Aggro radius → chase (leashed) → attack on a cooldown → the *player* takes
  damage (HUD HP bar). No death yet.
- **P3 — Death + respawn + spawners (DONE).** A monster at HP 0 DIES (despawn + AI/type cleanup) and its persistent
  **spawner** respawns a fresh full-HP one at its tile after a per-type delay (`slime.respawnMs`, ~5 s). The spawner is
  a server object (NOT a replicated entity); its red tile is replicated via `SpawnerMarkerMessage` (keyed by a stable
  spawner id + an Active flag, AOI-driven) so it survives the kill/respawn. `/monster` creates a spawner. The PLAYER at
  HP 0 DIES too → after a global delay (`player.respawnMs`, ~2 s) it teleports to spawn at full HP (a "downed" guard
  blocks acting/dying twice meanwhile). Minimal — no corpse/loot/penalty/death-screen.
- **P4 — Loot** on kill.

## Server load model — READ before scaling monster counts
Two distinct costs, gated differently:

- **Replication (network + per-client snapshot work): AOI-gated.** A monster is only sent to a client inside
  that client's interest radius. Off-screen monsters cost ~0 network. Cost scales with *monsters visible to
  players*, not total count.
- **Simulation (server CPU per tick): currently ALWAYS-ON.** Every monster's AI ticks each server tick
  regardless of visibility (`StepMonsterAi` iterates all monsters). Per-tick CPU scales with the *total* monster
  count.

Monsters are **lighter than players** — no socket/connection, no inbound-input processing, no per-client send
loop for themselves. A monster ≈ a fraction of a player's cost. But the always-on simulation is not free.

## DESIGN DECISION — AI dormancy when unseen
**Monster AI MUST go dormant (skip the per-tick brain) when no player is within its AOI**, so off-screen
monsters cost ~zero CPU — matching the replication model. This makes total monster count nearly irrelevant to
baseline server load, letting the world be densely populated **without scaling down player capacity**. This is
a first-class design goal, not an optimization to bolt on later.

- **Why not implemented yet:** at low counts (a few test monsters) the always-on AI is negligible; per the
  project's *measure before optimizing* guardrail, doing it now is premature.
- **Trigger to implement:** when monster counts grow enough to measure — concretely, when **P3 spawners
  populate the world with many monsters**, or a stress/profile shows the per-tick monster-AI cost is material.
- **How (cheap to add):** the per-monster AI already locates nearby players (the aggro scan); gate the whole AI
  step on "is any player within this monster's AOI / aggro+leash radius?" and skip the brain if not. The current
  `StepMonsterAi` structure supports this directly. Tracked in `todo/monster-ai-dormancy.md`.

## Other scaling levers
- **Monster-only index.** `StepMonsterAi` currently scans ALL entities each tick to filter monsters
  (O(entities)). A dedicated monster list removes that sweep (flagged in the P1 review). Pairs naturally with
  the dormancy gate.
- **Aggro throttle.** The target scan is throttled (not per-tick) — done in P2.

## Monster TYPES (named templates) — P2-polish
A monster TYPE is a named server-side template (`MonsterType`) with its OWN stats + AI tuning:
`maxHealth`, `moveSpeedMultiplier`, `roamRadius`, `pauseMin/MaxMs`, `aggroRadius`, `chaseLeash`,
`attackRange`, `attackDamage`, `attackCooldownMs`. The registry (`MonsterTypeRegistry`) owns the table of
types + the live-tuning apply/clamp + the tick-quantisation. Today there is ONE type: id `slime`,
display `Slime`. A spawned monster (`/monster <name>`, default `slime`) remembers its type; `StepMonsterAi`
reads that type's `Tunables` + its `SpeedMultiplier` each tick (no global block anymore).

- **Slower than the player (outrunnable).** `slime.moveSpeed` defaults to **0.8** — the slime's effective
  step cooldown is derived from its `SpeedMultiplier` via the existing per-entity `EffectiveStepCooldown`
  path (tick-quantised at 20 Hz: round(5 / 0.8) = 6 ticks = 300 ms vs the player's 250 ms base), so you can
  outrun the dumb ones.
- **`/monster <name>`** spawns that type at the caller's tile (= the leash home). No name → `slime`; an
  unknown name → an error listing the available type ids.

## Tuning — per-TYPE, live + replicated (P2-polish)
The former GLOBAL `monster.*` keys were REPLACED by PER-TYPE keys of the form `<typeId>.<field>`, e.g.
`slime.roamRadius`, `slime.aggroRadius`, `slime.moveSpeed`, `slime.maxHealth`. They are live-tunable via the
existing `AdminSetTuning` path (owned by `MonsterTypeRegistry`, not `ServerTuningRegistry`) AND **replicated**
to clients via `MonsterTuningSnapshot` (protocol v33) so the F1 **Monster tab** (a per-type dropdown + the
selected type's fields) can show + edit the authoritative live values — mirroring the combat.* tab.
De-aggro range (×1.5 aggro hysteresis) and the aggro-scan cadence (~0.5 s) stay DERIVED.

## Spawner + red anchor (P3)
A **spawner** is a persistent server object (`MonsterSpawner`) that OWNS a monster: a fixed tile, a monster type, a
respawn delay, and (for now) <= 1 live monster. It spawns the first monster and, when that monster dies, schedules a
respawn and spawns a fresh full-HP one of its type at the same tile after the delay. The spawner OUTLIVES the monster's
death/respawn. `/monster <name>` creates a spawner (which spawns the first monster). The monster's leash HOME = the
spawner tile.

The red anchor tile is now the SPAWNER (replacing the per-monster `MonsterHomeMessage`): replicated via
`SpawnerMarkerMessage(SpawnerId, Tile, Active)` keyed by a STABLE spawner id (not a monster network id, which is reborn
each respawn), AOI-driven per recipient — Active=true on AOI-entry (place the red tile), Active=false on AOI-exit (drop
it). Because it tracks the spawner, the red tile STAYS PUT when the monster dies and a new one spawns. Protocol v34.

## Death detection (P3)
- **Monster death:** detected in `HandleAttack` after the free-aim resolver applies damage — any Monster victim at HP 0
  is killed (`KillMonster`): despawn the entity (`EntityDespawn` to viewers + remove from the world/spatial index),
  clean up `_monsterAi.Forget(id)` + `_monsterTypeOf.Remove(id)` (the P3 leak cleanup), and notify the owning spawner to
  schedule the respawn. The per-tick `RespawnMonsters` pass spawns the fresh monster when due.
- **Player death:** detected in `ApplyMonsterAttack` — when a monster's hit drives the player to HP 0, the session is
  marked dead (`ClientSession.MarkDead`) with a global respawn delay and gets a "You died." toast. The per-tick
  `RespawnPlayers` pass teleports it to spawn at full HP when due. While dead, the message dispatch suppresses movement/
  attack inputs and the held-move pacer skips it (the downed guard), and `ApplyMonsterAttack` no-ops on a dead target —
  so a downed player can't act, take further hits, or die twice.

## Known gaps / future polish
- **Attacks are NOT telegraphed.** A monster attack is an instant hit on its cooldown — no wind-up, no
  swing animation, no on-ground danger indicator before the damage lands. There is nothing to dodge/react
  to; the player just sees the number + the HP drop. A telegraph (wind-up tick + a brief ground/▲ indicator
  before the hit resolves) is a combat-FEEL item for a later polish phase.

## De-aggro conditions (why a monster drops a target)
A Chasing monster returns home (`Returning` → `Idle`) when ANY of:
- **Target lost or dead** — the target despawned/logged out, or its HP hit 0 (no death/respawn this phase,
  so a downed player is dropped).
- **Target beyond the de-aggro range** — Chebyshev distance to the target exceeds `~1.5× aggroRadius` (the
  hysteresis margin, so it doesn't drop the instant the target steps one tile past the acquire radius).
- **Pulled beyond `chaseLeash` from home** — the monster's Chebyshev distance from its HOME exceeds the
  type's `chaseLeash`, regardless of how close the target still is (the hard leash bound).
- (Also: a no-progress watchdog bails a chase wedged against a wall corner — see `MonsterRoamAi`.)
