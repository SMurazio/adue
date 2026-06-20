# S68 — Live "Frame log (CSV)" toggle in the F5 visual panel

Severity: dev tooling. Today the per-frame CSV (`.run/client-frames.csv`, S67's motion columns) only writes when
the client is launched with `MMO_DEBUG_FRAME_LOG` set — the launch script never sets it, so capturing a trace
means an env-var dance + relaunch. Add a **live toggle** that starts/stops the CSV dump while the client is
running, exactly like the existing **"Uncap FPS (vsync off)"** F5 checkbox (S66-era). Client-only; no movement
or behavior change.

## What
1. Add a **`CheckBox` to the F5 visual panel** (`BuildVisualPanel`, next to the uncap-fps checkbox), labeled
   e.g. **"Frame log (CSV)"**. Toggling it:
   - **On** → start the per-frame dump: open `.run/client-frames.csv` **fresh** (truncate / new file so each
     capture is clean, not appended to a stale one), write the header, and begin appending rows
     (`AppendFrameCsvRow` already runs each frame and no-ops when the writer is null).
   - **Off** → **flush + dispose** the writer and null the handle so writing stops cleanly.
2. Refactor minimally: reuse the existing `OpenFrameCsv()` / `AppendFrameCsvRow()`; add a `CloseFrameCsv()`
   (flush + dispose + null). Make `OpenFrameCsv()` open a fresh file on each call (so a mid-session toggle-on
   starts a clean trace). The existing `MMO_DEBUG_FRAME_LOG`-at-launch path keeps working (auto-start); the
   checkbox should **reflect the current state** on first open (checked if the dump is already running) and
   control it live thereafter — mirror the uncap-fps checkbox's `SetPressedNoSignal` init + `Toggled` wiring.
3. Keep it admin-gated like the rest of F5 (note it in the review).

## Constraints
- Client-only; no protocol/server/movement change. Reuse the F5 panel's `CheckBox` pattern + the existing CSV
  writer (`OpenFrameCsv`/`AppendFrameCsvRow`, ~line 1640-1700) — don't reinvent. Header/columns unchanged
  (the S67 16-column format).
- Flush on toggle-off and on close so partial captures aren't lost (the writer uses `AutoFlush = false`).
- Run `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` before/after (try it; if Bash is denied, note it and
  continue — Orchestrator runs the gates). You can't run Godot — Orchestrator runs `godot-build` and verifies
  by toggling it on via the F5 panel after a relaunch (then a CSV with fresh 16-column rows appears). **Safe
  Local Execution** binds you.

## Acceptance
- `godot-build` green; the F5 panel has a live "Frame log (CSV)" checkbox; toggling on starts a fresh
  `.run/client-frames.csv` writing the S67 16-column rows, toggling off stops + flushes; the
  `MMO_DEBUG_FRAME_LOG` launch path still auto-starts it. Movement unchanged. Review-request →
  `review/review-request-s68-live-frame-csv-toggle.md`. Do NOT commit or delete the task file.
