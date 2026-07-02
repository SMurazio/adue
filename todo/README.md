# TODO Queue

Each file here is one self-contained work item. An agent works through them and removes them as they land.

## Convention

- One task per file, named `<PRIORITY>-<slug>.md` where priority is `S` (should-fix, do first)
  or `N` (nit/follow-up). Work `S*` before `N*`.
- Each file states: the problem (with `file:line`), the fix, and acceptance criteria.
- On completion: implement the fix, add/adjust regression tests, run
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd`, then **delete the file in the same commit**.
  One commit per task; reference the task filename in the commit message.
- If a task cannot be completed, do **not** delete it — append a `## Blocked` section explaining
  why, and move on to the next.
- Do not expand scope beyond what a file describes. New issues discovered along the way become
  new `todo/` files, not silent extra changes.

## Current priority order (as of 2026-07-02, post-continuous-migration)

Branch of record: `feat/continuous-migration` (fully continuous-native; `main` is still frozen tile).
The old tile-era order (S41/S36b/S42) is long shipped or obsolete. Active order:

1. **Quick hardening wins (small, do first):**
   - `N-atomic-manifest-write` — temp-file + atomic move for the F1 Save manifest write.
   - `N-docs-hygiene-resync` — add the protocol.md ↔ `ProtocolCodec.Version` drift gate.
   - `monster-types-followups` — `/clearspawners` admin command (+ the ~300 ms prose nit).
   - `loot-followups` — #1 construction-time tableRef cycle detection (+ #2 coverage).
2. **Guardrail compliance:** `N-movement-trace-live-toggle` — `MMO_DEBUG_MOVEMENT` env var
   violates the live-toggle rule; make it an F1 checkbox.
3. **Netcode feel (measure first, full rigor):** `N-gnoll-walk-jitter-extrapolation` —
   remote extrapolation of a turning entity; then `N-remote-extrapolation-followups` as gated.
4. **Feature track:** `S-movement-actions-phase-d` (charge + dodge-roll — framework proven by
   A/B/C, adding actions should now be cheap) → `N-movement-actions-phase-e` (skill-input wiring
   + animations; needs ART + human feel-tests).
5. **Deferred / trigger-gated:** `monster-ai-dormancy` (implement when monster-AI tick cost is
   measured material), `N-phaseC-monster-dense-bandwidth-stress` (run with the next stress pass),
   `N-test-suite-audit-tile-era-cruft`, remaining phase-followup files, `S28` (needs a human,
   nice-to-have despite the prefix).

## Waiting on the HUMAN (not agent-workable; ask, don't block)

- **Feel-tests pending:** walk-anim-idles-when-blocked (`31ab750`), free-angle movement
  (`825d0ba`), movement speed multipliers (`ea15bea`), monster behaviors P2/P4/P5 (gnoll
  glide/flee/charge), AgX tonemapping (`5c2823d`).
- **Merge decision:** `feat/continuous-migration` → `main` (hard replacement of tile) after the
  full feel-test.
- **Scope confirmations:** `N-retire-web-client` (retirement final?), the `docs/tile-audit.md`
  DECISION items (persistence tile cols, spatial-grid cell size, AOI gather-quant),
  `N-slime-feel-polish` (needs the user's feel verdict).
- **ART:** monster P6 real per-type visuals/animations (placeholder tint/scale shipped).

> **Protocol changes must update `docs/protocol.md`** (version + message list) in the same unit of
> work. `N-docs-hygiene-resync` adds a gate test so this drift fails the build instead of recurring.
