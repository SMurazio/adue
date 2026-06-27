# N (S-class when started) — Phase B2: client prediction + reconcile of the action (the netcode CRUX)

The high-risk phase. Full rigor: **measure/repro first**, headless determinism test under loss/latency, independent review, green gate, then the human live feel-test. Per `docs/movement-actions-design.md` §2. Builds on B1 (`6870171`, the wire + replicated vertical, server-executed, reviewed SHIP).

## Scope
- **Decide the tick-alignment model FIRST, by measurement — do NOT guess.** `EstimateServerTick()` was DELETED in the continuous migration; attacks send `AuthoredTick: 0` and anchor server-side at receipt. Before resurrecting a tick estimator, write a headless repro that MEASURES the reconcile correction a candidate model produces under simulated latency/loss, and pick the model from the numbers (the project's three netcode misses all came from a fix whose test inherited the wrong model). Candidate A: client runs a local deterministic action instance driving render, let the existing position-reconcile absorb the bounded latency offset (no estimator). Candidate B: resurrect `EstimateServerTick` + authored-tick anchor (consume the `AuthoredTick` B1 already sends/clamps). Measure both.
- Extend `ContinuousPredictor`'s buffer to carry action entries (`{ActionId, ctx, tickInAction}` vs `{dir, dt}`); replay dispatches `Trajectory` for action ticks, the integrator for move ticks (design §2.4).
- Enforce **one-at-a-time on the client** (decline a 2nd local action) so the common spam case never mispredicts (§2.8).
- Server can-act already validates (B1); when B2 consumes `authoredTick`, window-clamp it like the swing path.

## Carry-forwards from the B1 independent review (must address)
1. **Z double-count (subtle, important).** B1 made `EntityVisual` lift EVERY entity (incl. the LOCAL player) by the *replicated* `VerticalOffset`. When B2 predicts the local player's own Z, the local avatar must NOT render predicted-Z **plus** the replicated VerticalOffset on top (double height). B2's reconcile must use the SAME action position/height the executor drives — one Z source for the local player, not two.
2. **Test seam gap.** `ActionIntentHandlerTests` re-implements the handler's decision sequence (`HandleActionIntentCore`) rather than driving the real private `GameServer.HandleActionIntent` — so the live dispatch, the `IsDead`/`IsSuppressedWhileDead` gates, and the tick anchor are inspection-verified only, NOT headless-tested. When B2 moves the anchor from `_serverTick` to an `authoredTick`-derived tick, add a headless test guarding the real anchor (there is none today).

## Related B2/Phase-B todos (fold in)
- [[N-phaseB-keepalive-suppress-active]] — gate the keepalive force-stop on `!IsActive` (a mid-action entity going stale must not get StopMovement'd, fighting the executor).
- N-action-cooldown-prune — prune `_cooldownUntil` on despawn / when serverTick passes.
- Action-END `StateRevision` bump: when an action ends, the entity→rest transition must re-publish the precise end position (mirror the stop-edge `StopMovement` bump) so it self-heals under loss (design §2.4).
