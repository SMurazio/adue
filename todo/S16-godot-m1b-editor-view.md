# S16 — Godot M1b: isometric 3D view (renders `Mmo.Client.Core`)

Severity: should-fix (the playable client). Design: `docs/godot-client-design.md` (M1b).
**Prerequisite: S15** (`Mmo.Client.Core`).

## Status: needs the Godot editor — NOT fully headless-implementer-actionable

The Godot scene/view work requires the Godot 4.x editor (a human at the keyboard). A headless agent
can author the C# view scripts but cannot create/verify scenes. So: the implementer may draft the
view scripts that consume `Mmo.Client.Core`, but **wiring the scene tree and visual verification is
human-driven** in the editor. Automatable checks use the skill scripts:
`godot-build.cmd` (compile) and `godot-run.cmd` (headless run + captured output).

## Goal

In `src/Mmo.Client.Godot` (Godot 4.6 .NET, Forward+/3D): a `Node3D` scene with an orthographic
isometric `Camera3D`, a ground/grid + wall meshes built from `ZoneInfo`, and one 3D node per entity
positioned at tile→world `(x, 0, y)`, driven by `Mmo.Client.Core`'s interpolated position. Reference
`Mmo.Client.Core` + `Mmo.Shared`. Remotes interpolated, local confirmed-tile glide (no prediction —
that's M2). Connect/login/move/chat end to end against the live server.

This is a **view layer only**: rendering reads from Core; no netcode lives here. (See the
Albion 3-layer separation in the design doc.)

## Acceptance

- `godot-build.cmd` green; `godot-run.cmd` starts the client headless without crashing.
- Manual (human): two Godot clients connect to the live server, render the map + each other, move
  and chat. Movement glides smoothly (remotes interpolated).
