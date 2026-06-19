# N22 — Harden flaky `DepletedNodeRejectsSecondHarvest` integration test

Severity: nice-to-have (test robustness). Surfaced during S39 review: this S38 harvest integration test
**failed once under full-suite parallelism but passes in isolation** — a timing flake, not a logic bug.

## Likely cause

`InteractHarvestIntegrationTests.DepletedNodeRejectsSecondHarvest` harvests a node (success), then sends
a second `InteractRequest` expecting `InteractResult(false, "depleted")`. But S38 added a **4-tick
interact rate-limit** (`ClientSession.TryConsumeInteract`). If the second request lands within that
window — easy under parallel test load / scheduling jitter — the server replies `"rate_limited"` instead
of `"depleted"`, and the assertion fails. So the test conflates two reject reasons depending on timing.

## Fix

Make the second-harvest timing deterministic with respect to the rate limit:
- Wait past the interact cooldown (advance enough ticks / `WaitUntil` the rate-limit window has elapsed)
  before the second `InteractRequest`, **or**
- Assert the node is depleted via a path that isn't sensitive to the interact rate-limit (e.g. drive the
  second attempt after a deterministic delay and assert the reason is `"depleted"` specifically, retrying
  past any `"rate_limited"`), **or**
- Separate the two concerns into two tests (one for `depleted`, one for `rate_limited`) each with explicit
  timing.

## Acceptance
- The test passes reliably under the full parallel suite (run `run-checks` several times; no intermittent
  failure). No production code change expected (this is test timing only).
- `run-checks.cmd` green.
