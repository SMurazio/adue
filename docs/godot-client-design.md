# Godot Client Design

Design + decision record for the production game client, in Godot 4 (C#/.NET). This is the client
the project has been building toward (roadmap Phase 8). It supersedes the browser/Three.js client,
which stays as a throwaway debug surface.

## Why now

The web debug client has hit its feel ceiling: its self-avatar responsiveness is gated by
**no client-side prediction + the WebSocket→LiteNetLib bridge hop**, neither fixable there. The
Godot client removes both: it speaks **LiteNetLib directly (no bridge)** and is where
**local-player prediction** lives (the decided home — see networking-design-plan §2). It is also the
production client; the web client never was.

## Goals

- Reuse the server's contract: reference **`Mmo.Shared`** (protocol, codec, `Direction8`,
  `TileCoord`, messages) and **LiteNetLib** directly — zero protocol drift, no bridge.
- Responsive **and** smooth local movement via client-side prediction + reconciliation.
- Smooth remote movement via interpolation (the ~1.3× cadence buffer lesson from the web client).
- Render the server-authoritative tile world (from `ZoneInfo`) with a Godot TileMap.
- Stay server-authoritative: the client predicts, the server still owns truth.

## Non-goals

- Not the server, not gameplay beyond movement + chat for the first milestones.
- No combat/items/NPC interaction yet (those are server-side, separate).
- Not deleting the web debug client — it remains for quick protocol/debug checks.
- Rendering: **3D scene with a fixed orthographic/isometric camera (2.5D)** — decided. This matches
  the current web client (a 3D scene with an iso camera) and, unlike flat-2D sprites, keeps full 3D
  open (the "don't foreclose possibilities" call). Camera projection (orthographic-iso vs
  perspective) is a switch-anytime detail. Crucially this is a **view-layer decision only**:
  `Mmo.Client.Core` outputs tile/interpolated positions in tile space; the view maps `(x, y)` → 3D
  `(x, 0, y)`. So 2D-vs-2.5D-vs-3D never touches the netcode.

## Tech & project shape

- **Godot 4.x, .NET (C#) build** (not the standard build) — required to reference `Mmo.Shared` +
  LiteNetLib.
- New project (e.g. `src/Mmo.Client.Godot`) referencing `Mmo.Shared`.
- **Recommended seam:** extract the pure-C#, Godot-agnostic client networking + replicated-object
  model into a small library (e.g. `Mmo.Client.Core`) that the Godot client *and* the existing
  console/stress clients can share. Keeps the netcode testable and portable; the console client
  already proves the LiteNetLib client patterns to reuse.

## Architecture — Albion three-layer separation, realized in Godot

(Exactly the separation the architecture doc targets — keep these from bleeding together.)

1. **Network layer** (pure C#, no Godot types): LiteNetLib connection + `ProtocolCodec`
   encode/decode; inbound messages drained on the Godot main loop (poll `NetManager.PollEvents()` in
   `_Process`, or a worker thread that enqueues events the main loop drains — mirror the server's
   `_mainThreadActions` pattern). Mirrors the console/stress client.
2. **Replicated client objects** (pure data, no Godot types): one per entity — server-approved state
   (`networkId`, `kind`, `tile`, `facing`, name) plus *interpolation* state (for remotes) or
   *prediction* state (for the local player). This is the "replicated object" — testable without a
   scene.
3. **View objects** (Godot nodes): a scene per entity (Node2D/Sprite2D), driven by its replicated
   object; can be created/destroyed without touching network/replicated state. The "view object."
4. **Zone view** (Godot 3D, Forward+ renderer): a 3D scene built from `ZoneInfo` (dimensions +
   blocked tiles) — a ground plane/grid + simple wall meshes — under a `Camera3D` with orthographic
   iso projection. Entities are 3D nodes placed at tile→world `(x, 0, y)`, driven by Core's
   interpolated position; remotes interpolated, local predicted (M2). The server stays 2D tile
   coords (no Z); 3D is purely how the client renders them. Genuine elevation/multi-level *gameplay*
   would be a separate server-side Z/layers decision.

## Local-player prediction — RESERVED, gated on measured need (Orchestrator decision)

**Decision (after S16):** prediction is **deferred, not built.** The native Godot client removed the
web client's bridge hop — which was the main cause of the laggy/rubber-band feel — and the human
verified native movement feels correct *without* prediction. So the trigger that
`networking-design-plan.md` §2 and `feature-roadmap.md` require ("local movement *measured* as
unacceptable") is **not met**. This supersedes the earlier "M2 = the payoff" framing here: prediction
is a reserved escalation, only to be built if the native client's confirmed-state movement is later
measured as unacceptable (e.g. over real WAN latency). The no-prediction-until-measured stance wins
the conflict; this doc was over-eager. When/if triggered, the two prerequisites below apply.

### If triggered: local-player prediction and its two prerequisites

Tile-stepped movement makes prediction **unusually easy and reliable**: steps are discrete and
deterministic, so if the client runs the *same* step rule the server runs, its prediction will match
the server almost always (the server only rejects on cooldown/wall — which the client can check
too). So mispredictions are rare and corrections are cheap.

Model: the client applies each `MoveStep` **locally and immediately** (predict), tagging it with the
input sequence the protocol already carries (`MoveStepMessage.Sequence`); it keeps a buffer of
unacknowledged inputs; on an authoritative snapshot it reconciles.

Two small, production-shaped prerequisites (server/shared changes; needed for M2, not M1):

- **Prereq A — shared movement rule.** Extract the tile-step rule (`(tile, direction, grid,
  cooldown-state) → moved tile | rejected`) into a pure function in `Mmo.Shared`, used by **both**
  `WorldEntity.TryStep` (server) and the client predictor. Single source of truth ⇒ no prediction
  drift. The client already has the grid (`ZoneInfo`) and cooldown (`ServerHello`).
- **Prereq B — input-sequence echo.** The server tells each client the **last `MoveStep` sequence it
  processed** (a field on `WorldSnapshot`, or the player's own entity state). The client then drops
  acked inputs and replays the rest from the authoritative tile (classic Gambetta reconciliation).
  Small protocol addition (version bump). *Fallback if deferred:* a coarse "snap to authoritative
  tile if it diverges from prediction" works for tile-stepped movement because mispredicts are rare
  — but the input-seq echo is the correct, clean version; recommend doing it.

Self stays **predicted (instant)**; remotes stay **interpolated** with a small buffer (~1.3× the
tick-quantized cadence — the tuned web-client value).

## Staging (each milestone shippable)

- **M1 — parity, native.** Connect → login → `ServerHello`/`LoginResult`/`ZoneInfo` → render the
  tile map → entity spawn/despawn → render snapshots → **interpolate remotes** → send `MoveStep` →
  chat. **No prediction yet** (local player uses confirmed-tile glide, like the web client). Proves
  the Godot client works end-to-end on the shared protocol with no bridge. Acceptance: two Godot
  clients see each other move and chat against the live server.
- **M2 — the feel payoff.** Land prereqs A + B, then add **local-player prediction + reconciliation**.
  Acceptance: the local avatar responds instantly to input with no rubber-band, while remaining
  server-authoritative (verify a forced mispredict — e.g. stepping into a wall the client didn't
  know about — corrects cleanly).
- **M3 — polish (later).** Camera follow, art pass, entity labels/HUD, selected-entity details. Not
  scoped here.

## Scope fences

- No server gameplay changes beyond prereqs A + B. No combat/items/NPC logic. No multi-zone.
- Keep the network + replicated layers **Godot-agnostic** (pure C#, unit-testable). Rendering stays
  in the view layer only.
- Don't gold-plate art in M1/M2 — placeholder sprites; the point is netcode + feel.

## How this serves production-readiness

- The real client speaks the real protocol with no translation shim — what ships is what's tested.
- Prereq A (shared movement rule) and the replicated/view split are reusable seams (a future client,
  or server-side AI movement, can call the same rule).
- Prediction + reconciliation is the genuinely transferable, production-grade netcode skill — and
  tile-stepped determinism makes it a clean place to learn it correctly.
