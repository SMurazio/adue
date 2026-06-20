using Godot;

namespace Mmo.Client.Godot.Visuals;

// Live-tunable presentation parameters shared across entity visuals. These are the values the S60 admin
// tuning panel (F4) mutates at runtime: the name-label pixel size + the player-label height, and the rock
// model scale. They were fields/consts on MmoClientRoot before Stage 1; they now live here so the visual
// classes (which read them) and the EntityRenderer (which pushes label changes onto live visuals) share a
// single source of truth, while MmoClientRoot keeps owning the panel UI.
//
// Presentation-only: these are render knobs, not game state. Nothing here feeds back into Mmo.Client.Core.
public sealed class VisualTuning
{
    // Constant on-screen text size (FixedSize labels). Tuned for crisp glyphs without ballooning. The S60
    // panel clamps to [0.0001, 0.02]; default mirrors the pre-refactor field.
    public float LabelPixelSize { get; set; } = 0.0005f;

    // Player name-label height above the wrapper. Derived from the player model scale before the refactor
    // (PlayerVisual.ModelScale * 1.4); the panel clamps to [0, 10].
    public float PlayerLabelHeight { get; set; } = PlayerVisual.ModelScale * 1.4f;

    // One scale applied to ALL rock GLB variants. Each variant's ground offset is multiplied by this so the
    // base stays on the floor at any scale. The S65 F5 panel clamps to [0.1, 50].
    public float RockModelScale { get; set; } = 4f;

    // S65: per-archetype model scales, live-tunable from the F5 visual panel alongside the rock scale.
    //   * TreeModelScale  — the alberello Tree GLB. Default 1.2 matches ModelVisual's prior fixed tree scale so
    //     the look is unchanged until tuned; ModelVisual multiplies its tree ground offset by this too.
    //   * PlantModelScale — the BoxVisual "Plant" resource box (the current placeholder). Default 1.0 leaves the
    //     box at its native 0.7³ so nothing changes until tuned; BoxVisual applies it as a uniform body scale.
    // Both clamp to [0.1, 50] in the panel.
    public float TreeModelScale { get; set; } = 1.2f;
    public float PlantModelScale { get; set; } = 1.0f;

    // S73 diagnostic: when true, every Player-kind entity (local + remote) renders as a plain box with a small
    // facing arrow at its base instead of the character model — makes facing + per-step movement legible while
    // debugging movement feel. Flipped live by the F5 "Debug facing box" checkbox; default off = zero render
    // change. The factory reads this when choosing a player's archetype; the EntityRenderer rebuilds existing
    // player visuals on toggle so the swap is immediate.
    public bool DebugFacingBox { get; set; }

    // S96: when true, every Player-kind entity (local + remote) renders as the "Cato" AnimatedSprite3D billboard
    // (idle/walk PNG frames, side-view directional flip) instead of the character model. Flipped live by the F5
    // "Cato sprite (player)" checkbox; default off = zero render change. The factory reads this when choosing a
    // player's archetype (precedence: DebugFacingBox > CatoSprite > Player); the EntityRenderer rebuilds existing
    // player visuals on toggle so the swap is immediate.
    public bool DebugCatoSprite { get; set; }

    // S99: live-tunable placement for the Cato AnimatedSprite3D billboard, mirroring the per-archetype model
    // scales above. The art is a 512px frame with the cat low-center and a wand extending up-right (so the frame
    // CENTRE sits above the cat body). These three knobs are pushed onto live Cato visuals from the F5 panel
    // without a respawn (CatoSpriteVisual.ApplyPlacement), so the human can eyeball the fit while the client runs.
    //   * CatoPixelSize — world size of one texture pixel. S101 lowered the default 0.0066 → 0.0058 (~3 tiles tall;
    //     the 2× S96 guess read a touch big). Still live-tunable.
    //   * CatoYOffset   — vertical placement of the sprite pivot above the ground plane (lifts the cat onto the
    //     tile; the centred pivot is above the cat body, so this is less than half the sprite height).
    //   * CatoXOffset   — horizontal nudge (the wand extends right, biasing the frame centre, so a small offset can
    //     re-centre the cat body over the tile). Default 0 = no horizontal shift. The F5 panel clamps these.
    //   * CatoDepth (S101) — toward-camera depth in world units along the ground-projected camera direction
    //     (1,0,1)/√2 (the fixed iso camera sits at (24,28,24)). Positive = toward the camera. Default 0 = no shift.
    public float CatoPixelSize { get; set; } = 0.0058f;
    public float CatoYOffset { get; set; } = 1.0f;
    public float CatoXOffset { get; set; }
    public float CatoDepth { get; set; }

    // S79 diagnostic: when true, the local player's PREDICTED tile and CONFIRMED/server tile are each painted
    // as a flat ground marker (predicted = green, confirmed = magenta) at the tile centre, refreshed every
    // frame. They overlap when prediction and server agree and separate visibly under lag, so the human can
    // SEE the residual movement divergence in real time. Flipped live by the F5 "Prediction tiles" checkbox;
    // default off = zero render change (the markers are hidden and not repositioned). MmoClientRoot owns the
    // two marker nodes and reads this flag each _Process frame; nothing in the visual hierarchy reads it.
    public bool DebugPredictionTiles { get; set; }

    // Shared label font/outline styling (constant; not panel-tunable). Kept here so every visual builds an
    // identical Label3D without duplicating the magic numbers.
    public const int LabelFontSize = 64;
    public const int LabelOutlineSize = 14;
    public static readonly Color LabelOutlineColor = new(0.02f, 0.02f, 0.02f, 1f);
}
