using Godot;

namespace Mmo.Client.Godot.Visuals;

// A 2D billboard in the 2.5D world: the magic house (casa_magica.png), the first sprite-in-3D proof. Sprite3D
// mixed with 3D models needs deliberate depth/alpha/render-order handling (we hit z-fighting before with the
// NoDepthTest labels), so this class sets its sorting EXPLICITLY via the tunable consts below rather than
// leaning on defaults. First-guess values — human eyeballs the result on relaunch.
//
// Decorative: there is no server "House" resource today, so this never appears unless content adds a
// "House"-named entity; it is wired so it would render sensibly when it does.
public sealed partial class SpriteVisual : EntityVisual
{
    private const string TexturePath = "res://content/sprites/casa_magica.png";

    // World size of one texture pixel. The art is ~512px tall; PixelSize 0.006 ≈ 3 tiles tall — a building
    // reads bigger than a character. TUNABLE.
    private const float SpritePixelSize = 0.006f;

    // Lift the sprite so its bottom edge sits on the ground plane. Centered Sprite3D pivots at the middle, so
    // a ~3-tile sprite needs ~1.5 up. Recomputed if PixelSize changes. TUNABLE.
    private const float GroundOffset = 1.5f;

    // Label sits just above the building. TUNABLE.
    private const float SpriteLabelHeight = 3.4f;

    // Billboard mode: Y-billboard keeps the house upright (rotates only around the vertical axis to face the
    // camera) instead of tilting flat to the orthographic view — right for a 2.5D building. TUNABLE.
    private const BaseMaterial3D.BillboardModeEnum Billboard = BaseMaterial3D.BillboardModeEnum.FixedY;

    // Alpha-scissor (cutout) rather than alpha-blend so the transparent PNG border doesn't sort/blend against
    // the 3D models or fight the depth buffer — the house writes depth and reads cleanly against rocks/trees.
    // Threshold tuned to drop the fully-transparent border while keeping soft edges. TUNABLE.
    private const float AlphaScissorThreshold = 0.5f;

    private static Texture2D? _texture;
    private static bool _loadAttempted;

    private Sprite3D? _sprite;

    protected override float LabelHeight => SpriteLabelHeight;

    public static Texture2D? LoadTexture()
    {
        if (_loadAttempted)
        {
            return _texture;
        }

        _loadAttempted = true;
        _texture = GD.Load<Texture2D>(TexturePath);
        if (_texture is null)
        {
            GD.PushWarning($"S61 house: could not load sprite '{TexturePath}'; falling back to the box.");
        }

        return _texture;
    }

    protected override void BuildChildren()
    {
        var texture = LoadTexture();
        if (texture is null)
        {
            return;
        }

        _sprite = new Sprite3D
        {
            Name = "Sprite",
            Texture = texture,
            PixelSize = SpritePixelSize,
            Position = new Vector3(0f, GroundOffset, 0f),
            Billboard = Billboard,
            // Explicit sorting: alpha-scissor (cutout) writes depth so the house occludes / is occluded by 3D
            // models correctly instead of alpha-blending on top of them.
            AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
            AlphaScissorThreshold = AlphaScissorThreshold,
            Shaded = false,
            NoDepthTest = false
        };
        AddChild(_sprite);
    }
}
