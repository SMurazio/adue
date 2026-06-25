# Phase 3 — Protocol-Major Wire Break (implementation spec)

Part of the continuous migration. Base: Phases 0–2 (server moves players continuously + collides; wire still tile v35;
client tile-renders). Phase 3 = the wire carries CONTINUOUS positions + the continuous per-input MoveIntent; dead
commit/mode types deleted; **v35 → v36 (mutually undecodable — atomic lockstep cutover).**

## Decisions
- **Position encoding: fixed-point Q12.4, signed 16-bit per axis (NOT float32).** `qx = round(X*16)`; decode `qx/16`.
  1/16-tile (0.0625 u) precision; **keeps the snapshot position at 4 bytes/entity (zero inflation vs today)** — float32
  would be +33% on the hot record. Shared `PositionEncoding.Encode/Decode` (`FixedPointShift=4`), round-away-from-zero.
  **Quantize ON SEND ONLY** — the server's authoritative `Position` stays full-precision `double`; never round the sim
  to the grid (that would break determinism vs the predictor). The Phase-4 reconcile error budget must be ≥ the 1/16-u
  step so quantization alone can never trigger a correction.
- **`EntityStateSnapshot.Tile` → `WorldVector Position`.** **NO velocity field in Phase 3** (defer to Phase 5 — adding it
  now inflates the per-entity record before any consumer exists).
- **MoveIntent reshaped to per-input `{uint InputSeq, float DirX, float DirY, float DtSeconds}`** (analog of
  `exp:ContinuousInput`; reuse `MessageType.MoveIntent=3`). **Delete `MoveInputMessage` (NET1 redundant window)** — the
  per-frame model is self-redundant. **Server integrates per-input-by-dt on the receive path** (the experiment model):
  if `InputSeq > LastInputSeq`, `IntegrateMovement(rawDir, DtSeconds, zone, radius)` + `LastInputSeq = InputSeq`. **Retire
  the fixed-tick `IntegrateHeldMovementIntents` for players** (integration is 100% input-driven); move keepalive/stop/
  dead/swing-root-freeze guards onto the per-input path (a rooted player's input ACKs but doesn't move; `(0,0)` = stop).
  **Add `LastInputSeq` to the snapshot header** (recipient-scoped, like `RecipientStepSeq` rides today).
- **Boundary (A) — the crux:** Phase 3 ships the FINAL continuous-input wire + the server per-input integration; the
  Phase-3 CLIENT sends one `MoveIntent{seq,dir,dt}` per frame but does NOT predict (renders raw decoded position —
  crude/laggy, expected). **Phase 4 adds ONLY the client predictor** (additive, zero wire/server change). The R4 Δt
  convergence is established HERE (server integrates by the client's `dt`).
- **Delete dead types:** `StepCommitRequest/BatchMessage`, `StepCommitWindowEntry`, `MovementModeMessage`,
  `MoveInputMessage`, `MoveInputWindowEntry`; their `MessageType` members (leave numeric GAPS — don't renumber
  survivors). Dead `ClientSession` members (`ClientDrivenMovement`/`SetClientDrivenMovement`/`TryConsumeCommitSequence`/
  `LastCommitSeq`/`_lastCommitSeq` — Phase-1 followup #2). Client send machinery (move-input ring, commit/mode senders).
- **Keep `LoginResult`/`EntitySpawn`/`SpawnerMarker` as `TileCoord`** (genuine tiles/anchors) — only the HOT per-entity
  snapshot position goes continuous. Minimizes the break surface. Client `EntityState` keeps `Tile => Position.ToTileRounded()`
  so `HarvestTargeting`/`LocalTile` keep working.
- **Crude Phase-3 client:** decode `Position` (continuous) + `LastInputSeq` (store, unused till Phase 4); render raw via
  `RenderPosition.FromWorld`; leave `LocalPlayerPredictor`/`TileInterpolator`/`MonsterHopInterpolator` compiled-but-UNWIRED
  (Phase 4/5 replace them). Send per-frame `MoveIntent{++seq, rawDir, frameDt}`.
- **Version v36.** ALL in-repo senders flip atomically: `MmoClient`, web bridge, console, stress, synthetic. `ServerHello.
  ProtocolVersion` gives a clean mismatch error. Tag last-v35; coordinate the collaborator cutover.

## Sub-commits
1. `feat(shared): PositionEncoding fixed-point helpers + tests` — additive, green on v35.
2. `refactor(shared): EntityStateSnapshot.Tile → WorldVector Position` — retype the struct; server passes `entity.Position`,
   client `EntityState` holds `WorldVector` + derives `Tile`. **Codec STILL writes tiles (`ToTileRounded`)** so the WIRE is
   unchanged — green on v35. (Pass A = commits 1–2: the additive internal-type foundation.)
3+4 (merged, THE atomic break): reshape `MoveIntent`; delete dead types; `Version=36`; fixed-point positions +
   `LastInputSeq` in the codec; server per-input integrate-and-ack + **`DtSeconds` clamp**; all clients send per-frame
   MoveIntent + decode continuous + render raw; fold in the codec/server/client test rewrites. **The single revert point.**
5. `refactor: delete client commit/mode/move-input machinery + dead tests`.
6. `docs: Phase 3 progress + protocol.md v36`.

## Risks
- **R-dt anti-speedhack (NEW):** the client now sends `DtSeconds`; a hostile client could send a huge `dt` to teleport.
  **Server MUST clamp `DtSeconds`** (e.g. `[0, k/TickRate]` small k, or wall-clock-accumulate so a client can't integrate
  more sim-time than real time elapsed) before integrating. Design + test it (the experiment trusted dt — one local client;
  the real server cannot).
- **R-determinism of the encoded position:** wire is a lossy 1/16-u projection of the double sim. Quantize on send only;
  reconcile budget ≥ 1/16 u; one shared encoder; byte-precision test.
- **R-bandwidth:** fixed-point = 4 bytes/entity (zero inflation). `LastInputSeq` is +4 bytes/snapshot (header, negligible).
  No per-entity velocity in Phase 3. Phase-12 study starts from parity.
- **R-cutover:** v35/v36 undecodable — land server + all in-repo clients atomically; tag last-v35; notify the collaborator.
- **R-crude-client-feel:** raw render is laggy/jerky under latency — EXPECTED (Phase 4/5 refine); don't "fix" it here.
