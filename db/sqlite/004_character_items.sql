create table if not exists character_items (
    character_id text not null references characters(id) on delete cascade,
    template_key text not null,
    quantity integer not null,
    primary key (character_id, template_key)
);

create index if not exists ix_character_items_character on character_items(character_id);
