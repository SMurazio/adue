# N — BOSS-3 review follow-ups (from the Fable review of 46e8956, verdict SHIP-WITH-FOLLOWUPS)

- **MEDIUM — Zone.DisplaceResolved has zero direct tests.** The engine tests substitute a
  simplified displace delegate; the real wall-clamped shove — especially the MarkRepositioned
  else-branch (sub-tile shove must still re-publish; the stop-edge replication-miss class that
  bit this project three times) — is uncovered. FOLD INTO BOSS-4 as a prerequisite (its knockback
  pulses reuse this seam heavily): Zone-level test, shove into wall -> position clamped at wall
  face minus radius + StateRevision bumped on BOTH branches.
- **MEDIUM — field ring visual is not the hit test (USER DECISION at feel-test).** The 2u decal
  draws at each player's FIRE-time position; the resolve is PAIR-DISTANCE (6u/4u) 1.2s later.
  You can "step out of the ring" and still take Repel damage. Contradicts the honest-telegraph
  pillar (render = hit test). Options if the user rejects it: boss-centered ring solo; or a
  ring drawn AROUND THE PARTNER at radius 6u/4u (then render IS the rule: "partner inside my
  ring = repel danger") — the partner-ring variant is the recommended honest form.
- **LOW — shove ignores the damage outcome**: i-framed/shield-negated players are still
  displaced; a killed player's corpse is shoved. Likely intended (knockback as physics) —
  document in the delegate comment + one pin test.
- **LOW — duo->solo degradation mid-telegraph mislabels the field**: a "BIND" announced with 2
  living can resolve as solo move-out if one dies during the 1.2s window. Legibility wobble.
- **LOW — unbounded same-tick splinter pop burst** (up to 6 pops = 72 damage one tick when all
  converge on one player). Decide deliberately: cap pops/tick (1-2) or accept as pressure.
- **NIT — stagger comment overclaims** (covers only the 3 scheduled streams, not pops/baseline
  kit). **NIT — EchoLash living-only filter + disarm-cancels-pending are unpinned** (verified by
  code read only). **NIT — missing "splinter" type falls back to Default roamer silently**
  (acknowledged drone-precedent pattern).
