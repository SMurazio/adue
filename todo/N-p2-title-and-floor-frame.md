# N — P2-C: title/splash + floor framing (the last P2 polish)

Part C of the P2 demo (`docs/duo-p2-demo-plan.md`) — cosmetic frame over the EXISTING run loop
(`UpdateRunPanel` is already lobby→run→end→restart). CLIENT-only, feel-gated (build to compile +
smoke; the look/placement is a human feel-test). NO server change — the run is a single room, so
"how high climbed" is cosmetic (the end screen already shows "Sunderer left at N% HP").

## 1. Title / splash (greenfield — no title screen exists per the client map)
A branded intro: "ADUE" + the a2 mark (TEXT PLACEHOLDER — art pending) + a tagline. Make it feel like
a game, not a debug client, when a stranger duo sits down.
- **NON-BLOCKING** — it must NOT gate or delay the auto-connect / auto-pair flow (a demo-killer if it
  traps). Simplest safe design: a brief self-dismissing splash at startup, and/or the branded backdrop
  behind the "Waiting for your partner…" state that clears once in-game. NOT a "press start to connect"
  gate. Surface the approach as a fork if there's a materially better option.

## 2. Floor framing (light, over the existing run panel)
- Frame the encounter as a numbered floor per the doctrine (`docs/duo-living-tower.md`): the run banner
  reads e.g. "FLOOR 1 · The Sunderer" (there is only one floor today — this is framing, not content).
- End screen: add a one-line "how far you got" beat on top of the existing stats — e.g. "You reached
  the Sunderer." / "FLOOR 1 CLEARED" on a clear vs "The Sunderer still stands." on a wipe. Reuse the
  existing RunSummary fields (BossHealthPercent / outcome); NO new RunSummary field.

## Guardrails
- CLIENT-only; NO protocol/wire change (reads existing replicated RunStatus/RunSummary). If you think
  you need one, STOP and surface it.
- Extract any pure decision logic (e.g. the end-screen line for a given outcome/percent) into a
  testable helper (Mmo.Client.Core) + unit-test it; the Godot layer renders it.
- Do NOT claim the visual/feel is validated — list what a human must feel-test.
- godot-build compiles; godot-run smoke clean.

## Acceptance
- A non-blocking title/splash shows at startup without delaying connect/auto-pair; the run banner shows
  the floor label; the end screen shows the reached/cleared/wiped line. Any extracted end-screen-line
  logic is unit-tested; godot-build + godot-run clean; run-checks green. Delete this file in the
  landing commit. HUMAN feel-test owed (title look, floor label, end-screen copy/placement).
