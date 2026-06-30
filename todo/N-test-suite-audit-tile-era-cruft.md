# N — test-suite audit: prune obsolete (tile-era + redundant) tests

User observation (725 tests): "a lot come from tile stuff" — the tile-stepped movement path was DELETED in the
continuous migration, but tests pinning that dead behavior likely linger, plus accumulated redundancy. Re-evaluate the
suite and prune the DEAD WEIGHT.

**Goal:** remove tests that no longer earn their keep — NOT lower the count for its own sake. Keep all genuine
coverage; cut only:
- Tests for REMOVED/DEAD code (tile-step movement, tile prediction `LocalPlayerPredictor`-era, `MoveStep`/
  `StepCommit*`/`MoveInput`/`MovementMode`, `TileInterpolator`/`MonsterHopInterpolator` if gone, turn-delay, the
  cosmetic HopHeight, etc.) — confirm the SUT actually still exists first.
- Tests pinning OBSOLETE behavior the design intentionally changed (tile-quantized movement/AOI assertions that the
  continuous model supersedes).
- REDUNDANT tests (multiple tests asserting the same invariant; a probe/measure test kept past its decision).

**Approach (like the tile-reference audit):** a read-only sweep over `tests/**` categorizing each test file/class:
KEEP (live coverage) / DELETE (dead SUT or obsolete behavior, with the file:line proving the SUT is gone) / MERGE
(redundant) / DECISION (judgment call). Synthesize into `docs/test-audit.md`; delete the clear DEAD ones as discrete
gated commits; surface DECISION/MERGE for the user. CAUTION: a green test is cheap insurance — only delete when the
SUT is genuinely gone or the behavior is intentionally retired; when unsure, KEEP + flag.

**Also watch for FLAKY tests:** during P5 a monster test failed ONCE in ~9 full-suite runs (a 2nd failure alongside the
since-fixed charge test), never reproduced. Likely an RNG/ordering-dependent monster-AI test (the charge test had the
same class of bug: a monster that doesn't deterministically aggro falls back to random roam, making distance/position
assertions flaky). Sweep the monster/AI tests for assertions that depend on roam RNG without first forcing CHASING
(aggroRadius >= target distance). Make them deterministic or seed-pinned.

**When:** deferred ("at some point" per the user) — not blocking the monster-behavior phases. Good to run between
phases or once the monster work settles. Builds on [[tile-continuous-cleanup]].
