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
    // HOP-ARC (cosmetic, monster-only): the vertical render height in WORLD UNITS (tiles) to LIFT the visual this
    // frame — a parabolic jump that peaks mid-hop and lands at 0, SYNCED to the horizontal interp of a slime's
    // sparse server hop. PURELY presentational: it never touches Position/AuthoritativeTile/AuthoritativePosition,
    // so targeting/harvest/combat are byte-identical. 0 for players, continuously-moving entities, and a resting
    // monster (the render loop adds it to the visual's Y; 0 == flat, the unchanged behaviour for every other kind).
    double HopHeight = 0d,
    // MOVEMENT-ACTIONS Phase B1: the REPLICATED, server-authoritative airborne height in WORLD UNITS (tiles) — the
    // REAL jump arc (design §1.4.5), distinct from the cosmetic monster HopHeight above (Phase C retires the hop in
    // favour of this). Populated from the snapshot's EntityStateSnapshot.VerticalOffset for EVERY entity (local +
    // remote); 0 grounded. The renderer LIFTS the visual by it the same way it lifts by HopHeight. For B1 the local
    // player's own jump rises/lands via this same path (server-confirmed — no prediction yet). Presentation-only — it
    // never touches Position/targeting, exactly like HopHeight.
    double VerticalOffset = 0d)
{
    // True when this entity carries public HP (a dummy or another player). Resources/trees replicate 0/0,
    // so the overhead bar is hidden for them.
    public bool HasHealth => MaxHealth > 0;

    // Current/max as a [0,1] fill ratio for the bar. 0 when there is no HP (avoids a divide-by-zero).
    public float HealthFraction => MaxHealth > 0 ? System.Math.Clamp((float)Health / MaxHealth, 0f, 1f) : 0f;
}
