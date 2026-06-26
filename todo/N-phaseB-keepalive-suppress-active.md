# N — Phase B: gate the keepalive force-stop on !IsActive (IMPORTANT before the first live action trigger)

**Priority:** N now (harmless — `_active` is always empty in Phase A), but **do this IN Phase B** before any action trigger source exists. From the Phase-A independent review of `bcc7ca2`.

`GameServer.CreditMoveDtBudgetsAndKeepalive` (~`GameServer.cs:2865`) calls `entity.StopMovement()` on a session whose MoveIntents went stale (the keepalive force-stop). It does NOT gate on `!_actionExecutor.IsActive(entity)`. Once a player can trigger an action (Phase B), a jumping player whose intents go stale — or who is dead/frozen mid-action — would get `StopMovement()` called by the keepalive pass WHILE the executor is driving its position each tick → a Velocity/position fight (and a spurious stop-edge StateRevision bump).

**Fix (in Phase B):** gate that force-stop on `!_actionExecutor.IsActive(entity)`, mirroring the `HandleMoveIntent` suppression at ~`GameServer.cs:2773`. An entity mid-action is owned by the executor; nothing else should touch its motion.
