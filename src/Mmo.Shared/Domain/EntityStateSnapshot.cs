namespace Mmo.Shared.Domain;

public sealed record EntityStateSnapshot(uint NetworkId, TileCoord Tile, Direction8 Facing);
