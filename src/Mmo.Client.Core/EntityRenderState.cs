using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

public sealed record EntityRenderState(
    uint NetworkId,
    Guid CharacterId,
    EntityKind Kind,
    string DisplayName,
    RenderPosition Position,
    TileCoord AuthoritativeTile,
    Direction8 Facing,
    bool IsLocal);
