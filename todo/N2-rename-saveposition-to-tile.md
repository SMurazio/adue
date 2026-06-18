# N2 — `SavePositionAsync` name is stale now that it persists tiles

Severity: nit

## Problem

`ICharacterRepository.SavePositionAsync(Guid, TileCoord, …)` now saves a tile coordinate, but the
name still says "position" (`src/Mmo.Server/Data/ICharacterRepository.cs`,
`SqliteCharacterRepository.cs:34`, `CharacterRepository.cs` Postgres impl, caller at
`GameServer.cs:802`).

## Fix

Rename to `SaveTileAsync` (or `SavePlayerTileAsync`) across the interface, both implementations
(SQLite + Postgres), and the caller. Pure rename, no behavior change.

## Acceptance

- Builds; all references updated; `run-checks.cmd` green.
