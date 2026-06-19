namespace Mmo.Shared.Domain;

// Which per-entity fields a delta-coded snapshot row actually carries (S47b, protocol v16). An entity
// is only serialized when at least one field changed vs the viewer's ACKED baseline, and only the
// changed fields ride the wire — a changed-field bitmask plus those fields. The decoder reconstructs
// absolute state by applying the carried fields against the entity's CURRENT value (which, thanks to
// S47a's highest-contiguous ack, equals the acked baseline the server encoded against).
//
// Position is mutually exclusive between Absolute and Step: a baseline/AOI-entry/non-unit move carries
// ABSOLUTE coordinates (establishes/replaces the position); the common single-tile move carries a
// STEP direction (one byte) applied to the current tile. Neither flag set ⇒ the position did not change.
[Flags]
public enum EntityStateChange : byte
{
    None = 0,

    // The row carries absolute int16 x,y. Used on a baseline (complete/AOI-entry) snapshot and on any
    // non-unit position change (teleport/spawn) where a single step delta cannot express the move.
    PositionAbsolute = 1 << 0,

    // The row carries a single Direction8 step byte; the new tile is currentTile + Delta(direction). Only
    // valid against a baseline the client provably has (the acked baseline) — the cumulative-delta safety
    // that S47a guarantees.
    PositionStep = 1 << 1,

    // The row carries a facing byte (facing changed vs baseline).
    Facing = 1 << 2,

    // The row carries a depleted bool byte (depleted changed vs baseline, or this is a baseline row).
    Depleted = 1 << 3,
}

// One decoded entity row from a world snapshot. Value type (record struct): the client decodes one per
// entity per snapshot (20-40/sec) and the server builds one per visible delta entity per tick, so a heap
// object per entity would scale GC with the client count — a struct stores them inline with no per-entity
// allocation.
//
// `Changes` says which fields are authoritative on this row. `Tile` is absolute when
// `PositionAbsolute` is set; when `PositionStep` is set, `Tile` is unused and `Step` carries the
// direction to apply against the entity's current tile. Fields whose flag is clear are "unchanged" and
// the client must keep its current value for them.
//
// The legacy positional constructor (NetworkId, Tile, Facing, Depleted) produces an ABSOLUTE, all-fields
// row — exactly a baseline/complete snapshot row. Existing call sites and tests that build absolute rows
// are unaffected; only the new delta path sets `Changes`/`Step`.
//
// Depleted is an additive per-entity flag for resource nodes (harvested ⇒ true, respawned ⇒ false);
// always false for players/NPCs, which keeps the snapshot path uniform.
public readonly record struct EntityStateSnapshot(
    uint NetworkId,
    TileCoord Tile,
    Direction8 Facing,
    bool Depleted = false,
    EntityStateChange Changes = EntityStateChange.PositionAbsolute | EntityStateChange.Facing | EntityStateChange.Depleted,
    Direction8 Step = Direction8.N)
{
    public bool HasAbsolutePosition => (Changes & EntityStateChange.PositionAbsolute) != 0;

    public bool HasStepPosition => (Changes & EntityStateChange.PositionStep) != 0;

    public bool HasFacing => (Changes & EntityStateChange.Facing) != 0;

    public bool HasDepleted => (Changes & EntityStateChange.Depleted) != 0;

    // A complete (baseline/AOI-entry) row: absolute position + every field present. This is what the
    // server emits on a complete snapshot and what every legacy absolute construction produces.
    public static EntityStateSnapshot Absolute(uint networkId, TileCoord tile, Direction8 facing, bool depleted = false)
    {
        return new EntityStateSnapshot(
            networkId,
            tile,
            facing,
            depleted,
            EntityStateChange.PositionAbsolute | EntityStateChange.Facing | EntityStateChange.Depleted,
            Direction8.N);
    }
}
