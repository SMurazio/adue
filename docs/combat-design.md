# Combat design — tile-pattern attacks

Design record (user + orchestrator, 2026-06-21). The model that plays to the tile-stepped, server-authoritative
foundation.

## Core model
Every attack hits a **fixed tile pattern** relative to the attacker, rotated by the attacker's `Direction8`
facing, and damages **whoever is standing on those tiles**. The server already knows every entity's exact tile,
so "which tiles + who's on them" is a cheap, deterministic, cheat-proof server query — no hitboxes. Patterns are
**data** (a list of relative tile offsets per attack), so new attacks are new offset lists, not new code.

## Decisions
- **Telegraphed.** Attacks have a wind-up: the danger tiles light up, then resolve a beat later. A target can
  **step off** the tiles to dodge → positioning is the skill. (Biggest feel lever; chosen.)
- **Two attack types, both telegraphed:**
  - **Melee "shotgun" cone** — hits a small fan in front (e.g. front + front-left + front-right), everyone on
    those tiles. Instant-on-resolve over its tiles.
  - **Archer arrow** — a **travelling projectile**: an entity that moves tile-by-tile along a line and hits the
    **first** target it reaches (or a wall). Telegraphed launch.
- **No friendly fire** to begin with (attacks hit enemies only).
- **Diagonal-facing rotation** — UNDECIDED: rotating a pattern by 45° is awkward. Try it and feel it; may snap
  attacks to 4 cardinals or hand-author diagonal variants.
- **Tile stacking** — deferred. Today movement is ~one entity per walkable tile; rules for when entities may
  share a tile come later (not needed to start).

## Netcode shape (reuse the foundation, carry the NET6 lesson)
- **Combat actions are a SEPARATE stream with their OWN dedup cursor** — never share movement's sequence (the
  NET6 bug was two streams on one cursor stranding each other). This is the #1 rule to get right from day one.
- **Reliable delivery** for the attack command (don't lose an attack) — unlike movement's redundant-unreliable.
  Attacks are low-rate, so reliable retransmit is fine.
- **Server-authoritative resolution**: the server validates (off cooldown, in range/LoS, valid target),
  computes the hit tiles + occupancy, and owns the damage/outcome. Attacks have their **own cooldowns**
  (attack speed / ability CDs), independent of the 150ms move cooldown.
- **Predict the feel, confirm the outcome**: the client predicts the telegraph/animation immediately; the
  server confirms hits/damage.

## Staged plan (small, solid steps — high-risk, so repro/test each)
1. **Character properties** — HP / mana / stamina (current + max) on the entity, server-authoritative,
   replicated to the client, shown in the (just-added) HUD. No damage yet. *This is the foundation + lights up
   the HUD; do it first.*
2. **Attack action + tile-pattern resolution** — a reliable attack message (own cursor), server computes the
   hit tile set (pattern × facing) + occupancy, applies damage to HP. The melee cone first (simplest pattern).
3. **Telegraph timing** — wind-up → resolve; client renders the danger tiles; a target can step off to dodge.
4. **The travelling arrow** — a projectile entity that steps along a line and hits the first target/wall.
5. **Death + respawn** — HP<=0 handling, the respawn flow.

## Open questions to resolve as we go
- Diagonal pattern rotation (decide in stage 2/3 by feel).
- Mana/stamina costs + regen rates (tuning, once abilities exist).
- LoS / walls stopping the arrow + the cone (tile-based, so clean — confirm in stage 2/4).
