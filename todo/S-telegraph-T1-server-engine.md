# S — Telegraph arc T1: server-side scheduled telegraph + resolve engine (NO wire, NO rendering)

First phase of the telegraph combat arc (docs/game-direction.md §4 pillar 2; model: docs/
ability-telegraph-sync-design.md — the deadline form: schedule at resolveTick T, resolve at T against
positions AT T). Server-only so the model is proven headless before any protocol/rendering work (T2).

## Scope

1. **TelegraphShape** (shared, Mmo.Shared/Domain): CIRCLE only this phase (origin WorldVector + radius units).
   Shape membership test on continuous positions. (Cone/line are later content, the seam must allow them.)
2. **Server schedule**: a GameServer-owned list of pending telegraphs {id, casterId, shape, resolveTick,
   damage}. Each tick, resolve every telegraph whose resolveTick arrived: gather candidates via the spatial
   grid (superset query + exact shape test — the AOI gather pattern), apply damage to PLAYERS in the shape.
   Casters/monsters unaffected this phase (no friendly fire, mirroring ApplyMonsterAttack's targeting).
3. **THE DAMAGE CHOKE POINT (closes todo/N-iframe-gate-choke-point.md — delete it in this commit):** extract
   a single `DamagePlayer(victim, amount, source)`-style seam used by BOTH ApplyMonsterAttack AND the
   telegraph resolve, with the dodge-roll i-frame gate (`_actionExecutor.HasActiveIFrames`) INSIDE it — every
   current and future player-damage path routes through one gate. Add the test through the REAL seam that
   the review flagged as missing (deleting the gate must fail a test).
4. **Trigger for testing**: a monster ability primitive (id e.g. "slam") wired through the EXISTING per-type
   ability seam (like the gnoll charge): behavior decides (target in range + cooldown) → schedules a circle
   telegraph at the TARGET's position AT CAST TIME (locked origin = dodgeable), windup ~1.5s (30 ticks).
   Manifest: add the ability to the SLIME type (its first real attack pattern) with tunable knobs
   (radius/windup/damage/cooldown) via the data-driven descriptor path. Plus an admin/dev chat command to
   force-cast one at a position for testing.
5. **Explicitly NOT in scope**: any protocol change, any client work, cone/line shapes, player-cast
   telegraphs, monster-vs-monster damage. DamageEvent/HP replication already exists and just works when
   damage lands.

## Acceptance criteria

- Headless: a scheduled telegraph resolves at EXACTLY tick T; a player inside the circle at T takes damage;
  a player who was inside at cast but stepped/dodged out by T takes NOTHING; one who dodged INTO it at T is
  hit (positions AT T, never at cast).
- I-frames: a mid-dodge-roll player inside the shape at T is NOT damaged; the same test proves the choke
  point (remove the gate → test fails). ApplyMonsterAttack behavior unchanged (existing tests stay green).
- Livelock/cleanup: telegraphs from a despawned caster still resolve or are dropped cleanly (decide + pin);
  the pending list never leaks.
- Gate green; INDEPENDENT REVIEW (server combat/damage-path change — full rigor).

Builds on [[movement-actions-framework]] (executor/ability seam) + [[monster-behavior-architecture]] (manifest).
T2 (wire + client fill rendering + synced clock) and T3 (content/tuning) follow in separate tasks.
