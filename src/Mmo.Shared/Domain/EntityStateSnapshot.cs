namespace Mmo.Shared.Domain;

// Value type (record struct): the client decodes one of these PER ENTITY PER SNAPSHOT (20-40
// snapshots/sec), and the server builds one per visible entity per tick. As a reference type this
// allocated a heap object per entity per snapshot, so GC churn scaled with the number of connected
// clients. A struct stores them inline in the snapshot list/array with no per-entity heap allocation.
//
// Depleted is an additive per-entity state flag for resource nodes: a harvested node replicates as
// Depleted=true to clients that can see it (AOI-gated, like every other field here) and flips back to
// false when it respawns. It is meaningless (always false) for players and other entity kinds, which
// keeps the AOI snapshot path uniform — node availability is just another bit of replicated state.
//
// COMBAT-S2A: Health + MaxHealth are the PUBLIC vitals replicated for the overhead HP bar. HP is public
// (it drives a bar anyone nearby can see); mana/stamina stay OWNER-ONLY on the Stage-1 PlayerStatsMessage
// and never ride this snapshot. They are ushort (whole HP points, 0..65535 — well above any value this
// game uses) so each adds only 2 bytes/entity to the per-entity state (4 bytes total), keeping the snapshot
// delta-friendly. MaxHealth == 0 is the canonical "this entity has no HP" marker: resources/trees (and any
// kind without CharacterStats) replicate 0/0, and the client hides the bar for them.
public readonly record struct EntityStateSnapshot(
    uint NetworkId,
    TileCoord Tile,
    Direction8 Facing,
    bool Depleted = false,
    ushort Health = 0,
    ushort MaxHealth = 0);
