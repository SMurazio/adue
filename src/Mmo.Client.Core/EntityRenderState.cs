using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Value type (record struct) on purpose: ToRenderState() builds one of these per entity per frame
// in the client render loop. At hundreds-to-thousands of fps a reference type here churned Gen0 and
// caused frequent brief GC-pause micro-stutters; a struct keeps it allocation-free.
public readonly record struct EntityRenderState(
    uint NetworkId,
    Guid CharacterId,
    EntityKind Kind,
    string DisplayName,
    RenderPosition Position,
    TileCoord AuthoritativeTile,
    Direction8 Facing,
    bool IsLocal);
