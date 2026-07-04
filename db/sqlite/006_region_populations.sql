-- ECOLOGY E3 (docs/ecology-v1-design.md D8, S8 E3): first WORLD-state (non-character) persistence table. One row
-- per authored region x monster-type: its live stock/pressure and the server tick it was last saved at. A restart
-- must not heal the world, so this table is the durable copy of EcologyState's in-memory cells (region_id/type_id
-- are the SAME string ids EcologyRegistry/EcologyState key by; the pair is the natural primary key, no surrogate
-- id needed since a region never has two rows for the same type). No FK to another table: regions/types are
-- AUTHORED content (Content/ecology.json), not DB rows, so a stale row for a region/type the manifest no longer
-- authors is a content-drift case the loader ignores (logged), not a referential-integrity violation.
create table if not exists region_populations (
    region_id text not null,
    type_id text not null,
    stock real not null,
    pressure real not null,
    updated_at_tick integer not null,
    primary key (region_id, type_id)
);
