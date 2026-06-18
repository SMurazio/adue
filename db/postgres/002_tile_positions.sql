alter table characters add column if not exists tile_x integer not null default 8;
alter table characters add column if not exists tile_y integer not null default 8;

create index if not exists ix_characters_zone_tile on characters(zone_id, tile_x, tile_y);
