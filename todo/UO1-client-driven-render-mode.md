# UO1 — UO-style client-driven movement as a new render mode

PRODUCTION work on `review/tile-step-todo` (NOT the spike branch). Add Ultima-Online's proven model — **instant
client prediction + the server FOLLOWS the client's per-step requests (accept/reject)** — as a new, opt-in
`MovementRenderMode.UoClientDriven`. Default stays `CosmeticLead`; UO mode is inert until selected. Fully
revertable.

**Read `docs/uo-client-driven-mode-plan.md` first — it is the full grounded design** (the UO mode is mostly
ASSEMBLY: it reuses the predictor's prediction + the S103 commit-step path + the `StepSequence`/`RecipientStepSeq`
reconcile + the cooldown anti-cheat; the only new surface is a per-step commit emission and a one-bit
"client-driven" session flag so the server stops auto-pacing).

## Do it as the 5 STAGED, REVERTABLE COMMITS in the plan (one discrete commit each)
1. **Protocol** — `MessageType.MovementMode` + `MovementModeMessage(bool ClientDriven)` + codec + a
   `ProtocolCodecTests` round-trip. Version bump (server+client ship together). No behavior.
2. **Server honors the flag** — `ClientSession.ClientDrivenMovement` + handle `MovementModeMessage` + a one-line
   `continue` in `GameServer.StepHeldMovementIntents` for client-driven sessions (prevents double-stepping).
   Server test: a flagged session with a held MoveIntent does NOT advance via the tick loop.
3. **Predictor surfaces accepted-step directions** — `LocalPlayerPredictor.Tick` reports the direction(s) of
   steps accepted this call (caller-supplied buffer for the multi-step catch-up case). Pure, unit-tested.
4. **Client UO mode + per-step emission** — `MovementRenderMode.UoClientDriven=3`; a `UsesPredictor(mode)` helper
   routing the 4 sites through the predictor; per accepted step send `StepCommitRequestMessage(++_moveSequence,
   dir)` ReliableOrdered; send `MovementModeMessage` on entering/leaving the mode. Default stays `CosmeticLead`.
   Client tests: N commits for N predicted steps; mode message on toggle.
5. **Selectable + docs** — add `UoClientDriven` to `RenderModeCycle` (MmoClientRoot.cs:943) so the F6 render-mode
   button cycles to it; a line in `docs/movement-input-model.md`.

Honor the plan's constraints: keep `CommitAcceptFraction=0.5` unchanged (do NOT weaken it — flag any reject-snap
for follow-up tuning); every send (MoveIntent + each commit) takes a fresh `++_moveSequence` and stays
ReliableOrdered (shared cursor); send `MovementModeMessage` ReliableOrdered + re-send on (re)login/respawn.

## Gates (touches protocol + server + client + predictor + Godot)
- `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` (hardened `--no-incremental`) green AND
  `.\.shared\skills\mmo-dev\scripts\godot-build.cmd` clean.
- Stop a `Mmo.Shared.dll`-locking session via `stop-mmo.cmd` if needed; note it. You cannot run Godot — the human
  does the live test.
- **If your shell is denied** (it has happened repeatedly), say so EXPLICITLY and do NOT claim green — the
  Orchestrator runs both gates. Still write complete, compilable code for all 5 commits.

## Standing rules
- Five discrete revertable commits (one per stage), each referencing this task; delete this file in the LAST
  commit. **Safe Local Execution** (scripts only). No new findings become silent extra changes — file new todos.

## Acceptance / how the human tests it (put in the review-request)
The F6 render-mode button cycles to **UoClientDriven**; selecting it makes the local player client-driven (server
follows per-step commits). Relaunch → F6 set Net latency 100ms → cycle to UoClientDriven → walk/spam directions →
should feel UO-smooth (instant, no snap except on genuine blocks). Both gates green. Default unchanged.
