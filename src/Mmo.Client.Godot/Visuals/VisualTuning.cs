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

    // Shared label font/outline styling (constant; not panel-tunable). Kept here so every visual builds an
    // identical Label3D without duplicating the magic numbers.
    public const int LabelFontSize = 64;
    public const int LabelOutlineSize = 14;
    public static readonly Color LabelOutlineColor = new(0.02f, 0.02f, 0.02f, 1f);
}
