create table if not exists schema_migrations (
    id text primary key,
    applied_at text not null default (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

create table if not exists accounts (
    id text primary key,
    dev_name text not null unique,
    created_at text not null default (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at text not null default (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

create table if not exists characters (
    id text primary key,
    account_id text not null references accounts(id) on delete cascade,
    display_name text not null,
    zone_id text not null default 'sandbox',
    position_x real not null default 0,
    position_y real not null default 0,
    created_at text not null default (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at text not null default (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    unique (account_id, display_name)
);

create index if not exists ix_characters_zone_id on characters(zone_id);
