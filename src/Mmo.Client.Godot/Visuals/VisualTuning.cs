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
    // base stays on the floor at any scale. The S60 panel clamps to [0.1, 50].
    public float RockModelScale { get; set; } = 4f;

    // Shared label font/outline styling (constant; not panel-tunable). Kept here so every visual builds an
    // identical Label3D without duplicating the magic numbers.
    public const int LabelFontSize = 64;
    public const int LabelOutlineSize = 14;
    public static readonly Color LabelOutlineColor = new(0.02f, 0.02f, 0.02f, 1f);
}
