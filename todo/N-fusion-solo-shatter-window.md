# N — Solo-degraded fusion still opens the FULL Good 6s shatter window (fusion review MODERATE)

`BossEncounterEngine.OnFusion` (~line 840): `tier == Perfect ? perfectWindow : goodWindow` —
the else-branch lumps the new Solo tier (d1fb411 point-blank downgrade) in with Good. Exploit:
stand point-blank on the Sunderer, mash Q every 0.6s → each Solo merge opens (and extends,
`OpenWindow` ~993) the same 6s window an earned mid-range Good fusion opens. The P1 mastery
inversion is fixed for damage but only reduced (9s→6s), not removed, for the shatter gate.

Fix: pick a Solo-tier window — suggest ~2-3s (enough to reward the degraded path per the boss
design's reachability requirement, clearly worse than Good's 6s) — or get explicit design
sign-off that Good-parity is intended. Also fix the stale comment at ~834-836 ("Perfect = 9s,
Good = 6s" — no Solo case mentioned).

Acceptance: headless test pinning the Solo window length distinct from Good; existing
degraded-path-reachable test still green.
