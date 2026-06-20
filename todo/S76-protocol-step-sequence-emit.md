# S76 — Protocol v19 + server emit per-entity step-sequence (client decodes, ignores)

Stage 1 of 3 of the per-step-ack / server-reconciliation plan
(`C:\Users\stefano\.claude-mmo\plans\sleepy-tickling-lake.md`). **Goal: NO gameplay change.** Just put the
step-sequence on the wire so Stage 2 (S77) can use it. Server + client ship together (the version bump is
breaking). Touches server + shared protocol + a thin client decode.

## Why
A bare confirmed tile is ambiguous — the client can't tell which predicted step a snapshot confirm matches,
which is the reconcile rubberband's root. The fix is a per-entity step-sequence the client can match against.
This stage only emits it; S77 makes the client reconcile against it.

## What
1. **`src/Mmo.Server/Runtime/WorldEntity.cs`** — add `public uint StepSequence { get; private set; }`
   (init 0). Increment it **only on the accepted-step branch** (where `Tile = target` /
   `_nextEligibleTick = serverTick + stepCooldownTicks`). Do **NOT** increment on the turn branch or the
   blocked branch. It counts tile moves only (exactly what the predictor's `_predictedTile` advances on).
   Do NOT reuse `StateRevision` (that also bumps on turns).
2. **Protocol bump** — `src/Mmo.Shared/Protocol/ProtocolCodec.cs`: `Version` 18 → 19. Update the version-assert
   test (there is a `ProtocolVersionIs*` test — rename/retarget to 19).
3. **`src/Mmo.Shared/Protocol/Messages.cs`** — add `uint RecipientStepSeq` to `WorldSnapshotMessage`
   (a single per-snapshot header field, scoped to the recipient's OWN entity — NOT a per-entity field on
   `EntityStateSnapshot`). Default it to 0 in the convenience constructors.
4. **`src/Mmo.Shared/Protocol/ProtocolCodec.cs`** — write/read `RecipientStepSeq` in the world-snapshot
   payload (the `WriteWorldSnapshotPayload`/`ReadWorldSnapshot` pair and the `EncodeWorldSnapshot` helper +
   whatever `SnapshotEncodeBuffer`/encode-buffer wrapper the server uses). Pick a fixed position (e.g. right
   after `SnapshotSequence`) and mirror it exactly in the reader.
5. **`src/Mmo.Server/Runtime/GameServer.cs`** — populate `RecipientStepSeq` from the recipient session's own
   `WorldEntity.StepSequence` at snapshot-build time, in **BOTH** the real-delta snapshot path AND the
   empty/keep-alive snapshot path. CRITICAL: it must ride the header even when the local entity is delta'd out
   of the payload (idle player) — it is recipient-scoped metadata, not entity payload. The recipient entity is
   already resolved during per-session snapshot build (the AOI/`EntityId` path); read its `StepSequence` once.
6. **`src/Mmo.Client.Core/MmoClient.cs`** — decode the field (it arrives via the codec). The client may stash
   it but the predictor's reconcile is UNCHANGED this stage (still the old `Reconcile(tile, now)`). No
   behavior change.

## Tests
- `ProtocolCodecTests` (or the snapshot round-trip test): round-trip `WorldSnapshotMessage` with a non-zero
  `RecipientStepSeq` and assert it survives encode→decode, including a chunked snapshot and an empty/keep-alive
  snapshot (the field must be present on both).
- A server-side `WorldEntity` test: `StepSequence` increments by 1 per accepted step, and does NOT change on a
  turn-only action or a blocked step.
- The `ProtocolVersionIs*` assert updated to 19.
- All existing tests stay green (no gameplay change).

## Constraints
- Server + shared + thin client decode only; NO predictor/reconcile behavior change this stage. The server is
  stopped (dev mode) so the build won't DLL-lock. Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd`
  before/after (try it; if Bash denied, note + continue — Orchestrator runs the authoritative gate). You can't
  run Godot. **Safe Local Execution** binds you. Do NOT commit, delete the task file, or push.

## Acceptance
- `run-checks` green incl. the round-trip + step-seq-increment + v19 tests; `RecipientStepSeq` rides every
  snapshot to a client (real-delta AND keep-alive); `StepSequence` increments on steps only; zero gameplay
  change. Review-request → `review/review-request-s76-step-seq-emit.md`. Do NOT commit or delete the task file.
