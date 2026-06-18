# S15 — Godot M1a: `Mmo.Client.Core` (headless client netcode + replication)

Severity: should-fix (next major work). **Implementer-actionable now — fully headless.**
Design: `docs/godot-client-design.md` (milestone M1a). Prereq: none.

## Goal

A new pure-C# library `src/Mmo.Client.Core` (net8.0) referencing `Mmo.Shared` and LiteNetLib — the
rendering-agnostic client "brain." **NO Godot dependency.** Do NOT touch `src/Mmo.Client.Godot`
(that's M1b/S16).

## Scope

1. **Connection:** connect with the shared key; send `ClientHello` + `LoginRequest`; handle
   `ServerHello` (capture tickRate, stepCooldownMs, interestRadiusTiles), `LoginResult`, `ZoneInfo`
   (build a tile/zone model). Send `MoveStep`/`ChatSend`; receive `WorldSnapshot`, `EntitySpawn`,
   `EntityDespawn`, `ChatBroadcast`, `ServerError`. Reuse the console/stress client's LiteNetLib
   patterns. Poll-driven (an `Update`/`Poll` the host calls); no threading surprises.
2. **Replicated-entity model:** per-entity server-approved state (networkId, kind, tile, facing,
   name); add on spawn, remove on despawn, update from snapshots (handle full vs incomplete/merge
   snapshots and the snapshot-sequence staleness guard — drop stale/out-of-order). Track the local
   player by characterId.
3. **Interpolation:** clean, unit-testable pure functions producing a per-entity render position
   from confirmed tile updates + a clock. Glide over the TICK-QUANTIZED cadence
   (`ceil(stepCooldownMs/(1000/tickRate))*(1000/tickRate)`); remotes buffer ~1.3× cadence; local
   player uses confirmed-tile glide with NO buffer (NO prediction — that's M2/S17). Avoid the web
   client's bursty-delivery fragility.

## Tests (all headless-verifiable — the point of M1a)

- Unit tests for interpolation (jittery arrival times → continuous, monotonic output, no
  stall/backward-snap).
- Integration tests spinning up a real `GameServer` in-process (see
  `tests/Mmo.Server.Tests/AoiIntegrationTests.cs`) driving `Mmo.Client.Core`: logs in, receives
  `ZoneInfo` + advertised cadence/radius, sees another client spawn+move, sends `MoveStep` and
  observes its own confirmed movement, exchanges chat.

## Fences

No Godot, no prediction (M2), no combat/items, no server changes. Pure C#, unit-testable, no
rendering/UI. Optional only if quick: have the console client consume `Mmo.Client.Core` to prove
reuse.

## Acceptance

- `run-checks.cmd` green with the new unit + integration tests.
- `Mmo.Client.Core` drives a full login→move→chat round trip against an in-process server in tests.
