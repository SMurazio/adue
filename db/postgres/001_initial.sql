create extension if not exists pgcrypto;

create table if not exists schema_migrations (
    id text primary key,
    applied_at timestamptz not null default now()
);

create table if not exists accounts (
    id uuid primary key default gen_random_uuid(),
    dev_name text not null unique,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists characters (
    id uuid primary key default gen_random_uuid(),
    account_id uuid not null references accounts(id) on delete cascade,
    display_name text not null,
    zone_id text not null default 'sandbox',
    position_x real not null default 0,
    position_y real not null default 0,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    unique (account_id, display_name)
);

create index if not exists ix_characters_zone_id on characters(zone_id);
