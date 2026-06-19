# Two "content/state, not stream" model pivots (decided 2026-06-19)

Both came from asking "are we modeling this right?" and both *deleted* a class of wire traffic rather
than optimizing it. Don't re-propose the abandoned streaming approaches.

## 1. Static terrain ships as a SEED, not tiles (S42)
Static terrain is **content, not state** — it does not go on the wire. The map is procedural, so the
server sends `dims + seed + genVersion + contentHash` in `ZoneInfo` and the client regenerates the
identical map locally via the shared deterministic `TerrainGenerator`; the server stays authoritative
and a hash mismatch is logged as drift/tamper. Login terrain cost is ~constant regardless of map size.
**The chunked-streaming approach (old S36 / S36a) was built then ABANDONED** (preserved on branch
`wip/s36a-chunked-streaming`); only *dynamic* terrain (destructible/doors/player-built) should ever
stream, as AOI-gated state. For authored maps later: ship the map *file* + a version/hash, same shape.
See `docs/terrain-and-map-design.md`, `docs/movement-input-model.md` sibling.

## 2. Movement input is held-direction INTENT, not a MoveStep stream (S43)
For a no-prediction, server-authoritative tile-stepper, input is **state, not events**. The client sends
`MoveIntent(seq, moving, direction)` on change + a ~500 ms keepalive (reliable-ordered); the server
holds the intent and steps each entity at its own cooldown cadence. This **retired N21** (which was
tuning the redundant per-tick `MoveStep` stream) and removed the freeze-fix workaround — server-paced
stepping gives even cadence with no client-timing dependency. Wire is **v15**. See
`docs/movement-input-model.md`.

## The general lens (reusable)
Before optimizing a transfer, ask whether it should happen at all. Content ships with/regenerates on the
client; static world data isn't state; input is state the server simulates, not an event stream. Related
open instance: resource-node *placement* became deterministic world-content (S44); item/node *definitions*
are still code registries ("data files later"). Related: [[production-ready-intent]].
