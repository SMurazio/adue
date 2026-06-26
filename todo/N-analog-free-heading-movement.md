# N — analog / free-heading movement (8-way wire → raw analog)

**Priority:** N (feel improvement / feature — NOT a bug; movement already feels good). Surfaced by the movement-options
audit comparing `feat/continuous-migration` to the good-feeling `exp/continuous-movement`.

**The gap.** The migration sends movement as a `Direction8` on the wire (`MoveIntent` → `Direction8.ToUnitVector()` →
8 fixed unit headings). The experiment sent a **raw analog** unit vector. For WASD/keyboard input this is largely
moot — keyboard input IS 8-way (4 keys → 8 combinations), so `Direction8` and a normalized raw vector are the same 8
headings. It only matters for an **analog input source** (mouse-to-move / gamepad stick / click-to-point heading),
where the player could move at any angle, not just the 8 cardinals+diagonals.

**So this is a FEATURE, not a fix:** enabling free-heading movement for analog input. Scope (when wanted):
- Wire: extend `MoveIntent` to carry an analog heading (a quantized angle, or a fixed-point unit vector) instead of /
  alongside `Direction8` — a protocol bump (v37 → v38). Keep `Direction8` for facing/animation.
- Server: integrate the analog unit vector × speed × dt (the integrator is already continuous — `ComputeMoveDelta`
  takes a `unitDir`, so it already accepts an arbitrary unit vector; the only change is feeding it the analog heading
  rather than `Direction8.ToUnitVector()`).
- Client predictor: predict with the same analog heading (it already takes `inputX/inputY` — feed the raw vector).
- Input: add an analog source (mouse-heading / stick); WASD stays 8-way by nature.

High-rigor (movement netcode + protocol) when implemented — measure, faithful predict/server parity, independent
review, and don't destabilize the now-good tile-gated-reconcile-fixed movement ([[continuous-movement-experiment-viable]]).
