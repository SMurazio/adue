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
    // CONTINUOUS: the confirmed server position as a continuous WorldVector (NOT the rounded AuthoritativeTile).
    // The "Server positions" debug overlay positions its marker from this so it tracks the true server position
    // smoothly instead of snapping tile-to-tile. Defaults to (0,0) for non-render-loop constructions (tests).
    RenderPosition AuthoritativePosition = default,
    // MOVEMENT-ACTIONS Phase B1: the REPLICATED, server-authoritative airborne height in WORLD UNITS (tiles) — the
    // REAL jump arc (design §1.4.5). Populated from the snapshot's EntityStateSnapshot.VerticalOffset for EVERY entity
    // (local + remote); 0 grounded, so the common case is the unchanged flat render. The renderer LIFTS the visual by
    // it. Phase C retired the cosmetic monster HopHeight arc in favour of this — a slime's hop is now a REAL replicated
    // Z, so its arc renders from this single field. Presentation-only — it never touches Position/AuthoritativeTile/
    // AuthoritativePosition, so targeting/harvest/combat are byte-identical.
    double VerticalOffset = 0d,
    // MONSTER-BEHAVIOR P6: the PLACEHOLDER per-type visual replicated on EntitySpawn (protocol v41). TintRgb is a packed
    // 0xRRGGBB the renderer modulates the entity's body by (0xFFFFFF = white = NO tint, the default); RenderScale
    // multiplies the visual node's size (1.0 = unchanged, the default). A monster carries its type's authored values
    // (a gnoll = brown + 1.4); every other entity (and a default-constructed test state) gets white + 1.0 → a no-op,
    // so its render is byte-identical. Presentation-only — the replicated hook real per-type models slot into later.
    uint TintRgb = 0xFFFFFFu,
    float RenderScale = 1f,
    // N (entity-collision walk anim): true when this entity is actually TRANSLATING (a coherent MOVING signal), false
    // when stopped or blocked. Computed in MmoClient.ToRenderState — REMOTE entities from the replicated Velocity
    // (~0 when blocked, tangential when sliding), the LOCAL player from the predictor's resolved velocity — NOT from
    // the per-frame render-position delta the visuals used to read. The player walk/idle visuals (PlayerVisual +
    // CatoSpriteVisual) drive off this (with a short anti-flicker hold), so a player pinned against a wall / monster /
    // another player goes IDLE just like a flat wall already does, while a walk or a slide keeps animating. Defaults
    // false so a default-constructed (test) state and any velocity-less entity read idle. Presentation-only.
    bool Moving = false)
{
    // True when this entity carries public HP (a dummy or another player). Resources/trees replicate 0/0,
    // so the overhead bar is hidden for them.
    public bool HasHealth => MaxHealth > 0;

    // Current/max as a [0,1] fill ratio for the bar. 0 when there is no HP (avoids a divide-by-zero).
    public float HealthFraction => MaxHealth > 0 ? System.Math.Clamp((float)Health / MaxHealth, 0f, 1f) : 0f;
}
