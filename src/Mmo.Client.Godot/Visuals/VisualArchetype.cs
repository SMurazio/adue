namespace Mmo.Client.Godot.Visuals;

// A client-local rendering key the factory assigns per entity. Stage 1 only: this is NOT the on-the-wire
// VisualArchetype the design plans for Stage 2 (the server doesn't send a rendering id yet); it exists so
// the EntityRenderer can pool released visuals BY archetype (a parked PlayerVisual is only ever reused for
// another player, never for a rock). The factory still derives the archetype from the current
// EntityKind + DisplayName dispatch; Stage 2 swaps that derivation for a server-sent id without touching
// the renderer or the visual classes.
public enum VisualArchetype
{
    Player,
    // S73 debug-only: a Player rendered as a box + facing arrow instead of the model rig, chosen when the F5
    // "Debug facing box" toggle is on. Pooled separately from Player so a parked debug box never reuses (or is
    // reused as) a real PlayerVisual.
    DebugFacingBox,
    // S96: a Player rendered as the "Cato" AnimatedSprite3D billboard (idle/walk PNG frames) instead of the
    // model rig, chosen when the F5 "Cato sprite (player)" toggle is on. Pooled separately from Player so a
    // parked Cato is only reused for another Cato, never mixed with a real PlayerVisual.
    CatoSprite,
    // NODE-FIELD N2/N3 (docs/node-field-design.md D3/D6): Rock/Tree archetypes REMOVED — harvestable nodes are
    // no longer WorldEntities (they render via the catalogue field's MultiMeshes, NodeFieldPainter, never
    // per-entity), so no entity is ever spawned with a DisplayName these dispatched on. Any other unknown
    // Resource-kind entity (only House/Portal remain) falls to Portal/HouseSprite/Box below as before.
    Portal,
    HouseSprite,
    // LOOT P4b: a dropped lootable corpse (EntityKind.Corpse). Rendered by BoxVisual as a small dark mound/sack on
    // the ground (distinct from a player capsule / resource box at a glance) so a kill leaves a visible, walk-up-and-
    // interact corpse. No art yet — a coloured low box. Pooled separately so a parked corpse never reuses a player.
    Corpse,
    // DUO-SKILLSHOT (exp/duo-abilities): an in-flight fusion-skillshot projectile (EntityKind.Projectile). Rendered by
    // BoxVisual's projectile path as a small bright sphere; the server's replicated tint+scale colour/size it per tier
    // (solo cyan / good amber / perfect magenta, bigger for stronger). Pooled separately so a parked projectile never
    // reuses (or is reused as) a body.
    Projectile,
    Box
}
