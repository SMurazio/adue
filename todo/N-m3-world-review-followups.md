# N — M3 authored-world review followups (a8a89e6: APPROVE-WITH-FOLLOWUPS)

Determinism/coupling core verified sound (no client hard-fail path found; layout matches §4 including
the only-one-gate wall). The MEDIUM-HIGH finding (death respawn → legacy (8,8) wilderness) was FIXED by
the orchestrator immediately (RespawnPlayers → Zone.NextSpawnTile). Remaining:

1. ~~Death-respawn regression test~~ **DONE** (test-batch-1: live lethal /slam → respawn asserted on an
   authored S anchor).
2. ~~Only-one-gate row assert~~ **DONE** (GateRowHasExactlyFourWalkableTiles).
3. ~~Dump-to-ASCII~~ **DONE** (AuthoredMap.ToAsciiRows + round-trip tests on alphabet + real maps).
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
