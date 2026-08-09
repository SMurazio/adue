# S — P2 pairing: auto-pair + duo-card reveal (replaces /pair for the demo)

From the Fable adversarial review (2026-08-09, `docs/duo-p2-demo-plan.md` workstream "B-pairing"):
`/pair <name>` reads as cringe because it asks a player to SELECT the only possible partner (a fake
choice). For the 2-player in-person LAN demo, pairing should not be an input at all. HOST-AUTHORITATIVE
server work (two-session pairing + the ready gate) — gets a review; the reveal is client UI (feel-gated).

## Server (config-flagged "demo" mode — a new ServerOptions flag; surface the exact wiring as a fork)
1. **Auto-pair on join.** When a player joins and exactly ONE other unpaired player is online, form the
   pair through the existing `HandlePairCommand` internals (skip name resolution; `SendPairStatus` both
   ways already replicates). Disconnect already breaks the pair (`BreakPair`, `GameServer.cs:~966`), so
   the same join rule self-heals on reconnect.
2. **Guard the solo-start race** (same flag). `RunEngine` currently solo-starts an UNPAIRED ready
   immediately (RunEngine.cs:~24) — so if P1 mashes ready before P2's client connects, a solo run
   starts and the operator must intervene in the first minute. With the flag on, an unpaired ready is
   refused with "Waiting for your partner to join." (no solo-start). Flag OFF = today's behaviour
   (solo-start + `/pair`), so dev/headless testing is unchanged.
3. `/pair` / `/unpair` survive as dev commands regardless of the flag.

## Client
4. **Kill the "type /pair" prompt.** `OnboardingCoach.PairingPromptText` → an unpaired player (only
   realistically pre-join now) shows "Waiting for your partner…". Drop the lobby "(/pair <name> to run
   as a duo)" line (`MmoClientRoot.cs:~2933`); keep the "2/2 ready" line.
5. **Duo-card reveal.** On pair formation, a one-shot ~2-3s card: the *a due* framing + both player
   names ("A & B — a due"). Use a text/placeholder for the a2 logo mark (art is still pending). Pairing
   becomes a fact the game CELEBRATES, not a petition typed. VISUAL/feel is a human feel-test — flag it.

## Guardrails / forks
- Surface the flag wiring as a FORK: the new ServerOptions field + how the demo launch enables it
  (e.g. start-duo passes it, or a demo default) vs. dev/headless keeping it off.
- NO new protocol messages expected (reuse PairStatus + the existing pair internals). If you need a
  wire change, STOP and surface it.
- Extract any client hint-selection change into the pure `OnboardingCoach` + unit-test it.

## Acceptance
- Headless (RunLoopSession/ClearSpawners style): with the flag ON, two clients that join are auto-paired
  (both see PairStatus.Paired) with no `/pair`; an unpaired ready is refused "Waiting for your partner
  to join" (no run starts); reconnect re-pairs. With the flag OFF, unchanged (solo-start + `/pair`).
- `OnboardingCoach` no longer emits the /pair prompt (unit-tested); godot-build compiles; godot-run smoke clean.
- Run-checks green; delete this file in the landing commit. HUMAN feel-test owed for the duo-card reveal.
