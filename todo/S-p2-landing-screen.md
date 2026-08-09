# S — P2: a real branded LANDING screen (replaces the thin splash + the small lobby panel)

Feel-test feedback: the P2-C startup splash was too thin — the demo wants an ACTUAL landing/title
screen (user pick: "branded title + press to begin"). This replaces the removed splash AND the small
`RunPhase.Lobby` face of the run panel with a full-screen branded title that leads into the run.
CLIENT-only (Godot); build to compile + smoke; the look/feel is a HUMAN feel-test.

## What it is
A full-screen branded LANDING screen shown while in the LOBBY (pre-run) state:
- Big **ADUE** title + the **a2** mark (TEXT placeholder — art pending) + a tagline (e.g. "two players,
  one line").
- A clear **"Press B together to begin"** prompt that reflects live ready state — e.g. "Waiting for
  your partner to ready…" / "You're ready — waiting for <partner>" / "Both ready — descending…".
  Pressing **B** readies up (the EXISTING `SendRunReady` path; RunEngine starts the run when both
  ready — this is the real "commit together" beat, per `docs/duo-living-tower.md`).
- Optional, if cheap: a one-line "…or /practice to warm up first" nudge toward the practice room.

## Behaviour / integration
- Shows during `RunPhase.Lobby` (the pre-run and between-runs state) AND the pre-login "connecting /
  waiting for your partner…" state (reuse the OnboardingCoach waiting copy). HIDDEN during
  `RunPhase.Active` (the run) and `RunPhase.Summary` (the end screen keeps the existing run panel).
- REPLACES the small lobby face of `UpdateRunPanel` (`MmoClientRoot.cs:~2872`, the `RunPhase.Lobby`
  branch "THE SUNDERER — press [B] to ready up"): suppress that small panel while the landing is up.
  The Active banner + Summary end screen stay in the run panel. Surface the cleanest integration as a
  fork (a new full-screen overlay vs. reflowing the run panel).
- Coexists with the duo-card reveal (the card flashes over it on pairing) and the auto-pair flow.
- NON-conflicting with auto-connect: connect/auto-pair still run in `_Ready`; the landing just renders
  the lobby state. It is NOT a "press start to CONNECT" gate — you're already connected; B readies.

## Rules
- Extract the pure "given (isPaired, selfReady, partnerReady/roster, phase) → landing prompt line"
  decision into a testable helper in `src/Mmo.Client.Core` (reuse/extend `OnboardingCoach` or a new
  small class) and UNIT-TEST it; the Godot layer renders it.
- CLIENT-only; NO protocol/wire change (reads existing RunStatus: phase, ready count, self-ready,
  pair state). If you think you need a wire change, STOP and surface it.
- Do NOT run gated scripts; do NOT commit; do NOT delete this todo. List exactly what a human must
  feel-test (layout, title/mark sizes, copy, the ready-state transitions, the a2 placeholder).

## Acceptance
- A full-screen branded landing shows in the lobby with the title + "press B together to begin" +
  live ready state; B readies; the screen gives way to the run when both ready and to the end screen
  on finish; hidden during Active/Summary. The prompt-line logic is unit-tested. godot-build compiles;
  godot-run smoke clean; run-checks green. Delete this file in the landing commit. HUMAN feel-test owed.
