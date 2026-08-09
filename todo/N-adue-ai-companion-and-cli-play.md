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

## Before building: Fable ADVERSARIAL review (per the new rule)
This is a consequential design decision (does the game have AI allies at all? does that dilute the
duo thesis or the P2 kill-test?) → it MUST pass a Fable red-team before we commit, per
`.shared/memory/design-decisions-survive-fable-adversarial-review.md`. Ask Fable to REFUTE: does a
bot ally undermine "the second player is a person you laugh with" (the P2 pass criterion)? Is piece 2
(agent-drivable combat) worth the surface area vs. the human feel-test? Where does each die?

## Gating
Design-exploration; not queue-urgent. Piece 2 (CLI/agent combat control) is the more independently
useful half and could be scoped as tooling even if the bot-ally design is deferred. Do not start the
bot-ally build until the Fable adversarial review runs and (if it survives) the P2 demo has answered
whether the duo core lands.
