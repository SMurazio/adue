# N — Telegraph T1 review followups (independent review of 0b54871: APPROVE-WITH-FOLLOWUPS)

Core engine/choke-point/refactor verified sound by the independent reviewer; these are the surviving
findings, priority order. (1) is a quick direct fix; (2)+(3) are one test-writing pass; (4) is a
game-design decision to make BEFORE/WITH T2's circle rendering.

1. **Clamp `/slam` radius (MINOR).** `HandleSlamCommand` (GameServer.cs ~2207) accepts any finite
   radius > 0 — the manifest path clamps to 16, this path clamps nothing. A fat-fingered
   `/slam 100000` makes the resolve-tick gather iterate ~(2·R/cell)² cells = multi-second
   single-thread stall; radius ≥ ~2^31 overflows `(int)Math.Ceiling` → gather collapses to 1 tile →
   giant telegraph silently misses. Fix: clamp to the same MaxSlamRadiusUnits bound the registry uses.

2. **Wiring + cadence pins (MINOR).** The suite drives TelegraphScheduler/PlayerDamageGate directly;
   nothing pins the GameServer wiring. Unpinned regressions that pass the whole suite: deleting the
   `_telegraphs.ResolveDue(_serverTick)` call in TickCore (feature dead, all green); deleting the
   brain's cooldown re-arm (BasicRoamerBehavior.cs ~419 `NextSlamTick`) → in-range slime schedules
   EVERY tick (20 casts/s, 15 dmg/tick at T+, `_pending` balloons). Add a headless brain-level test:
   slime + adjacent player, run N ticks, assert exactly ⌈N/cooldown⌉ schedules; plus a wiring pin
   that a scheduled telegraph resolves through a real GameServer tick.

3. **Gather-margin rim test (MINOR).** All tests place victims ≤1 tile from origin; the superset
   margin (`ceil(R)+1`, TelegraphScheduler.cs ~115) is unpinned — dropping the `+1`, or gathering
   around the CASTER instead of the origin, passes every test but yields live "I was inside and it
   missed" bugs. Add: origin x=32.49, victim x=34.49, radius 2.0 (neighbor-cell rim hit).

4. **DECIDED (user, 2026-07-03): CENTER-POINT membership — fairness pillar.** The user's combat
   pillar is "it needs to feel FAIR and RESPONSIVE": the drawn circle must BE the rule, and ambiguity
   errs player-favorable. You are hit iff your character's CENTER is inside the drawn circle; a body
   clipping the rim never kills you (the reverse — hit while looking out — is the unfairness we
   refuse). The divergence from melee/free-aim body-clip is deliberate and stays: telegraphs are a
   dodge-the-zone rule (legibility first), free-aim is a did-my-swing-connect rule. Remaining WORK:
   document this on TelegraphShape.Contains, require T2 to render the TRUE radius (no visual
   padding/shrink — the honest-telegraph rule), and pin center-point with a rim-overlap test (body
   overlaps, center outside → no damage).

Also noted (no action): the gate's players-only guard is a theoretical behavior change to
ApplyMonsterAttack (unreachable today); `_monsterTypes.Default` fallback is no longer inert (it's the
slam-enabled slime) but trigger and cast use the same fallback so they cannot disagree; uint
resolveTick wrap ≈6.8y accepted.
