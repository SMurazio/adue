# S98 — Remove turn-then-move / `turnDelayMs` (direction changes step immediately)

Severity: S (movement behavior — user decision: the turn-delay is outdated). Server + protocol + client. One
cohesive, revertable change. Protocol version BUMP (ServerHello loses a field) — server+client ship together.

## Why

Turn-then-move (S59/S63) makes a facing change a separate, delayed action: pressing a direction you're not
facing first TURNS in place (no tile move) and frees the next action after `turnDelayMs` (default 80ms/2 ticks)
before stepping. The user wants it gone: it adds ~a turn-beat of latency to every direction change, was the
root of the spam-direction-change turn-vs-step skew, and no longer fits the cosmetic-sprite/responsiveness
direction. After this change a direction change **steps immediately** in the new direction (facing updates with
the step); there is no turn beat and no `turnDelayMs` anywhere.

## New server step behavior (`src/Mmo.Server/Runtime/WorldEntity.cs`, `TryStep`)

Remove the turn-then-move branch. On each eligible tick while the held intent is moving:
1. **Set `Facing = direction`** (always — the step itself faces you; no separate turn action).
2. Resolve the step exactly as today: `target = tile + direction.Delta()`; if `IsStepWalkable` → MOVE
   (advance tile, `StepSequence++`, `_nextEligibleTick = tick + stepCooldownTicks`); if blocked → HOLD at the
   wall (`_nextEligibleTick = tick + 1`, do NOT consume the cooldown), facing already updated.
3. Delete the `if (direction != Facing)` turn branch and all use of `turnDelayTicks`.

**Replication detail (important):** today the turn action bumps `StateRevision` so a facing-only change
replicates. After removal, a facing change with NO tile move (e.g. pressing into a wall, or changing direction
on the same tick you're blocked) must STILL bump `StateRevision` so the new facing reaches clients (the Cato
sprite flip depends on facing). Ensure a facing change replicates even when the tile doesn't move. Verify the
snapshot/`StateRevision` path.

## Remove `turnDelayMs` everywhere (grep `turnDelay`/`TurnDelay` to catch all)

- **Server config** `src/Mmo.Server/Configuration/ServerOptions.cs`: remove `TurnDelayMs` (:39), `TurnDelayTicks`
  (:57), the `MMO_TURN_DELAY_MS` env read (:82), and its validation (:126).
- **Server runtime** `GameServer.cs`: drop `turnDelayTicks` from the `TryStep` call (~:1566), the `ServerHello`
  construction (~:225), and any `ServerTuning`/`AdminSetTuning` handling of `move.turnDelayMs` (the live F4
  knob — server side).
- **Protocol (version BUMP)** `src/Mmo.Shared/Protocol/Messages.cs` + `ProtocolCodec.cs`: remove `TurnDelayMs`
  from `ServerHelloMessage` and its codec write/read; bump `ProtocolCodec.Version`. Update `docs/protocol.md`
  (version + message list) in THIS unit of work (guardrail). Round-trip test updated.
- **Client** `src/Mmo.Client.Core/MmoClient.cs`: remove `ServerInfo.TurnDelayMs`, `ResolveTurnDelay`,
  `SetPredictorTurnDelay`, and the `turnDelayMs` arg threaded into `AttachPredictor`.
- **Predictor** `src/Mmo.Client.Core/LocalPlayerPredictor.cs`: remove `_turnDelayMs`/`_turnDelayTicks`,
  `SetTurnDelay`, the `turnDelayMs` ctor param, the turn-delay half of `RecomputeTickCounts`, AND the
  turn-then-move branch in `Tick` (mirror the server: set facing on the step, no separate turn action) plus any
  turn handling in `Reconcile`/`AdvanceOneStep`. Model A must stay in lockstep parity with the new server rule.
- **Cosmetic driver** `src/Mmo.Client.Core/LocalPlayerCosmetic.cs`: remove the now-pointless `SetTurnDelay`
  no-op and its call sites.
- **F4 tuning panel** (client, `MmoClientRoot`): remove the `move.turnDelayMs` field and its apply/seed wiring.

## Tests (the gate)

- **Rewrite the turn-parity tests** in `LocalPlayerPredictorTests.cs` (`TurnPathParity_AgainstRealWorldEntity`,
  `OffGridTurnParity_AgainstRealWorldEntity_Sweep`, and any turn-delay-specific test) to the NEW model: a
  direction change steps immediately (facing set on the step), no turn beat; predictor `PredictedTile`/
  `PredictedStepSeq` stay in lockstep with the real `WorldEntity` across direction changes. Remove tests that
  asserted the turn-delay beat specifically.
- **Server** `WorldEntity`/movement tests: update any asserting turn-then-move; add/adjust one asserting a
  direction change steps immediately and that a blocked direction-change still updates+replicates facing.
- **Protocol** `ProtocolCodecTests`: ServerHello round-trip without `TurnDelayMs`; version bump reflected.
- **Client.Core** reconcile/cosmetic suites stay green.
- `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` green before/after; build the Godot project
  (`godot-build.cmd`) clean (the F4 field + predictor ctor signature changes must compile there). Fresh
  standard stress gate **120 clients / 30s** (movement-rule change — confirm no throughput/behavior regression).

## Constraints

- Server + protocol + client. Protocol version bump is BREAKING — server and client must be rebuilt together;
  note it in the review-request. No new movement features beyond removing turn-then-move.
- **Safe Local Execution** binds you (scripts only; stop a `Mmo.Shared.dll`-locking session via `stop-mmo.cmd`
  and note it). You cannot run Godot — the Orchestrator runs the live check (direction changes step immediately;
  no turn hitch; sprite faces/flip correct including pressing into a wall).
- This is a LARGE surface — work methodically, `grep` every `turnDelay`/`TurnDelay`/`TurnDelayTicks` reference,
  and if a genuine fork appears (e.g. the protocol bump interacts with something unexpected) STOP and surface it.
- Do NOT commit, push, or delete the task file — leave the tree dirty + write
  `review/review-request-s98-remove-turn-then-move.md`; the Orchestrator verifies and commits.

## Acceptance

- Turn-then-move is gone: a direction change steps immediately in the new direction (facing set on the step),
  no turn beat; `turnDelayMs` removed from server config, protocol (version bumped), client, predictor, and the
  F4 panel. Predictor stays in lockstep parity with the new server rule. Facing-only changes still replicate.
  run-checks green, Godot build clean, 120/30s stress clean. Review-request written.
