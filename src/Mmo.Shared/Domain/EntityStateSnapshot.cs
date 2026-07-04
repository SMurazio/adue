namespace Mmo.Shared.Domain;

// Value type (record struct): the client decodes one of these PER ENTITY PER SNAPSHOT (20-40
// snapshots/sec), and the server builds one per visible entity per tick. As a reference type this
// allocated a heap object per entity per snapshot, so GC churn scaled with the number of connected
// clients. A struct stores them inline in the snapshot list/array with no per-entity heap allocation.
//
// NODE-FIELD N2 (docs/node-field-design.md D3/D4): Depleted is now ALWAYS false — harvestable nodes are no
// longer WorldEntities, so no entity kind ever sets this bit anymore (GameServer.BuildEntityState hard-codes
// Depleted: false). Node availability instead replicates via the global, index-keyed
// NodeStateMessage/NodeStateBatchMessage (never per-entity, never AOI-scoped). The field is kept — removing it
// would ripple through the codec/wire (out of N2/N3's scope, and no protocol version bump is warranted for a
// bit that already always reads false) — but no live consumer should branch on it; the client-side visuals
// that used to (BoxVisual/ModelVisual/EntityVisual/Minimap) had their Depleted-conditional branches removed in
// N3 once this went constant.
//
// COMBAT-S2A: Health + MaxHealth are the PUBLIC vitals replicated for the overhead HP bar. HP is public
// (it drives a bar anyone nearby can see); mana/stamina stay OWNER-ONLY on the Stage-1 PlayerStatsMessage
// and never ride this snapshot. They are ushort (whole HP points, 0..65535 — well above any value this
// game uses) so each adds only 2 bytes/entity to the per-entity state (4 bytes total), keeping the snapshot
// delta-friendly. MaxHealth == 0 is the canonical "this entity has no HP" marker: resources/trees (and any
// kind without CharacterStats) replicate 0/0, and the client hides the bar for them.
//
// MIGRATION (Phase 3 Pass A): the position field is now a continuous WorldVector (double X, Y) rather than a
// TileCoord. This is an internal-type seam ONLY — the codec STILL quantizes it to a tile on the wire
// (WriteEntityStates does Position.ToTileRounded(); ReadEntityStates rebuilds Position = WorldVector.FromTile),
// so the v35 bytes round-trip byte-for-byte unchanged. Pass B flips the codec to send fixed-point continuous
// positions (PositionEncoding) and bumps the protocol version. Consumers that need a grid cell derive it via
// Position.ToTileRounded() (e.g. the client's ClientEntity.Tile).
// MOVEMENT-ACTIONS Phase B1 (v38): VerticalOffset is the entity's authoritative airborne height in WORLD UNITS (tiles)
// above the ground plane — 0 grounded, >0 mid-jump (design §1.4.5). It is DEFAULTED (0) so every existing construction
// (the non-airborne common case, tests) is unchanged; the codec encodes it compactly (a presence flag + an optional
// Q12.4 ushort, so a grounded entity pays +1 byte). The renderer LIFTS the visual by it; XY/collision/AOI never read it
// (the XY/Z split, §1.4.1).
// REMOTE-WALK Phase 1 (v39): Velocity is the entity's authoritative CONTINUOUS velocity (units/sec, = unitDir ×
// SpeedUnitsPerSecond, zeroed on stop). It is DEFAULTED (Zero) so every existing construction (tests, the resting
// common case) is unchanged; the codec encodes it compactly under a combined flags byte (a "moving" presence bit +
// two signed shorts of 1/256-unit/sec velocity, only when moving). It is replicated so a remote client can dead-reckon
// the entity between sparse snapshots (Phase 2 — Phase 1 only WIRES + BUFFERS it; Sample does not extrapolate yet).
public readonly record struct EntityStateSnapshot(
    uint NetworkId,
    WorldVector Position,
    Direction8 Facing,
    bool Depleted = false,
    ushort Health = 0,
    ushort MaxHealth = 0,
    double VerticalOffset = 0d,
    WorldVector Velocity = default);
