# Session and model economy (user directive, 2026-08-09)

Binding cost-hygiene rules for every orchestrator session in this repo:

- **Main loop model = OPUS by default** (user prefers Opus 4.8 when pinnable). **Fable is used
  ONLY for design requests or when the user explicitly asks for it** (e.g. "get fable to review
  X"). The session model is a user command (`/model`) — if a session is on Fable for
  non-design work, say so and ask the user to switch.
- **All subagents = `model: "opus"`** — implementers, scouts, and reviewers, including
  high-risk reviews. No auto-escalation to Fable.
- **One work-arc per session, then close.** Idle gaps past the prompt-cache TTL force a full
  context re-write at premium rates on the next turn — an idle-riddled long session cost ~$70
  in cache writes alone on 2026-08-09. The handoff between sessions is this memory dir +
  `todo/` + `docs/` — not a long-lived chat.
- Keep giant tool outputs out of context (tail the output, prefer files_with_matches /
  head-limited searches); don't restart mid-arc while background agents are in flight.
