# N — remote velocity-extrapolation refinements (gated on the live feel-test)

From the independent review of the remote-walk velocity+extrapolation feature (v39, `c581922` + `e231359`). The dominant symptom (choppy straight walking) is FIXED and shipped. These are two bounded edge artifacts to address **only if the human feel-test finds them objectionable** — don't pre-optimize.

## ~~1. Reversal staleness~~ — RESOLVED by `5290233` (which postdates this note)
The premise ("a velocity sample is fresh only as of the last tile crossing") died when `5290233` shipped the
per-tick force-include of MOVING entities (`forceMoving = Velocity.LengthSquared > 0` → re-sent every tick while
moving, `GameServer.cs:~1109`). A turning/reversing entity now replicates a fresh resolved velocity every snapshot
(~50ms), so the ~1-tile old-direction glide can't happen. No action; kept for the record because the fix this item
proposed is effectively what shipped (stronger: per-tick, not per-change).

## 2. Stop overshoot at high latency / packet loss (~1 tile, bounded by the 250ms cap)
If a stop confirm is lost or arrives after the playout buffer has entered starvation, the render glides up to the `MaxExtrapolationMs`=250ms cap (~1 tile at walk speed 4 u/s) then snaps/lerps back when the next confirm lands. At LAN latency the 125ms playout delay absorbs it (no artifact); the risk is ~200-300ms+ RTT with a stop right after a tile crossing.
**Fix if needed:** a render-side smoothing/blend on the starvation→confirm transition (mirror the predictor's decaying render-offset) so the snap-back eases instead of pops. Adds a little latency/complexity → only if the feel-test shows it.

## Minor (no action): at admin-set extreme speeds (>128 u/s) the Q-scale-256 velocity clamps (under-reports) — irrelevant at the ~4 u/s walk speed; the clamp is safe (no wrap).

## 3. (from the correction-smoothing review, F3 — pre-existing) delay>0 knob users still get the un-smoothed pop
The re-base correction smoothing covers only the default delay-0 starvation (extrapolate-to-now) regime. With a
POSITIVE F1 "Remote interp buffer" the steady regime is bracket-lerp, and the starvation→bracket transition (a
late sample finally landing) still steps in one frame — exactly the pre-fix behavior. Fine while the knob is a
diagnostic; extend the capture across that transition if the buffer is ever recommended as a real setting.
