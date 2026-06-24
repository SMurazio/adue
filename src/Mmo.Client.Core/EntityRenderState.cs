using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// Value type (record struct) on purpose: ToRenderState() builds one of these per entity per frame
// in the client render loop. At hundreds-to-thousands of fps a reference type here churned Gen0 and
// caused frequent brief GC-pause micro-stutters; a struct keeps it allocation-free.
// COMBAT-S2A: Health/MaxHealth are the PUBLIC HP replicated on the snapshot, threaded through to the
// overhead red HP bar. MaxHealth == 0 means "no HP" (resources/stat-less entities) and the visual hides
// the bar. HasHealth is the convenience the renderer reads.
public readonly record struct EntityRenderState(
    uint NetworkId,
    Guid CharacterId,
    EntityKind Kind,
    string DisplayName,
    RenderPosition Position,
    TileCoord AuthoritativeTile,
    Direction8 Facing,
    bool IsLocal,
    bool Depleted = false,
    ushort Health = 0,
    ushort MaxHealth = 0,
    // MONSTER-HOP: the vertical bounce offset (world units) for a hopping monster's render — 0 for everything
    // else (players/NPCs/resources glide flat). The Godot wrapper adds this to the visual's world Y so the slime
    // arcs up-and-down as it hops tile-to-tile. Purely cosmetic: never affects AuthoritativeTile or targeting.
    double HopHeight = 0d)
{
    // True when this entity carries public HP (a dummy or another player). Resources/trees replicate 0/0,
    // so the overhead bar is hidden for them.
    public bool HasHealth => MaxHealth > 0;

    // Current/max as a [0,1] fill ratio for the bar. 0 when there is no HP (avoids a divide-by-zero).
    public float HealthFraction => MaxHealth > 0 ? System.Math.Clamp((float)Health / MaxHealth, 0f, 1f) : 0f;
}
