# Review Queue

This folder is the **Orchestrator's inbound review queue** (the Implementer → Orchestrator channel
in `AGENTS.md`).

## Convention

- When the Implementer finishes a unit of work that needs review, it drops one
  `review-request-<slug>.md` here — a self-contained briefing following the structure in
  `AGENTS.md` step 3 (intent + branch & base commit, how to diff, change manifest, decisions &
  deviations, self-verification evidence incl. a fresh stress run, known gaps, highest-risk areas,
  and what the reviewer should check).
- The Orchestrator treats each file as a task: it verifies the briefing **independently** (re-runs
  build/tests/stress, re-reads the diff — never rubber-stamps), produces a severity-ranked verdict
  (BLOCKING vs nits, with file:line), records any actionable findings in the `todo/` queue, and
  delivers the verdict to the human.
- Once reviewed, the Orchestrator **deletes the request file** here (git history / the reviewed
  branch preserve the briefing). A request that can't be fully reviewed gets a `## Blocked` note and
  stays.

So at any moment, the contents of `review/` = "what is waiting on the Orchestrator."
