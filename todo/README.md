# TODO Queue — ADUE

Each file here is one self-contained work item. An agent works through them and removes them as
they land.

## Convention

- One task per file, named `<PRIORITY>-<slug>.md` where priority is `S` (should-fix, do first)
  or `N` (nit/follow-up). Work `S*` before `N*`.
- Each file states: the problem (with `file:line`), the fix, and acceptance criteria.
- On completion: implement the fix, add/adjust regression tests, run
  `.\.shared\skills\mmo-dev\scripts\run-checks.cmd`, then **delete the file in the same commit**.
  One commit per task; reference the task filename in the commit message.
- If a task cannot be completed, do **not** delete it — append a `## Blocked` section and move on.
- Do not expand scope beyond what a file describes. New issues become new `todo/` files.

## Current priority order (as of 2026-08-09, P1 landed)

Roadmap of record: `docs/duo-standalone-plan.md`.
**P1 (run-loop chassis) is DONE + independently reviewed (SHIP-WITH-FOLLOWUPS) on
`feat/p1-run-loop` — UNMERGED; the live feel-test is the merge gate.** Order:

1. **P1 review followups**: `S-adue-run-edge-ux` (leaver-frozen-dead, refusal-reports-success,
   silent abandon, globally-dismissible end screen), `S-adue-run-symptom-tests` (session-level
   harness so RespawnPlayers/MarkAlive/dead-ready are tested for real),
   `N-adue-runstatus-hardening`.
2. **Duo/boss followups that sharpen the demo**: `N-boss-p3-ward-reject-legibility`,
   `N-boss-p1-partner-loss-slog`, `N-fusion-review-followups`,
   `N-telegraph-shapes-review-nits`, `N-boss*-review-followups` as they bite.
3. **`N-adue-p2-stranger-demo`** — the kill-test demo slice (gates all further investment per
   the plan; needs P1 merged + feel-tested).
4. **Post-P2 (only if the demo passes):** couch co-op mode, bundled host-side server, first
   couple-items, second boss from the inversion generator (`docs/duo-standalone-plan.md` P3).
5. **Inherited MMO-era files** (ecology/AOI/stress/web-client/etc.): PARKED — irrelevant to Adue
   until they block something; delete or work them only on friction, per the fork plan.

## Waiting on the HUMAN (not agent-workable; ask, don't block)

- **FIRST LIVE ADUE RUN (the P1 merge gate):** on `feat/p1-run-loop` — press **B** (or
  `/ready`) to ready up, full run to the end screen, solo and duo. Then the merge decision
  `feat/p1-run-loop` → `main`.
- **Feel-tests pending:** the duo-grill fixes as a set (echo-cue ring flash; P3 ward duo rule
  Good/Perfect + >=4u — the 4u and the fusion 2.0u/0.6s numbers are all untuned; the 2-3u
  fusion crossing band; opposite-side-hug P3 strategy `N-boss-p3-opposite-side-hug`).
- **ART direction**: the committed low-fi style decision (blocks the P2 demo's Steam-facing
  packaging, not the P2 playtest itself); the a2 logo pick (mockups exist — Fable artifact).

> **Protocol changes must update `docs/protocol.md`** (version + message list) in the same unit
> of work — the drift gate test enforces this.
