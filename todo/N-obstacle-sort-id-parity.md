# N — entity-obstacle sort parity: server sorts by entity Id, client by (recycled) NetworkId

From the Phase D independent review (F4, LOW; PRE-EXISTING — walking has it too, the dash just routes
through the same gather).

**The gap.** `Zone.GatherEntityObstacles` sorts obstacles by `WorldEntity.Id` (`Zone.cs:~230`) — the
monotonic `_nextEntityId++`. The client sorts by **NetworkId** (`MmoClient.cs:~647`), which is RECYCLED from
a pool (`NetworkIdPool`). The `Zone.cs:229` comment claiming "the client gather sorts by the same Id (the
shared NetworkId)" is FALSE as written. After any despawn/reuse cycle (e.g. a monster respawn), the two sort
orders can invert.

**Why it matters.** The collision resolve is Gauss-Seidel (order-dependent): a move contacting **≥2
overlapping bodies** can resolve to different contacts on client vs server → crowd rubber-band. Needs
simultaneous multi-overlap + an id-order inversion, so rare in practice — but it silently breaks the
Id-sorted crowd-parity guarantee the e5892b2 fix was for.

**Fix directions.** Make both sides sort by the same key the client actually has: NetworkId on the server
gather too (it's on WorldEntity), OR replicate/derive a stable sort key. Verify with a parity test that
spawns/despawns to force id-order inversion, then resolves a 2-overlap contact both sides.

Netcode-adjacent → headless repro + parity test + review. Relates to [[entity-collision-predicted]].
