# Project Instructions — ADUE

**Adue** (logo mark "a2", from the score marking *a due*: two divided instruments rejoining on a
single line) is a **standalone two-player co-op action roguelite**: relationship-as-input combat
(crossing projectiles that fuse, sync-window shields, a tether beam, midpoint detonations),
contested-channel bosses, ~30-45 min runs, couch + online co-op. Canonical design docs:
`docs/duo-mechanics-framework.md` (the 12-Law grammar — the review checklist for all duo work),
`docs/boss-encounter-sunderer-design.md`, `docs/duo-standalone-plan.md` (the fork plan + phases).

This repo is a **full-history fork of the MMO repo (`D:\MMO`, github SMurazio/mmo)** made
2026-08-09 at commit `10c0f9c`. The MMO continues separately. **Prune on friction, not on
principle**: MMO-only systems still in this tree (SQLite persistence, ecology, AOI-at-scale,
spawners, stress fleet) stay until they actually block a change. No submodules/shared packages —
a fix that matters to both repos is cherry-picked across by hand.

This project is built by Claude Code: a main **orchestrator** loop that plans and drives the
work, spawning **implementer subagents** to write code and **independent reviewer subagents** to
verify it. This file is the canonical project contract. Root `CLAUDE.md` imports it. The `todo/`
queue is the backlog; see `todo/README.md`.

## Usage Budget — ALWAYS true

Never spend more than the subscription's included usage. **Never activate extra / overage /
pay-as-you-go usage of any kind.** If a usage limit is reached, STOP and surface it to the user —
do not opt into additional paid usage to keep going. This binds every agent and subagent, always.

## Startup Checklist

1. Read this file.
2. Read `.shared/memory/MEMORY.md` (NOTE: inherited MMO-era notes are marked — read the fork
   banner there) and any memory note relevant to the task.
3. Read `todo/README.md` before working the queue.
4. Use `.shared/skills/` as the canonical repo-local skills location.

## Roles, Loop, Review Independence, Decision Authority

Identical to the MMO contract this repo forked from, in brief:

- **Orchestrator** plans, decides architecture/scope/priorities, runs ALL verification
  (`run-checks`, stress, launches) and makes ONE discrete revertable commit per task (deleting
  the todo file in that same commit); commissions reviews; never sole-certifies its own work.
- **Implementer subagents** write code + tests for one task, surface forks instead of guessing,
  emit a `review/review-request-<slug>.md` briefing, and never run the gated scripts.
- **Reviewer subagents** are fresh per review, get the live symptom + the diff — NOT the plan —
  and judge the hypothesis against the symptom.
- Work happens on **feature branches, never directly on `main`**; merge only tested + reviewed.
- **Scale rigor to risk** (2026-08-09 policy): `N-*` nits skip independent review — orchestrator
  verification (run-checks green + the task's own regression test) is their gate. `S-*` reviews
  batch **by shared code seam, not by count** — one fresh reviewer per cluster of tasks touching
  the same code, never a grab-bag diff. The **host-authoritative sim** (run-loop / combat / damage /
  hit-test / protocol / two-session concurrency) still gets one review per seam-batch regardless:
  "authoritative" here means the host holds the real game state both players trust — it is a
  *sim-correctness* concern, NOT dedicated-server ops (which are parked). Couch-only paths (one
  client, one clock) are lower-risk. Trivial edits still go direct with no review.
- Some verification (anything that runs the live Godot client) only the human can do — ask.

## Project Guardrails

- Combat pillar: **fair and responsive** — honest telegraphs (render = hit test, center-point
  membership, err player-favorable); windup > latency + fair dodge window. The 12 Laws govern
  every duo mechanic; Law changes are a user decision.
- Server-authoritative continuous movement, client prediction for the local player. The roadmap
  includes a **bundled host-side local server** (host client launches the server as a child
  process) — no dedicated-server ops for a shipped $20 game.
- Couch co-op (two players, one client, one clock) is a first-class target: it dissolves the
  sync-window latency debt (Law 11) locally. Online keeps the client-server path.
- **Diagnostics are live, in-client toggles — not launch flags.** (F5 panel / hotkeys; never
  env-var-at-launch; minimize restarts.)
- Use the repo-local SDK at `.tools\dotnet\dotnet.exe` and
  `.shared\skills\mmo-dev\scripts\run-checks.cmd` for the standard gate.
- Measure before optimizing.

## Safe Local Execution (binds the orchestrator and every subagent)

Run the server and clients **only** through the repo skill scripts under
`.shared\skills\mmo-dev\scripts\` (`start-server.cmd`, `start-godot-visual-check.cmd`,
`stop-mmo.cmd`, ...). Never hand-roll launchers combining hidden windows, policy bypass,
obfuscated one-liners, or ad-hoc PID kills — that pattern triggers Windows Defender. Keep
launches visible and script-based; stop via `stop-mmo.cmd`; extend a script (and have it
reviewed) rather than improvising. If the only way to do a task looks like the forbidden
pattern, stop and raise it.
