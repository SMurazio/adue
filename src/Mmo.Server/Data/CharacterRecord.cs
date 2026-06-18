using Mmo.Shared.Domain;

namespace Mmo.Server.Data;

public sealed record CharacterRecord(
    Guid AccountId,
    Guid CharacterId,
    string DisplayName,
    string ZoneId,
    TileCoord Tile);
