# S50 — Gate ALL dev/monitoring overlays behind F3 (one hotkey, hidden by default)

Severity: should-fix (client UX / clean default screen). Play-test request: the server-monitoring HUD
(tick budget, bandwidth, etc.) is always on; only the FPS/perf HUD is behind F3. Put **all** the
dev/monitoring overlays behind the **same F3 toggle**, hidden by default, so the default screen is clean.

## Current state (`src/Mmo.Client.Godot/MmoClientRoot.cs`)
- `F3` → `TogglePerfHud()` toggles `_perfPanel` only; `_perfPanel.Visible = false` by default (~line 394).
- The **metrics panel** (`metricsPanel` / `_metricsLabel`, top-right server monitoring) is added to the
  overlay (~line 397) with **no visibility gate → always visible**.
- The **status panel** (top-left, `_statusLabel`) shows connection/help/diagnostics — also always visible.
- Gameplay UI: chat (`_chatLabel`), inventory (`_inventoryLabel`), harvest toasts (`_toastPanel`).

## What
1. Make **F3 toggle one cohesive "debug/monitoring overlay" set**: the **perf panel** AND the
   **metrics (server-monitoring) panel** together. Both **hidden by default**; F3 shows/hides both.
   Rename `TogglePerfHud` → e.g. `ToggleDebugOverlay` (or keep the name but toggle the whole set) and
   track one `bool _debugOverlayVisible`.
2. **Status panel:** gate the **diagnostic** content behind F3 too (it's monitoring/dev noise). If any of
   it is genuinely useful always-on (e.g. a one-line connection state, or the controls hint), keep a
   minimal always-on element and move the rest behind F3 — but default to a clean screen. Use judgment;
   the human will confirm the exact split visually.
3. **Keep gameplay UI always visible:** chat, inventory, harvest toasts are NOT monitoring — leave them on.
4. Update the controls/help text (~line 629, "F3 toggles perf.") to reflect that F3 now toggles the whole
   monitoring/debug HUD.
5. Don't break the `mmo-client-control` MCP `client_toggle_perf` / telemetry hooks — if `client_toggle_perf`
   maps to this toggle, keep it working (toggling the unified overlay is fine); note any change.

## Files
- `src/Mmo.Client.Godot/MmoClientRoot.cs` — unify the toggle; default-hide perf + metrics (+ status
  diagnostics); update help text.

## Acceptance
- On launch the screen is clean (no perf, no server-metrics, minimal/no diagnostics); **F3 reveals all of
  them together and hides them again**; chat/inventory/toasts unaffected. Verify via the Godot client
  (visual) + the `mmo-client-control` MCP (`client_toggle_perf` still functions).
- `godot-build.cmd` green; `run-checks.cmd` green for any `Mmo.Client.Core` changes (likely none — this is
  Godot-overlay only). Do NOT commit — Orchestrator reviews. Verification is partly **visual** (human).
