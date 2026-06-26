# N — prune ServerActionExecutor._cooldownUntil (minor; Phase B/C)

**Priority:** N (minor). From the Phase-A independent review of `bcc7ca2`.

`ServerActionExecutor._cooldownUntil` (`Dictionary<(entityId, actionId), uint>`, ~`ServerActionExecutor.cs:236`) is never pruned: entries accumulate per (entity, action) and are NOT removed on entity despawn (the orphan path at ~`:212` only clears `_active`, not the cooldown map). Slow unbounded growth over a long-lived server, and a REUSED entity id could inherit a stale cooldown from a previous occupant.

**Fix:** prune a `(entityId, *)` entry on despawn, and/or lazily drop entries once `serverTick` passes their value. Cheap; do it alongside the Phase B/C trigger wiring.
