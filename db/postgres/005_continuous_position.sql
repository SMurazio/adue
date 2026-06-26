-- CONTINUOUS MIGRATION (Phase 10): persist the continuous WorldVector position, not just the rounded tile.
-- Adds double precision pos_x/pos_y columns ALONGSIDE the existing integer tile_x/tile_y (additive + reversible —
-- tile_x/tile_y are kept so any tile-keyed query still works and an old client/tool reading them is unaffected).
-- Backfill the new columns from the existing tile so pre-migration characters load at their tile centre (no data
-- loss); from here on the save path writes the true sub-tile position and login restores it losslessly.
-- The column DEFAULT mirrors the spawn-tile default (tile_x/tile_y default 8, from migration 002) so a freshly
-- INSERTed character — which takes the tile defaults — gets its continuous position AT the spawn centre, not (0,0).
alter table characters add column if not exists pos_x double precision not null default 8;
alter table characters add column if not exists pos_y double precision not null default 8;

update characters set pos_x = tile_x, pos_y = tile_y;
