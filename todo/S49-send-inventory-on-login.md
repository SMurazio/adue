# S49 — Send the player's inventory on login (display empty after relogin)

Severity: should-fix (core-loop correctness/UX). Play-test: after relogin the inventory **display is empty**
even though the server loaded the persisted inventory. Root cause: `SendInventoryUpdate` is only called on
a successful **harvest** (`GameServer` ~line 1025). Login loads the inventory into the spawned entity
(`new Inventory(_itemRegistry, items)`, ~line 365) but never sends it to the client, so the panel has
nothing to show until the next harvest delta arrives.

## What
After a successful login spawns the entity with its loaded inventory (and `LoginResult` is sent), send the
owning client a **full inventory snapshot**: `SendInventoryUpdate(session, <all current stacks>)` —
every non-empty `ItemStack` with its authoritative quantity. The existing `InventoryUpdateMessage` +
client merge handler already populate the panel from a stack list, so a one-shot "all stacks" message on
login is a full refresh for a fresh client panel.

- Do it for BOTH login paths (fresh spawn and account-takeover handoff) — whichever inventory the entity
  ends up with is the one to send.
- Empty inventory on login is fine (send an empty/own-stacks message or skip — but ensure the client panel
  reflects "empty", not a stale value; for a fresh client it's already empty, so skipping when empty is OK).
- Reliable-ordered (same as the harvest update). Owner-only; never AOI-replicated.

## Files (server only)
- `src/Mmo.Server/Runtime/GameServer.cs` — send the full inventory after login/spawn (both paths).

## Tests
- An integration test: a character with persisted items logs in and receives an `InventoryUpdate`
  carrying those stacks (before any harvest). Covers the fresh-login path; ideally the takeover path too.
- Existing harvest/inventory/persistence tests still pass.

## Acceptance
- On login, the client's inventory panel shows the persisted contents immediately (verify via the client
  after a harvest → relogin round-trip). `run-checks.cmd` green. No protocol change (reuses
  `InventoryUpdateMessage`). Server-only. Do NOT commit — Orchestrator reviews.
