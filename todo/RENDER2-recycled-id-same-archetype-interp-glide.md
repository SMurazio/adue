# RENDER2 — recycled NetworkId of the SAME archetype inherits the prior entity's interpolation state

PRIORITY N. Pre-existing (NOT introduced by the EntityRenderer archetype self-heal, commit 632ffa9). Surfaced
by that fix's independent review, 2026-06-21.

## Issue
The renderer fix (632ffa9) rebuilds a visual when a recycled NetworkId needs a DIFFERENT archetype (resource
landing on a departed player's id). But when a freed id is recycled for a new entity of the SAME archetype
(e.g. a new player on a departed player's id, after a loss-dropped despawn), Core reuses the EXISTING
`TileInterpolator`: `MmoClient.UpsertEntity` (~`MmoClient.cs:1431-1448`) keeps the prior interpolator and just
`ApplySnapshot`s the new tile — so the new entity briefly glides from the OLD entity's last position instead of
snapping in. Cosmetic, at most ~one tile, only on a recycled-id-after-lost-despawn.

## Fix direction
Reset/rebind the interpolator (and any per-entity render state) when a NetworkId is reused for a different
entity identity (CharacterId / Kind / DisplayName change), or when an `EntitySpawn` arrives for an id already
known — so a recycled id starts fresh rather than inheriting the prior occupant's interpolation. This is a Core
change (the renderer layer is already handled). Consider whether despawn delivery should be made reliable as a
deeper fix (so the id isn't recycled with a stale client view in the first place).

## Acceptance
A recycled NetworkId (same archetype) starts its interpolation at its own first confirmed position (no glide
from the prior entity). Gates green.
