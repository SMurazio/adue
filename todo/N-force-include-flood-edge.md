# N — sub-tile own-entity force-include: benign flood-only re-send at zero distance

**Priority:** N (benign; flood-only — NOT honest play). From the independent review of `1133c7e`.

The sub-tile reconcile fix force-includes the recipient's own entity in its own snapshot while
`Velocity.LengthSquared > 0d`. Edge: a flood-throttled player that has DRAINED its anti-speedhack dt-budget but is
still HOLDING a direction hits `HandleMoveIntent`'s budget-exhausted branch (`GameServer.cs:~2765`), which calls
`ComputeMoveDelta(unitDir, 0d)` — that sets `Velocity` NON-ZERO (from `unitDir`) but advances **zero** distance
(dt=0). So the own entity is force-included every tick re-sending its UNCHANGED position.

**Benign:** only a budget-drained/misbehaving peer pays it (a few bytes/tick), and the position is unchanged so the
client reconciles against a CORRECT base — no rubberband, no correctness bug. But it contradicts the fix's "nothing at
rest" intent. **Fix when convenient:** gate the force-include on actual position change (or on the integrate having a
`dt > 0`), so a zero-distance Velocity doesn't trigger a re-send.
