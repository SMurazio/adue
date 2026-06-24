# N — loot follow-ups (from the P4a review)

P4a (the loot engine) shipped SHIP. Three items the reviewer flagged as latent/coverage-depth — address in **P4b,
before loot content grows beyond the current 1-deep acyclic seed**:

## 1. Construction-time cycle detection for nested tableRefs
`LootTableRegistry` construction validates only that referenced table ids EXIST, not that the nesting graph is
ACYCLIC. An authoring cycle (A→B→A) is caught only at ROLL time (skip+log at the depth-8 guard) — safe (no stack
overflow) but every rolled kill pays up to 8 wasted hops AND emits a warn-spam line. Add an acyclic-graph check at
registry construction so a bad table fails fast at startup, not once per kill. Latent now (the seed is 1-deep,
acyclic); matters the moment more nested pools are authored.

## 2. (coverage) `slime_core` rate test + an explicit independence assertion
The rarest drop (`slime_core`, 0.0008) has no rate test, and the "each drop resolves independently" claim has no
dedicated assertion (it's structurally true — a fresh `NextDouble()` per drop, no shared state — and implicitly
exercised, but not directly asserted). Add both when touching the loot tests in P4b.

## 3. (note) Rare-tail statistical tolerance is loose
`LootTableTests` rare-tail check is ±0.0015 around 0.004 (~±37%): it catches gross errors (2×, inversion) but
would pass a subtle ~30% bias. Fine for a foundation gate; tighten with more rolls if precise calibration ever
matters.
