# S107 — Latency-aware tuning: scale the commit reject-grace by effective RTT (fix the 100ms snap-back)

Severity: S (movement feel under latency — confirmed real on the current build). Client-only. No protocol/server
change. Builds on S103 (commit-step) + S93 (latency sim).

## Why

We MEASURE RTT (`MmoClient` `NetworkLatencyUpdateEvent` → `_movementTrace.UpdateLatency`) but feed it into NO
movement logic. At ~200ms RTT the commit-step's **reject grace window is a fixed snapshot count**
(`CommitRejectGraceSnapshots`, `MmoClient.cs:33`), so the server's ACCEPT snapshot arrives AFTER the grace
expires → `ReconcilePendingCommit` declares a **false reject** and `SnapTo`s the player back, even though the
server accepted. That is a prime cause of "behaves really poorly at 100ms." Fix: make the grace scale with the
actual round-trip.

## Critical detail — effective RTT must include the sim

The S93 net-latency **sim delays packets at the app layer (in `NetLatencySimulator`), AFTER LiteNetLib measures
RTT**, so `NetworkLatencyUpdateEvent`'s value is ~0 on LAN even with the sim at 100ms. The tuning MUST use:

    effectiveRttMs = measuredTransportRttMs + 2 * SimulatedLatencyMs   (SimulatedLatencyMs is one-way)

or it won't react to the sim slider during testing (and won't reflect reality). Expose `effectiveRttMs` on
`MmoClient` (and ideally surface it in the F3 HUD next to the existing latency line).

## What to build

- Compute and expose `MmoClient.EffectiveRttMs` (measured transport RTT + 2×sim one-way). Confirm whether
  LiteNetLib's reported latency is one-way or round-trip and combine correctly (document which).
- Replace the fixed `CommitRejectGraceSnapshots` with a **latency-scaled grace**: the client waits at least
  `ceil(effectiveRttMs / snapshotIntervalMs) + margin` snapshots (margin ~1-2) before declaring a commit reject,
  **capped** at a sane max (e.g. ≤ ~10 snapshots — "up to a point", so a pathological RTT can't hang a pending
  commit forever). At 0ms this collapses to today's small fixed grace; at 200ms it waits ~4-5 snapshots so a
  genuine accept is never misread as a reject.
- Keep the accept path unchanged (a confirmed tile == target still accepts immediately); only the
  reject-declaration timing scales.

This is intentionally NARROW: it fixes the false-reject snap-back. It does NOT grow the cosmetic lead past 1 tile
(that's model-A prediction territory) and does NOT change the steady cut (that's the S105 blend option). Those
are separate, deliberately.

## Tests
- `MmoClientCommitStepTests`: at a high `EffectiveRttMs`, a commit whose ACCEPT snapshot arrives several snapshots
  late is NOT falsely rejected (no `SnapTo`), and is accepted when the tile reaches the target; at 0ms RTT the
  grace matches today's behavior; the grace is capped (a never-arriving accept still resolves to a reject within
  the cap). Include the effective-RTT-includes-sim computation in a small unit test.
- Hardened `run-checks` green (`--no-incremental`); Godot build clean. Live check: at the 100ms sim, release past
  the commit threshold and confirm no spurious snap-back.

## Constraints
- Client-only; no protocol/server change. **Safe Local Execution** (scripts only; stop a locking session via
  `stop-mmo.cmd`, note it). You cannot run Godot — Orchestrator does the live check. If your shell is denied, say
  so explicitly; don't claim green you didn't run.
- Do NOT commit/push/delete the task file — leave the tree dirty + `review/review-request-s107-latency-grace.md`;
  the Orchestrator verifies (hardened gate) and commits.

## Acceptance
- `EffectiveRttMs` (measured + 2×sim) is exposed; the commit reject-grace scales with it (capped), so the 100ms
  sim no longer produces false-reject snap-backs; 0ms behavior unchanged. Tests + hardened run-checks green.
