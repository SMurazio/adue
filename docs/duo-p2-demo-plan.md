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

## Workstreams (grounded in the client map, 2026-08-09; sequenced by value to the kill test)

The run (P1 chassis + duo kit + Sunderer) already exists; these are the frame + onboarding around it.
Correction from the map: the duo set is **Q = fusion skillshot** (HOLD-aim/RELEASE; "fusion" is the
emergent CROSSING of both partners' shots server-side, not a button) **+ Shield / Tether / Detonate**
(the 3-value `DuoAbilityKind` on R/G/V) — 4 verbs, NOT 4 `DuoAbilityKind` values.

- **A. Practice room + scripted dummy — FIRST (highest value, all seams exist).** A bounded space a
  pair enters to rehearse the 4 verbs against a non-aggressive dummy before the run. Reuse: mirror
  `Mmo.Shared/Domain/BossArena.cs` for a new sealed `PracticeRoom` pocket + an `AuthoredMaps`
  stamp + carve-out; enter/leave via the existing `_zone.Teleport` seam (`GameServer.cs:665`); a new
  `"dummy"` `monsters.json` type with `aggroRadius 0` (the aggro test `Distance <= 0` never fires →
  never chases/attacks) spawned via `SpawnMonsterCore`. Server side is headless-testable; the client
  entry action + feel are the live part.
- **B. In-context onboarding (with/after A).** Teach the 4 verbs + pairing without a manual. Reuse the
  `Label3D` teach pattern (`Visuals/EntityVisual.cs:313`, today only the boss-plating cue); add a
  first-run / practice-room hint layer for the verbs + a pairing prompt. Mostly client; feel-gated.
- **C. Menu shell + title — polish over the EXISTING run panel.** `UpdateRunPanel`
  (`MmoClientRoot.cs:2872`) is already a lobby→run→end→restart loop keyed on `RunVersion`. Greenfield:
  a title screen, an explicit pair step as a screen (wraps `/pair` + existing `IsPaired`), and a light
  floor/height label on the end screen. NOTE: the run is a SINGLE room today, so "how high climbed" is
  cosmetic — the end screen already shows "Sunderer left at N% HP"; no multi-floor content in P2.
- **D. Instrumentation.** Mostly the observer (in person). Optionally light bounce-point telemetry
  (where they quit / stick) if cheap.
- **E. In-person LAN session kit.** A setup checklist + a one-command two-machine launch (existing
  `start-server` / `connect-server`) + a recruit/observation sheet. Mostly human/doc; small tooling.

Sub-tasks are seeded in `todo/` as they become active (A first). This doc is the decomposition of
record; `todo/N-adue-p2-stranger-demo.md` is the umbrella + pass criteria.

## Pass criteria (from `docs/duo-mechanics-framework.md`)
Do stranger duos **attempt the Perfect version unprompted**, and do they **laugh**? Plus: **where do
they bounce** (pairing? a specific verb? the boss?). This gates whether the duo core is real.

## Not in P2 (explicitly deferred; do not build ahead of the gate)
Steam integration, bundled host-side server, remote packaging, matchmaking, couple-items/loot, the
second boss, committed art direction. Per the plan, all wait on the P2 result.
