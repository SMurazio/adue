# N — Profile the dormancy scan in a monster-DENSE cluster before ecology ships at scale (E0 review flag)

E0 review (4cd6098, APPROVE-WITH-FOLLOWUPS): idle/roaming steady-state cost dropped to ~0, but the
relevance scan now uses the DORMANCY-widened gather box (max(aggro, InterestRadius) → ~81 cells at the
default radii vs ~4-9 before — the same order as a player AOI query), runs per-monster on the ~0.5 s
staggered cadence, and distance-tests every candidate in the box (the query doesn't pre-filter by
kind). In a dense monster hub (the ecology target scenario, 100-300 monsters with overgrown clusters)
that is an emergent O(local-monster-density) cost per scan — bounded, probably fine, but UNMEASURED in
exactly the configuration that matters (the standard stress run is monster-sparse).

DO before ecology populations ship at the high end (E2 + overgrowth): a Measure-category or capacity
run with ~200+ clustered monsters, mostly dormant, no players near — assert the scan pass stays within
budget. THE PRE-DESIGNED LEVER if it's hot: scan-cadence exponential backoff while dormant (a monster
nobody approached in 30 s doesn't need a 0.5 s re-check; cap the backoff so approach latency stays
sub-second). Do NOT build the lever before the profile says so (measure-before-optimizing).
