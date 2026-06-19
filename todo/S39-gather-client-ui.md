# S39 — Gather client UI (render inventory + drive the harvest verb in the Godot client)

Severity: should-fix. Makes the S37/S38 gather loop **playable/visible**. Client-only — consumes the
protocol already shipped in S38 (v13); no server/protocol changes. See `docs/gather-and-inventory-design.md`.

## Why

S37 (inventory + persistence) and S38 (resource nodes + Interact/harvest + InventoryUpdate, protocol
v13) are complete and verified server-side, but nothing renders them. The Godot client today shows
players/entities but neither resource nodes' state, the harvest action, nor the inventory. This task
closes the loop so a human can walk up to a node, harvest, and watch their inventory fill.

## Scope (Godot client only; NO server/protocol changes)

1. **Render resource nodes** — they already arrive as `EntityKind.Resource` entities in the snapshot
   with the `Depleted` bit. Give them a distinct visual from players, and reflect Available vs Depleted
   (e.g. greyed/hidden when depleted, restored on respawn). Drive purely off the snapshot `Depleted`
   flag the client already decodes.
2. **Send `InteractRequest`** — on a harvest input (pick a binding, e.g. a key or click) targeting the
   nearest adjacent resource node (or the selected entity), send `InteractRequestMessage(targetNetworkId)`.
   Respect the server's authority — do not predict the result.
3. **Handle `InteractResult`** — surface success/failure feedback to the player (e.g. a brief toast/log
   line using the `Reason` code: `too_far`, `depleted`, `inventory_full`, `rate_limited`, …).
4. **Render inventory from `InventoryUpdate`** — maintain a client-side inventory view updated by the
   owner-only `InventoryUpdateMessage` (each stack's Quantity is the new authoritative total; 0 = empty).
   Show it in a simple panel/HUD (item key/name + count). Use `ItemRegistry` display names if convenient.

## Files (client only)
- `src/Mmo.Client.Godot/` — node rendering + depleted visual; input → InteractRequest; InteractResult
  feedback; inventory panel.
- `src/Mmo.Client.Core/` only if the inventory-view state belongs in the shared client core (keep it
  server-agnostic; the web debug client may reuse it but a Godot-only view is acceptable for this task).

## Acceptance
- In the running Godot client, a player can approach a resource node, harvest it (adjacency enforced by
  the server), see the node deplete then respawn, and watch the harvested item appear/increment in the
  inventory panel — all driven by server messages, no client-side prediction of results.
- Failure reasons (too far, depleted, inventory full) produce visible feedback.
- `godot-build.cmd` green; `run-checks.cmd` green for any Core changes; verified via a visual check
  (use the `mmo-client-control` MCP and/or `start-godot-visual-check`). Do NOT commit — Orchestrator reviews.

## Notes
- Verification is partly **visual** (Godot) — isolate from headless server work.
- Keep it debug-grade UI, not final art/UX (roadmap Phase 5 is debug tooling, not the shipped game UI).
- Independent of S36a/S36b (terrain) — can land in either order.
