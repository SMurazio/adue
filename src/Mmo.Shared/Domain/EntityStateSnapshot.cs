namespace Mmo.Shared.Domain;

// Value type (record struct): the client decodes one of these PER ENTITY PER SNAPSHOT (20-40
// snapshots/sec), and the server builds one per visible entity per tick. As a reference type this
// allocated a heap object per entity per snapshot, so GC churn scaled with the number of connected
// clients. A struct stores them inline in the snapshot list/array with no per-entity heap allocation.
public readonly record struct EntityStateSnapshot(uint NetworkId, TileCoord Tile, Direction8 Facing);
