# N — ContinuousServerStallRegressionTests: drive the REAL SendSnapshotPackets

**Priority:** N (test fidelity). From the independent review of `1133c7e`.

`tests/Mmo.Server.Tests/ContinuousServerStallRegressionTests.cs` comments "THIS HARNESS IS THE REAL PATH" but
re-implements the snapshot delta-selection INLINE (`carryPlayer = firstSend || Position != lastSentPosition`) instead of
calling the shipped `GameServer.SendSnapshotPackets`, and its trigger (`Position != lastSentPosition`) is NOT the shipped
trigger (`entity.Velocity.LengthSquared > 0d`). They're behaviorally equivalent for the moving case (both fire every tick
while moving), so the test still validates the symptom — but the comment overstates fidelity and the test would NOT catch
a regression in the real `SendSnapshotPackets` selection loop. **Fix:** tighten the harness to drive the real method, or
correct the comment to "models the selection" (the `ContinuousTileGatedReconcileTests` client-side repro is the faithful
behavioral guard).
