# S — ADUE P1: the run-loop chassis (roguelite skeleton)

From `docs/duo-standalone-plan.md` P1. Strip "persistent world" out of the moment-to-moment
loop and replace it with the run shape:

**start-run → arena/floor → boss (the Sunderer) → death-or-clear → end screen → restart.**

Scope (keep it a skeleton — no meta-progression, no items, no new bosses):

1. A "run" state machine on the server: lobby/ready (both players ready up or solo-start) →
   teleport into a run arena → fight waves/reach the Sunderer → clear or wipe → end-of-run
   summary state → reset cleanly into a new run without server restart.
2. Reuse the existing boss arena + `/boss` flow as the run's boss room; the run replaces the
   ad-hoc `/boss` command as the front door (command can stay as a dev shortcut).
3. Death rules for a run: no town respawn mid-run — a wiped pair goes to the end screen.
   (Solo player death = wipe. One dead partner = current boss rules until wipe or clear.)
4. Client: minimal end-screen/summary UI (clear/wipe, run time, damage dealt — whatever the
   server already tracks cheaply) + a ready-up affordance. Legibility over polish.
5. Persistence, ecology ticking, spawner world-sim: bypass or freeze inside a run if they
   interfere; do NOT delete them (prune-on-friction rule).

Acceptance: headless test of the run state machine transitions (ready → run → wipe → reset;
ready → run → clear → reset); a live two-player run start-to-end-screen is the human feel-test.
Full rigor (server state machine): independent review.
