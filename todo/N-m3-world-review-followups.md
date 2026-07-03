# N — M3 authored-world review followups (a8a89e6: APPROVE-WITH-FOLLOWUPS)

Determinism/coupling core verified sound (no client hard-fail path found; layout matches §4 including
the only-one-gate wall). The MEDIUM-HIGH finding (death respawn → legacy (8,8) wilderness) was FIXED by
the orchestrator immediately (RespawnPlayers → Zone.NextSpawnTile). Remaining:

1. **Death-respawn regression test** (required by the review, batched here): live-server style — kill a
   player (e.g. /slam with lethal damage), tick past the respawn delay, assert the respawned position is
   one of the authored S anchors (not (8,8)). Harness: TelegraphWireIntegrationTests drives a live
   GameServer and already records damage events.
2. **Only-one-gate row assert**: TownAndFloor1MapTests checks wall points, but after a deliberate map
   edit re-pins the hash, nothing structurally pins the single gate. Add: row y=111 contains EXACTLY 4
   walkable tiles (x191-194) — survives re-pins, catches an accidental second hole.
3. **Dump-to-ASCII** (D2a promised, not built): AuthoredMap gains a render-to-string[] so any stamped
   map can be eyeballed/diffed outside the live client; round-trip test (dump → Parse → identical).
4. **Cache the parsed authored layout**: CreateZoneInfoMessage re-parses all 147,456 tiles per login
   (single-digit ms — fine, but a static Lazy<TerrainLayout> for the authored version is one line).
5. Cosmetics/nits (batch opportunistically): H-anchor tiles paint as single Grass squares inside the
   cobble plaza (visible paint-bug jank under the 7 house sprites — either give H a context category or
   leave for the art pass, note in the doc); NextSpawnTile int wraparound at 2^31 logins ((uint) cast);
   unknown MMO_SPAWN_DISTRIBUTION silently falls back to Authored (was Distributed) — a typo moves 120
   stress bots to 6 plaza tiles; consider logging the fallback.

Also note (already known): persisted pre-M3 characters keep old SW-quadrant positions if walkable
(one-time migration jank, accepted); positions inside new walls silently round-robin to the plaza.

Standard band, mostly test-only. Sonnet-tier implementer per the model policy.
