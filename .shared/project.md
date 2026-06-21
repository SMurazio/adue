# Project Instructions

This project is built by Claude Code: a main **orchestrator** loop that plans and drives the work, spawning
**Implementer subagents** to write code and **independent reviewer subagents** to verify it. This file is the
canonical project contract. Root `CLAUDE.md` imports it. The `todo/` queue is the backlog; see
`todo/README.md`.

## Usage Budget — ALWAYS true

Never spend more than the subscription's included usage. **Never activate extra / overage / pay-as-you-go usage
of any kind.** If a usage limit is reached, STOP and surface it to the user — do not opt into additional paid
usage to keep going. This binds every agent and subagent, always, and overrides any instinct to "just finish
the task."

## Startup Checklist

At the start of every session:

1. Read this file.
2. Read `.shared/memory/MEMORY.md` and any memory note relevant to the task.
3. Read `todo/README.md` before working the queue.
4. Use `.shared/skills/` as the canonical repo-local skills location.

## Roles

All roles are played by Claude Code — a main **orchestrator** loop plus the **subagents** it spawns. There is
no second external agent and no human relay.

**Orchestrator** (the main loop)

- Plans the work and makes architectural, scope, protocol, and priority decisions.
- Populates the `todo/` queue and maintains planning docs in `docs/`.
- Drives implementation by spawning Implementer subagents (or, for small changes, editing directly).
- Runs ALL build/test/stress verification and makes the commits — subagents can edit files but cannot run the
  gated scripts (`run-checks`, `dotnet`, stress, server launches), so the orchestrator is the single source of
  verification truth. See `.shared/memory/orchestrator-runs-verification.md`.
- Commissions an independent reviewer subagent to verify finished work and synthesizes the verdict; does NOT
  solely certify work it authored (see Review Independence).

**Implementer subagent** (spawned per unit of work)

- Implements a `todo/` item or explicit task; writes code and tests; emits a review-request briefing.
- Does not make architectural, scope, protocol, or priority decisions unilaterally — surfaces forks, ambiguous
  specs, or disagreements back to the orchestrator instead of guessing.
- Does not invent work outside its task.

**Reviewer subagent** (fresh, per review)

- Independently verifies finished work — given only the live symptom and the diff, not the plan (see Review
  Independence).

## The Loop

1. **Plan** - Orchestrator picks the next `todo/` item (priority `S` before `N`) or defines the task.
2. **Implement** - An Implementer subagent (or the orchestrator directly, for small changes) writes the code
   and tests, surfacing forks/ambiguities rather than guessing. New issues become new `todo/` files, never
   silent extra changes. The subagent emits a self-contained review-request briefing as
   `review/review-request-<slug>.md`: intent, base commit, how to diff, change manifest, decisions and
   deviations, self-verification notes, known gaps, highest-risk areas, and what the reviewer should check.
3. **Verify + commit** - The orchestrator runs `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` (and a stress
   run when relevant — the standard gate is **120 clients / 30s**, fixed and comparable across tasks; longer
   60s+ runs are reserved for milestone/capacity studies), then makes ONE discrete revertable commit per task
   referencing the task filename and deletes the task file in that same commit on success. A task that cannot
   be finished gets a `## Blocked` note and stays.
4. **Review** - For each file in `review/`, the orchestrator commissions a **fresh independent reviewer
   subagent** (clean context; given only the live symptom + the diff, never the plan — see Review Independence)
   that re-runs build/tests/stress, re-reads the diff, and tests the hypothesis against the actual symptom. The
   orchestrator synthesizes its severity-ranked verdict, updates `todo/` with any new findings, and deletes the
   request file once reviewed.
5. Repeat.

The orchestrator drives the whole loop; subagents are spawned per step and return their results to it. Some
verification (anything that runs the live Godot client) only the human can do — the orchestrator asks for it.

## Review Independence (author ≠ sole reviewer)

The part of Claude that **authored** a change — its plan, its handoff, or its code — is **never the sole
reviewer of it.** Independent verification is the entire point of the loop, and it collapses when the author
also designs the tests that "prove" the fix and then signs off on it. (Three movement-netcode misses —
UO5-stall, NET2, NET3-live — each passed a headless test the author wrote, because the test inherited the same
wrong model that produced the fix. An independent reviewer would have asked whether the test reproduces the
*live* symptom.)

**Mechanism.** When a unit of work is finished, the orchestrator commissions a **fresh reviewer subagent** with
a **clean context**, given **only the live symptom and the diff — NOT the plan or the handoff.** That reviewer
independently re-runs build/tests/stress, re-reads the diff, and judges the *hypothesis against the actual
symptom* — not merely "did the code match the plan." The orchestrator synthesizes and relays the reviewer's
verdict but does not self-certify work it planned; a finding the reviewer raises becomes a new `todo/` item.
The human still makes the final live call (only the human can run the Godot client).

## Branch Workflow

Work happens on **feature branches, never directly on `main`.** `main` only receives work that is **tested
(gates green) and approved (independent review, plus the human's sign-off where relevant).** Per unit of work:
branch off `main`, commit there, run the gates + the independent review on the branch, and merge to `main` only
once it is green and approved. (Not enforced on GitHub by choice — it is a discipline the agents follow.)

## Decision Authority

- Architecture, scope, protocol, and priorities are the orchestrator's call.
- Implementation details inside an accepted task are the implementer subagent's call.
- If an implementer subagent hits an architectural fork, an ambiguous spec, or a disagreement with the plan,
  it raises the issue in the briefing or a new `todo/` file rather than deciding unilaterally.

## Shared Artifacts

- `.shared/project.md` - canonical project contract and startup instructions.
- `.shared/memory/` - canonical, version-controlled durable project knowledge. Read
  `.shared/memory/MEMORY.md` at session start.
- `.shared/skills/` - canonical repo-local skills and scripts.
- `CLAUDE.md` - entry-point stub that imports this file.
- `todo/` - live backlog and source of truth for outstanding work.
- `review/` - the orchestrator's inbound review queue (review-request briefings awaiting independent review).
- `docs/` - plans and decision records.

## Project Guardrails

- Movement is tile-stepped (protocol v9), server-authoritative, and currently has no client
  prediction, lockstep, rollback, lag compensation, or LOS-for-AOI.
- Single process until metrics justify a split.
- Measure before optimizing.
- Use the repo-local SDK at `.tools\dotnet\dotnet.exe`.
- Use `.shared\skills\mmo-dev\scripts\run-checks.cmd` for the standard build/test check.
- **Diagnostics are live, in-client toggles — not launch flags.** Any opt-in debug utility (frame-log
  CSV dump, uncap-FPS, motion/perf overlays, future tracing) is exposed as a runtime control — an F5
  visual-panel checkbox or a hotkey — that flips on/off **while the client is running**. Do NOT gate a
  diagnostic behind a launch-time env var or anything that needs a client or server restart. Minimize
  restarts: every avoided client/server restart tightens the debug loop. (Precedents: the F5 uncap-FPS
  checkbox; the F5 "Frame log (CSV)" toggle, S68.)

## Safe Local Execution (binds the orchestrator and every subagent)

Run the server and clients **only** through the repo skill scripts under
`.shared\skills\mmo-dev\scripts\` (e.g. `start-server.cmd`, `start-godot-visual-check.cmd`,
`review-stress*.cmd`, `stop-mmo.cmd`). This is a hard rule, not a preference.

**Never** hand-roll ad-hoc shell that resembles a malware launcher on the user's machine. Concretely,
do NOT run commands that combine any of: `Start-Process -WindowStyle Hidden`, `-ExecutionPolicy
Bypass`, base64/escaped-quote-obfuscated one-liners, or `Stop-Process -Id`/`taskkill` PID-killing.
That pattern triggers Windows Defender (it has, including a silent launch failure) and makes the
machine look like it is under attack.

- Keep process launches **visible** (normal window) and **script-based**, never hidden/background.
- Stop processes via `stop-mmo.cmd`, not ad-hoc PID kills.
- If a diagnostic needs something the scripts don't do (e.g. a Release server, or server output teed
  to a file), **extend the script** (and have it reviewed) rather than improvising a raw launcher.
- If the only way to do a task looks like the forbidden pattern, stop and raise it instead of running
  it. See `.shared/memory/safe-local-execution.md`.
