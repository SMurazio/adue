# N — remote velocity-extrapolation refinements (gated on the live feel-test)

From the independent review of the remote-walk velocity+extrapolation feature (v39, `c581922` + `e231359`). The dominant symptom (choppy straight walking) is FIXED and shipped. These are two bounded edge artifacts to address **only if the human feel-test finds them objectionable** — don't pre-optimize.

## 1. Reversal staleness (~1 tile of old-direction glide on a sharp reversal)
The client extrapolates along the last-RECEIVED velocity. The walk integrator (`WorldEntity.ComputeMoveDelta`, ~`:538`) updates `Facing`/`Velocity` SILENTLY and only re-publishes (`StateRevision` bump) on a **tile crossing** (`ApplyResolvedMove`, ~`:521`). So a remote viewer's velocity sample is fresh only as of the last tile crossing (~250ms). On a sharp direction reversal a remote viewer briefly dead-reckons ~1 tile in the OLD direction before the next crossing corrects it. (My `e231359` commit msg wrongly implied a facing change bumps StateRevision — it does so only via `TrySetFacing`/the monster path, NOT the player walk integrator.)
**Fix if needed:** re-publish (bump `StateRevision`, or force-include for one tick) when an entity's velocity **direction/magnitude changes meaningfully**, not just on tile crossing — so a fresh velocity sample reaches viewers promptly on turns. Cost: extra re-sends on direction changes (bounded; not per-tick). Touches the hot integrator → measure, gate, review.

## 2. Stop overshoot at high latency / packet loss (~1 tile, bounded by the 250ms cap)
If a stop confirm is lost or arrives after the playout buffer has entered starvation, the render glides up to the `MaxExtrapolationMs`=250ms cap (~1 tile at walk speed 4 u/s) then snaps/lerps back when the next confirm lands. At LAN latency the 125ms playout delay absorbs it (no artifact); the risk is ~200-300ms+ RTT with a stop right after a tile crossing.
**Fix if needed:** a render-side smoothing/blend on the starvation→confirm transition (mirror the predictor's decaying render-offset) so the snap-back eases instead of pops. Adds a little latency/complexity → only if the feel-test shows it.

## Minor (no action): at admin-set extreme speeds (>128 u/s) the Q-scale-256 velocity clamps (under-reports) — irrelevant at the ~4 u/s walk speed; the clamp is safe (no wrap).
