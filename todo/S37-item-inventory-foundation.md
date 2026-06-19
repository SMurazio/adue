# S37 — Item & inventory foundation (domain + persistence)

Severity: should-fix. First gameplay-loop foundation (gather/craft). See
`docs/gather-and-inventory-design.md` and `docs/feature-roadmap.md` Phase 4. Persistence is stable, so
this is on-roadmap (Phase 3 gated inventory until now).

## Scope (this task = data model + persistence ONLY)

No interaction verb, no resource nodes, no client UI, no crafting. Just the item/inventory model, a
registry, a server-authoritative inventory service, and durable write-behind persistence. **S38** adds
the harvest verb on top; **S39** adds client UI.

## What

1. **Item definitions (templates) + registry** — `src/Mmo.Shared/Domain`:
   - An item definition: stable `ItemType` key/id (enum or string key — pick stable + serialization-
     friendly), display name, `MaxStack`, category. Immutable.
   - A code **registry** mapping key → definition. Seed a tiny set: e.g. `Wood`, `Stone`, `Fiber`
     (stackable, MaxStack 99). Adding items must be a registry entry, not code branches.
2. **Item instance / stack** — compact `(templateKey, quantity)`. Include a **reserved, optional
   unique-instance-id field** (e.g. `Guid? InstanceId = null`) for future uniques — present but unused
   by stackables. Do not build unique-item logic now.
3. **Inventory model + service** (server-authoritative) — a per-character inventory (collection of
   stacks) with `TryAdd`/`Remove` that respect stacking + `MaxStack` (and an optional simple capacity;
   none is acceptable for now). All mutations go through this one path. Pure, unit-testable.
4. **Hold + load inventory in server memory** — attach the inventory to the durable character/world
   entity (NOT derived from the session each tick). Load on login alongside the character; keep it as
   server-memory truth.
5. **Persistence (normalized, write-behind)** — mirror the existing tile pattern
   (`SqliteCharacterRepository.SaveTileAsync`, `MigrationRunner`):
   - New table `character_items` (`character_id` FK, `template_key`, `quantity`), via a migration
     (clean-bootstrap AND existing-db upgrade must both work — see Phase 3 / existing migration tests).
   - Repository methods to load a character's stacks and to persist them write-behind (upsert changed
     stacks / delete emptied ones) — no DB writes in the tick hot path; flush on the existing
     checkpoint/`FlushAsync` boundary.

## Files (server + shared; no client, no protocol)
- `src/Mmo.Shared/Domain/` — item definition, registry, item stack.
- `src/Mmo.Server/Runtime/` — inventory service; wire inventory onto the character/world entity + load.
- `src/Mmo.Server/Data/` — `character_items` table migration + repository load/save (follow
  `SqliteCharacterRepository` + `SqliteMigrationRunner`).
- Tests: inventory add/stack/remove logic (unit); repository round-trip save→load with a **temp SQLite
  db**; migration clean-bootstrap + existing-db upgrade.

## Acceptance
- Inventory survives logout→login (round-trip persistence test): add items, flush, reload, items intact.
- Stacking honored (`MaxStack`), remove works, capacity (if any) enforced; all via the one service.
- Migration works on a fresh DB and upgrades an existing DB without data loss.
- `run-checks.cmd` green. No protocol/client changes. Do NOT commit — Orchestrator reviews.
