# N — Monsters regen to full while leash-returning home (design decided by orchestrator, user delegated)

User (2026-07-03): "when monsters reset they should probably heal to full while they roam back? idk if
it's interesting design so I leave that choice to you."

**Decision: fast visible regen DURING the return walk, guaranteed full by arrival home.** Rationale:
- Anti-cheese (fair): without it, chip-damage across repeated leash pulls kills anything risk-free.
- More interesting than the WoW-style instant-full-heal-on-evade: the recovery is VISIBLE and creates a
  real decision — re-engage the wounded monster mid-return (it fights back with what it has) or concede
  the reset. Legibility is the pillar-5 discipline: the HP bar climbing as it walks home tells the story.
- Living-ecology flavor: creatures recover; the world does not hold your half-finished fights.

Implementation sketch: in the leash/return-home behavior state, restore health per tick at a rate derived
from distance-home / speed so arrival ≈ full (or a simple % max per tick floored to reach full within the
typical return); replicates free via the existing snapshot HP field. Add a behavior test (damaged monster
leashes → HP climbs during return → full at home; re-aggro mid-return interrupts regen? DECIDE: keep
regenerating while re-fighting = NO — regen only in the returning state).

Standard band: one implementer or orchestrator-direct; behavior tests; no independent review unless the
damage path is touched (it should not be — this is healing, not damage; do NOT route through
PlayerDamageGate, that gate is player-damage only).
