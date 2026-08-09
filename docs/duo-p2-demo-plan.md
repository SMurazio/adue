# P2 — the stranger-duo demo slice (the kill test)

**Status: PLANNING (2026-08-09).** P2 gates ALL further investment (`docs/duo-standalone-plan.md`).
The kill test all three design reviews converged on: put P1 + the duo kit + the Sunderer in front of
5-10 duos who don't know the developer and measure the pass criteria from the framework itself —
**do stranger duos attempt the Perfect version unprompted, and do they laugh?** (plus: where do they
bounce?).

## Decisions (2026-08-09, user)

- **Delivery = IN-PERSON LAN sessions.** Recruit stranger duos and sit them at a LAN setup you
  control (two machines + a local server — existing tech: `start-server` + `connect-server`). You
  OBSERVE the pass criteria live (Perfect attempts, laughter, bounce points) — observation IS the
  measurement. **Deferred past the gate by this choice:** Steam ($100 + app), the bundled host-side
  server, remote/downloadable packaging, matchmaking. None are needed to run the test.
- **Onboarding = a small PRACTICE / TUTORIAL ROOM.** A dedicated space to rehearse the four duo verbs
  (crossing→fusion, shield, tether, detonate) + pairing against a **scripted dummy** — NOT an AI ally
  playing the game; the practice-construct that survived the CLI red-team, scoped here as rehearsal.
- **Shell = a MENU, not a walkable hub** (`docs/duo-living-tower.md`, "A in doctrine, B in the
  build"): title → pair → (practice) → run → end screen (floor-height strip) → again. Numbered floors
  framing; the Sunderer is the top floor. No base camp, no extract/haul language.
- **Content = P1 chassis + the duo kit + the Sunderer** (all exist). Art stays committed-low-fi
  placeholder for the PLAYTEST; the Steam-facing art/market probe waits for real art direction (not a
  playtest blocker).

## Workstreams (decomposition finalised after a grounding pass on the current client)

- **A. Menu shell** (Godot client) — adapt the P1 run panel into the menu flow above + the
  end-screen floor-height strip.
- **B. Practice/tutorial room + scripted dummy** (client + a little server) — a rehearsal zone; the
  dummy stands/holds/fires on a metronome so a pair can practice each verb.
- **C. In-context onboarding** — teach Q/R/G/V + pairing without a manual (reuse the teach labels;
  the practice room carries most of this).
- **D. Instrumentation** — capture the pass criteria: mostly the observer, plus light bounce-point
  telemetry if cheap (where do they quit / get stuck).
- **E. In-person LAN session kit** — a setup checklist + one-command two-machine launch + a recruit/
  observation script. Mostly human/doc; small tooling.

## Pass criteria (from `docs/duo-mechanics-framework.md`)
Do stranger duos **attempt the Perfect version unprompted**, and do they **laugh**? Plus: **where do
they bounce** (pairing? a specific verb? the boss?). This gates whether the duo core is real.

## Not in P2 (explicitly deferred; do not build ahead of the gate)
Steam integration, bundled host-side server, remote packaging, matchmaking, couple-items/loot, the
second boss, committed art direction. Per the plan, all wait on the P2 result.
