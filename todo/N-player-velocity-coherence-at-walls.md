# N — player replicated velocity is the DESIRED (pre-collision) one, not the resolved one

Surfaced while fixing GlideLocomotion (P2). The PLAYER integrator (`Zone.IntegrateMovement` → `WorldEntity.ComputeMoveDelta`
sets `Velocity = dir × speed`, then the swept-circle resolve + `ApplyResolvedMove`) replicates the DESIRED velocity, not
the velocity of the ACTUAL resolved motion. So a player SLIDING along a wall replicates a velocity pointing INTO the
wall, and remote viewers — who now EXTRAPOLATE along the replicated velocity by default (zero-lag remote render) — drift
into the wall and correct each tick (~one sample-interval × speed ≈ 0.2 tile of shimmer while wall-following).

P2 fixed this for the glider: after resolving, set `Velocity = (landing − from) / dt` (the velocity-coherence guardrail;
`WorldEntity.SetVelocity`). The PLAYER path has the same latent pattern but was NOT touched (pre-existing; the user
feel-tested open walking as "very smooth"; the artifact only shows while sliding along a wall on a REMOTE screen).

**Fix:** apply the same resolved-velocity rule in `Zone.IntegrateMovement` — after `ApplyResolvedMove(collided)`, set the
entity's replicated `Velocity` to `(collided − from) / dt` instead of leaving the pre-collision `dir × speed`. This makes
remote extrapolation of a wall-sliding player coherent. Caution: the player's OWN client predicts locally (doesn't read
this replicated velocity for itself), so this only affects how OTHER clients render it — verify no reconcile interaction.
Low priority (cosmetic, remote-only, sliding-only). Gate + the existing movement tests + a remote-view feel-test.
Builds on [[tile-continuous-cleanup]] / the extrapolate-to-now default.
