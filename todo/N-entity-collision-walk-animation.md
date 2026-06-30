# N — walk animation doesn't stop when blocked by an ENTITY (only walls stop it)

User (player↔monster collision feel-test): "unlike the walls, [entity collision] doesn't stop my walking animation."
Pushing into a monster you're blocked (good) but the player keeps playing the WALK animation; pushing head-on into a
WALL correctly drops to IDLE.

**Mechanism (confirmed):** the player walk/idle anim is driven by RENDER-POSITION delta (per-frame displacement above a
threshold = "walk"), not input — `Visuals/CatoSpriteVisual.cs:10` ("movement detected from the interpolated
render-position delta, exactly like PlayerVisual") + the threshold/hold at ~:55-71. A head-on wall freezes the rendered
position → idle. Pushing into a monster leaves RESIDUAL per-frame render motion → "walk".

**Hypotheses (investigate — measure the actual per-frame render delta when blocked by a STATIONARY monster head-on):**
1. Sliding around the monster's CURVED surface — the circle resolve preserves tangential motion, so unless you push
   exactly head-on you keep translating along the surface (this would be CORRECT walk anim, not a bug — confirm the
   repro is head-on vs angled).
2. Predict/reconcile MICRO-CORRECTION vs the entity: the local player's render = predicted position; vs an entity the
   prediction is approximate (the documented parity gap), so even a "stationary" monster may leave tiny per-tick
   position corrections (monster idle-roam drift, position quantization, resolve not perfectly stable) that exceed the
   moving threshold — a flat wall has perfect parity → zero delta → idle.

**Fix directions (don't break the clean wall→idle case):**
- Drive walk/idle off actual PROGRESS / translation intent vs the held input being satisfied, rather than raw render
  delta; OR
- Add hysteresis / raise the moving threshold so sub-pixel entity-collision jitter doesn't read as walking; OR
- Make the entity-blocked predicted position a clean dead-stop (kill the residual) when fully blocked head-on.

**Repro question for the user:** does it keep walking when you push STRAIGHT into a stationary monster (fully blocked),
or only when angled/sliding around it? Straight-in → a real bug (residual jitter); angled-only → arguably correct.

Relates to [[monster-behavior-architecture]] + the player↔monster collision. Queue AFTER player↔player lands (same
prediction/render area). Polish, not blocking.
