# N — CLI/agent input seam for client-side repro (kept from the CLI-play exploration)

The useful slice that survived the CLI-play review: let an agent (Claude) or a script drive the Godot
client's inputs headlessly — to REPRODUCE client-side combat/input/prediction/render bugs and capture
combat-load perf traces without a human at the keyboard. No "feel" claim: the channel reads state,
not the screen, so feel judgement stays human.

## Scope (deliberately minimal)
- **One generic verb** on the existing localhost debug control channel:
  `inject_input {action, pressed | held_ms}`, mapped to Godot's existing input-action map. Covers
  every current/future ability with zero per-ability maintenance and exercises the REAL input path a
  keyboard uses. (Semantic verbs like `client_fusion` were rejected — they'd rot with the kit and
  test a path no player runs.)
- Honest claim it may make: "deterministic input playback to reproduce client-side input/prediction/
  render bugs and capture combat-load perf traces" (e.g. `N-client-swingroot-freeze` when it next
  needs agent repro). NOT "automated feel testing", NOT a regression gate.

## Server-side combat regression (separable, do-anytime)
If/when combat-LOGIC regression coverage is wanted, extend the headless test `RunClient`
(`RunLoopSessionIntegrationTests`) with duo-ability packets — runs in run-checks, no Godot flakiness.
This is where automated combat regression belongs (not the Godot client), and it is already growing
organically as the boss-encounter/session headless tests expand.

## Gating
- The `inject_input` client verb: build ON FRICTION (the first client-side combat bug that actually
  needs agent repro), and after P2 — it competes with nothing on the P2 critical path.
- The server-side test-`RunClient` extension: do-anytime, if/when combat regression coverage is wanted.
- A scripted practice-dummy (an onboarding rehearsal aid — stands where told, fires a metronome
  crossing shot to practice against) is OK as a possible post-P2 addition; not needed now.
