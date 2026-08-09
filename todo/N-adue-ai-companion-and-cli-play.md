# N — AI companion bot + agent/CLI-drivable play (design idea; needs a Fable adversarial review)

User idea (2026-08-09): a **bot ally that plays alongside a human** so SOLO players can experience the
duo game — and, as a dual benefit, a **client the agent (or a CLI) can drive** to actually *play* the
game (not just launch/telemetry it), which would let Claude feel-test combat itself instead of relying
on the human for every live check.

Two intertwined but separable pieces:
1. **Duo bot ally** — a server- or client-side AI that fills the second slot: aims, uses the duo kit
   (fusion crossing, sync-window shield, tether, midpoint detonate), reads the boss's contested
   channels. This is a design + AI-behavior task, NOT trivial — the whole game is *relationship-as-
   input*, so a bot that can't participate in the Perfect version is a different (lesser) game. It
   also risks undercutting the couch/online-duo social core if it becomes the default.
2. **Agent/CLI-drivable play** — extend the existing debug control channel (localhost `client-control`
   MCP already does move/telemetry) toward *combat* actions (attack, dodge, duo abilities, ready) so a
   script/agent can run a full run headlessly. Overlaps the existing console-client + control-channel
   infra; lower-risk, high leverage for automated feel/regression testing.

## Red-team verdict on PIECE 2 (CLI-play), 2026-08-09: SURVIVES-NARROWED
Fable adversarial review (per `.shared/memory/design-decisions-survive-fable-adversarial-review.md`)
KILLED both headline claims and reduced the idea:
- **"Claude feel-tests combat" — DROPPED (category error).** The channel reads state JSON, not the
  screen, so it bypasses the render layer where Law-7 legibility and render==hit honesty live; and an
  MCP-round-trip agent has no human reaction time, so sync-window (T1) signal is zero-information
  about humans. Laughter (the P2 gate) has no computable proxy. Feel stays HUMAN, per the contract.
- **"Automated combat regression" — BELONGS SERVER-SIDE, not here.** The existing headless harnesses
  (e.g. RunLoopSessionIntegrationTests drives the full sim over loopback) already cover the sim;
  extend the test `RunClient` with duo-ability packets for combat regression — it runs in run-checks,
  no Godot flakiness. A Godot-client combat test can't run in run-checks and inherits process/frame/
  TCP flake (tests the harness, not the game).
- **Semantic combat verbs (`client_fusion` etc.) — REJECTED.** They freeze the pre-P2, churning duo
  kit into three parallel surfaces (protocol + flags + MCP tools) and rot with every tuning/ability;
  they also bypass the real input path, so they'd test a path no player runs.
- **Entanglement:** "play a full run" is either trivial-but-useless (flails, dies) or IS the bot-ally
  problem (piece 1) in disguise (needs a competent second slot). Only the trivial fragment is
  separable. Do NOT let "we'll want CLI play" launder bot-ally work into existence pre-P2.

**Surviving scope (build ON FRICTION, after P2, not now):** ONE generic `inject_input {action,
pressed|held_ms}` verb mapped to Godot's existing input-action map — covers every current/future
ability with zero per-ability maintenance, exercises the REAL input path. Honest claim it may make:
"deterministic input playback to reproduce client-side input/prediction/render bugs and capture
combat-load perf traces" (e.g. the live `N-client-swingroot-freeze` when it next needs agent repro).
Everything else in piece 2 is dropped.

## Gating
- **Piece 2 (CLI-play):** narrowed as above; build on friction (first client-combat bug needing
  agent repro), after P2. Server-side combat regression (extend the test RunClient) is the separable,
  do-it-anytime slice if/when combat regression coverage is wanted.
- **Piece 1 (bot ally):** NOT reviewed yet; a post-P2, post-evidence design question — if P2 passes
  with humans it's a real question, if P2 fails neither piece matters. Do not start pre-P2.
