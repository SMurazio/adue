---
name: production-ready-intent
description: "Production-ready means keeping the right seams open and decisions reversible, not building every production feature up front."
metadata:
  node_type: memory
  type: feedback
---

The project began as a learning project, but the user wants the work to remain as production-ready
and transferable as possible. That does not mean building every production feature now. It means
making the important one-way decisions carefully and keeping two-way decisions behind clean seams.

Apply this framing:

- One-way decisions to treat carefully: server authority, shared versioned protocol contracts in
  `Mmo.Shared`, persistence behind repository interfaces, state-sync over determinism, and the
  shape of future auth/security and durability.
- Two-way decisions to defer behind seams: client prediction, grid AOI, delta snapshots, world size,
  elevation/layers, NPCs, combat, process splitting, and database migration from SQLite to Postgres.
- For deferred work, keep the hook or boundary visible so the later change is contained.
- Flag any choice that closes a door or bakes in a throwaway assumption.

Never equate production-ready with building it all now. Rejected techniques are usually
genre-mismatches or scope mismatches, not "useless" ideas.
