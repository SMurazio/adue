using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

public sealed record ReplicatedEntity(
    uint NetworkId,
    Guid CharacterId,
    EntityKind Kind,
    string DisplayName,
    TileCoord Tile,
    Direction8 Facing,
    bool IsLocal);
