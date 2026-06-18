# N3 — Dead `position_x` / `position_y` columns remain after tile migration

Severity: nit (low)

## Problem

Migration `db/sqlite/002_tile_positions.sql` (and the Postgres counterpart) add `tile_x` / `tile_y`
but leave the original `position_x` / `position_y` columns from `001_initial.sql` in place. They are
no longer read or written.

## Fix

Add a migration `003_drop_position_columns.sql` (SQLite + Postgres parity) that drops the dead
columns. For SQLite, confirm the installed version supports `ALTER TABLE ... DROP COLUMN` (3.35+);
if the bundled `Microsoft.Data.Sqlite` engine is older, instead leave a comment in the migration
explaining why the columns are retained, and close this task as Blocked with that note.

## Acceptance

- Fresh DB bootstrap and existing-DB upgrade both still pass the repository tests.
- `run-checks.cmd` green.
