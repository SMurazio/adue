# N — i-frame damage gate: single inline site + no test through the real seam

From the Phase D independent review (F3, LOW). The dodge-roll i-frame negation gate lives ONLY inline in
`GameServer.ApplyMonsterAttack` (~`GameServer.cs:3740-3744`). Verified: that IS currently the only path that
damages a player (free-aim excludes players via `IsAttackableEnemy`; no DoT/self/environmental damage). So
authority holds today. Two hardening gaps:

1. **No choke point.** Any FUTURE player-damage path (PvP, hazards, DoT) silently bypasses i-frames. Move
   the check into (or adjacent to) a single player-damage choke point every future path must route through.
2. **Test seam.** `ActionIntentHandlerTests.IFrameAuthority_*` re-implements the gate order in a local
   `ResolveHit` lambda — deleting the real gate from `ApplyMonsterAttack` would pass all tests. Add a test
   through the REAL damage path (the loopback GameServer harness precedent exists —
   `ClearSpawnersIntegrationTests`: spawn a monster, let it hit a rolling vs non-rolling player), or at
   minimum through `ApplyMonsterAttack` itself.

Acceptance: the i-frame gate sits on a choke point (or every damage path provably routes through it), and a
test fails if the gate is removed from the real path. Builds on [[movement-actions-framework]].
