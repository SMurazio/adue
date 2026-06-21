# SPEED1 — pin the base step cooldown to a constant 150ms (3 ticks) + remove the F4 stepCooldownMs knob

PRODUCTION on `main`. **PRIORITY S.** User directive (2026-06-21): the global base move cooldown becomes a
constant **150ms** (a clean 3 ticks at 20Hz; the old 140ms already quantized to 3 ticks, so the EFFECTIVE walk
speed is unchanged), and the F4 "move.stepCooldownMs" live knob is removed. Per-entity speed (the S106 `/speed`
multiplier) stays — it now scales off the constant base.

## What to do
1. **Server:** set the base step cooldown default to **150ms** (3 ticks) and pin it as a constant — remove the
   runtime-tunability of the BASE (or, minimally, stop exposing/accepting changes to it). The per-entity
   `SpeedMultiplier` path is UNCHANGED (entities still scale off the base).
2. **Client:** remove the **"move.stepCooldownMs"** field from the F4 "Admin Server Tuning" panel and its
   apply/send path. Keep `aoi.interestRadius` (and any other F4 field).
3. **S106:** with a constant base, the dropdown derives its labels off a base that never changes mid-session,
   which **closes the S106b stale-label issue** — delete `todo/S106b-...md` in this commit if fully resolved.
   Confirm the dropdown still preselects the 1.0x walk (now exactly 3 ticks / 150ms).
4. **Keep the per-entity `SpeedMultiplier` mechanism** — it's needed for creatures/NPCs and combat status
   effects (snare/haste/root) later. Do not remove it.

## Where to look
- `src/Mmo.Server/Runtime/ServerTuning.cs` / `ServerOptions.cs` — the `StepCooldownMs`/`StepCooldownTicks`
  default + tunability.
- `src/Mmo.Server/Runtime/GameServer.cs` — the AdminServerTuning apply path for `stepCooldownMs`; `ServerHello`
  advertises `StepCooldownMs`.
- `src/Mmo.Client.Godot/MmoClientRoot.cs` — the F4 panel `stepCooldownMs` field + its apply.
- Tests: any that tune `stepCooldownMs` via the F4/admin path — update to the constant or per-entity speed.

## Out of scope
- Do NOT remove the per-entity `SpeedMultiplier` / `/speed` path or S106. No change to the movement netcode.

## Gates
`run-checks.cmd` + `godot-build.cmd` green. One discrete revertable commit referencing this task; delete this
file (and `todo/S106b` if resolved) in that commit. Safe Local Execution; you cannot run Godot — the human
verifies the F4 field is gone and walk speed is unchanged.

## Acceptance
The base move cooldown is a constant 150ms (3 ticks); the F4 `stepCooldownMs` knob is gone; per-entity `/speed`
(S106) still works off the constant; walk speed is visually unchanged. Gates green.
