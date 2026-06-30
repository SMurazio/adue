# N — gnoll walk jitters left/right (remote extrapolation of a turning entity?)

User: the gnoll's position slightly jitters left/right when it walks. NOT intentional. A straight-walking PLAYER was
feel-confirmed "very smooth" with the same extrapolate-to-now remote render — so the differentiator is likely that the
gnoll CHASES (continuously re-aims at the player), so its replicated velocity DIRECTION changes each snapshot, and the
default zero-lag EXTRAPOLATION (`newest.Position + newest.Velocity × elapsed`, RemotePositionInterpolator) swings the
drawn position perpendicular to motion as the heading updates. Hypothesis — MEASURE before fixing.

**Measure first (the project rule; headless + the live client):**
- Does it jitter on a STRAIGHT walk (gnoll walking directly at a stationary player) or only while TURNING/curving? Turning
  → confirms the extrapolation-of-a-turning-entity hypothesis. Straight-too → look at the per-tick resolved velocity
  (GlideLocomotion `(landing-from)/dt`) for perpendicular wobble, or the interp cadence (monster interp cadence is
  seeded from MoveSpeedMultiplier at spawn and may mismatch the actual glide motion → mis-timed samples).
- Live diagnostic: raise F1 → Movement "Remote interp buffer" — if the jitter smooths, it's the extrapolation.
- Check the FACING too (Direction8 snapping as it turns rotates the model in steps — that's rotation, not position, but
  worth ruling out vs the reported "position" jitter).

**Candidate fixes (prefer latency-free, per the user's no-added-lag principle):**
- Smooth/ease velocity-DIRECTION changes in the extrapolation (blend the heading) so a turning entity doesn't swing —
  latency-free, cosmetic. (NOT render-tween smoothing — that's the known dead end; this is in the extrapolation model.)
- A SMALL remote interp buffer only when an entity is turning (adaptive) — adds minimal lag only mid-turn.
- Confirm the monster interp cadence matches its glide motion (re-seed/align it for gliders).

Relates to [[monster-behavior-architecture]] (P2 glide + the extrapolate-to-now default) and the
remote-extrapolation follow-ups. Netcode → measure/repro + independent review + feel-test. Not blocking; queue after
the monster-collision pass + the tuning-tab work.
