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
    Rock,
    Tree,
    Portal,
    HouseSprite,
    Box
}
