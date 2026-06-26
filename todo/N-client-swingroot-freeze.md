# N — client predictor doesn't mirror the swing-root (latent; inert at default root=0)

**Priority:** N (latent — NOT the live combat rubberband; that was re-localized to slime-proximity, see the
entity-collision investigation).

**The gap.** The Phase-4 `ContinuousPredictor` has NO swing-root (`MmoClient.cs` ~535 comment: the tile-predictor's
`ApplyAttackMovementRootAt` mirror was deleted and never re-added). When `combat.rootMs` (`ServerTuning.AttackRootMs`)
is **> 0**, the server freezes the attacker's movement for `AttackRootTicks` (`WorldEntity.IsMovementFrozen`), but the
client keeps predicting movement from the held input → a held-move-through-a-swing mispredicts → reconcile yanks back.

**Why it's only latent right now.** `CombatTuning.MovementRootMs = 0` by default (`CombatTuning.cs:15`) and the user
confirmed they never raised the F1 Combat "swing root (ms)" knob — so the server never roots, the client never needs to
freeze, and there is no rubberband from this. It only bites if someone raises `combat.rootMs`.

**The fix (when wanted).** Freeze the client's movement PREDICTION for `RootMs` after `SendAttack`, gated on `RootMs>0`:
track `_movementFrozenUntil = _currentTime + RootMs` (RootMs = replicated `CombatTuning.RootMs`); in `PredictAndSendMove`
force the predicted+sent dir to (0,0) while frozen (the seq still buffers/sends; only motion is suppressed) — mirroring
the server, which ACKs the seq but integrates zero motion under `IsMovementFrozen`. Duration parity is exact; the ~RTT/2
send-vs-receive edge is reconcile-absorbed.

**Status:** an implementer drafted exactly this (MmoClient `_movementFrozenUntil` + `PredictAndSendMove` zero-dir +
a `ContinuousReconcileHarnessTests` parity test), but the parity test **FAILED** (the WITH-freeze correction wasn't
bounded as asserted — a timing bug in the freeze window vs the harness's server root, likely the Godot predict-before-Poll
stale `_currentTime` the author flagged, or the send-vs-receive offset). Reverted (kept the tree green). Re-do with the
test timing corrected when this knob actually gets used.
