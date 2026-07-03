# N — Slam root+leap review followups (review verdict: APPROVE-WITH-FOLLOWUPS; grounded-gate already fixed)

Independent review of the root+leap change confirmed the leap-timing math (lands exactly on the resolve
tick; leapStart = resolve − D + 1, D = min(HopAirborneTicks, windupTicks)), root completeness, channel
exit guarantees, and non-slammer safety. The orchestrator already fixed finding 1 (grounded gate in
TryBeginMonsterSlam — no more mid-hop casts). Remaining, all TEST work (no production changes):

1. **Integration pin of GameServer.TryBeginMonsterSlam** (the important one). The current
   BasicRoamerBehaviorTests fake (PlanSlam) RE-IMPLEMENTS the timing formula — a sign/min error in the
   real GameServer derivation would ship green. Add a test that calls the REAL TryBeginMonsterSlam (via
   the live-server harness in TelegraphWireIntegrationTests.cs, or by extracting the derivation to a
   testable seam) asserting: (a) LeapStartTick == resolveTick − min(hopAirborne, windup) + 1 for the
   shipped slime numbers; (b) a target beyond HopDistanceUnits DECLINES (returns false, schedules NO
   telegraph — PendingCount unchanged); (c) an airborne caster (action active) DECLINES; (d) the cast
   aims the leap at the QUANTIZED origin (SlamCast.Origin == TelegraphScheduler.QuantizeToWire result).
2. **Two-sided landing pin.** The existing landing test catches a LATE landing only. Add: at
   resolveTick − 1 the slime is still airborne (VerticalOffset > 0), at resolveTick it is grounded at
   the origin. This catches an EARLY landing regression.
3. **Deferred-start retry test.** The retry-with-shortened-duration path in the brain
   (SlamLeapStarted=false re-try; duration = max(1, min(airborne, resolveTick − S' + 1))) is
   load-bearing but unreachable with live slime numbers — pin it directly with a contrived def
   (hop cadence long enough that the leap tick arrives while the executor still rejects), asserting the
   shortened hop still lands ON resolveTick.

NITs (note in code comments if touched, no dedicated work): the separation pass can nudge a rooted
channeler (~accepted, matches rooted players); the reachability gate tests raw distance but the leap
aims at the quantized origin (≤ ~0.045u over hop range at the boundary — cosmetic); the test harness's
CreateSlamLeap omits HopDelayTicks vs the real impl (deliberate simplification — keep them from
drifting further).

Standard band, test-only: implementer + gates; no independent review needed.
