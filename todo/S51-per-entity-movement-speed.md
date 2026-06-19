# S51 — Per-entity movement speed (varying step cadence)

Severity: feature (gameplay foundation). Today every entity moves at one global cadence
(`ServerOptions.StepCooldownTicks`, ~140 ms). This makes step speed **per-entity**, so speed buffs /
mounts / slows become "change this entity's cadence." Fits the tile-stepped, server-authoritative,
**no-prediction** model exactly: the server still paces steps and is authoritative; the client just
tweens at the entity's advertised cadence. **v1 = the mechanic + a dev command to exercise it.**
Item/buff-driven triggers (a mount item, a speed potion) are a SEPARATE follow-up (needs item-use, which
doesn't exist yet) — do NOT build them here.

## Design (Orchestrator decision)

1. **Server — per-entity effective cadence.** Give `WorldEntity` a movement-speed stat — a
   `SpeedMultiplier` (double, default 1.0; >1 = faster) — and derive an **effective step cooldown** from
   the base (`StepCooldownTicks` / `StepCooldownMs`) ÷ multiplier (clamp to a sane floor, e.g. ≥1 tick /
   ≥ the config min). The per-tick stepping loop (the "intent is Moving && cooldown elapsed → step one
   tile" path) must use the **entity's** effective cooldown, not the global. Default multiplier 1.0 keeps
   every existing entity identical to today.
2. **Protocol (version bump from v15).**
   - `EntitySpawn` gains the entity's **effective step cooldown in ms** (`uint16`) so a viewer knows the
     cadence the moment it sees the entity.
   - New reliable **`MovementSpeedChanged`** message (`networkId`, `stepCooldownMs`) sent to a viewer's
     AOI when an entity's effective cadence changes mid-session (buff applied/removed). Reliable-ordered
     like spawn/despawn. (Keeps speed off the hot snapshot path.)
3. **Client — per-entity tween cadence.** The client currently tweens at the single `ServerHello`
   cooldown. Store a **per-entity cadence** (from `EntitySpawn` / `MovementSpeedChanged`); the
   `TileInterpolator` for that entity tweens at its own cadence. Fall back to the `ServerHello` global
   when an entity carries no explicit value (back-comp / safety). Still **no prediction** — confirmed-step
   tweening, just at the right speed.
4. **Dev command to exercise it (v1 deliverable):** a slash command — `/speed <multiplier>` —
   **admin-gated** (same gating as existing dev commands), sets the **caller's own** entity
   `SpeedMultiplier` live (server recomputes the cooldown, emits `MovementSpeedChanged` to AOI). Lets the
   human see varying speed end-to-end. (Reset with `/speed 1`.)

## Files
- `src/Mmo.Shared/` — `EntitySpawn` field + new `MovementSpeedChanged` message + codec + version bump.
- `src/Mmo.Server/Runtime/` — `WorldEntity` speed stat; per-entity cooldown in the step loop; emit
  `MovementSpeedChanged` on change; the `/speed` command (admin-gated).
- `src/Mmo.Client.Core/` — per-entity cadence store; `TileInterpolator` uses it; handle the new message.
- `src/Mmo.Client.Godot/` — only if needed to wire the per-entity cadence through (keep logic in Core).
- `docs/protocol.md` — document the new field + message + version (keep in sync).

## Tests
- Server: an entity with multiplier 2.0 steps about **twice as often** as a default entity over a fixed
  window (and a slow multiplier steps less often); cadence respects the floor clamp.
- Codec round-trips: `EntitySpawn` with cooldown; `MovementSpeedChanged`; version is the new value.
- `/speed` changes the caller's cadence and emits `MovementSpeedChanged` to an AOI viewer (integration).
- Default (no `/speed`) behaviour is byte-for-byte unchanged for normal play; existing movement/AOI/
  snapshot tests pass.

## Acceptance
- With `/speed 2` the player visibly moves faster and the client tween **matches** the server cadence (no
  starvation/overrun); `/speed 1` restores normal; other entities unaffected. Headless tests cover the
  server cadence + protocol; the **feel** is a human check on relaunch.
- `run-checks.cmd` + `godot-build.cmd` green; protocol version bumped + `docs/protocol.md` updated. Do NOT
  commit — Orchestrator reviews.

## Notes / guardrails
- Still server-authoritative, tile-stepped, **no prediction** — this only varies the cadence.
- Keep it allocation-light on the step path. Clamp the effective cooldown to the configured min/max so a
  silly multiplier can't break the tick loop.
- Out of scope (separate follow-up): item/buff-driven speed (mount, potion) — needs item-use first.
