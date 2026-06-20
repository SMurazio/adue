using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// S96: the "Cato" character — a 2D-in-3D billboard built from PNG frame sequences (idle 10 frames, walk 17
// frames), rendered with an AnimatedSprite3D. Swapped in for a Player (local + remote) when the F5 "Cato sprite
// (player)" toggle is on, mirroring the S73 DebugFacingBox alternate-Player pattern. Plays idle at rest and walk
// while moving (movement detected from the interpolated render-position delta, exactly like PlayerVisual), and
// flips horizontally by facing per the user's side-view rule (E/NE/SE normal, W/NW/SW flipped, N/S keep last —
// see CatoFacingFlip in Mmo.Client.Core, the pure unit-tested seam).
//
// Sorting follows SpriteVisual: alpha-scissor (cutout) cutout writes depth so the sprite occludes / is occluded
// by the 3D models cleanly instead of alpha-blending on top of them. Billboard FixedY keeps it upright facing
// the camera. Pooled separately (VisualArchetype.CatoSprite) so a parked Cato is only reused for another Cato.
//
// The 30-ish frames load ONCE into a shared static SpriteFrames on first Cato spawn (a build with no Cato never
// pays the load); a missing frame leaves _frames null and the factory falls back to the box (the visual is never
// constructed). Until Godot imports the PNGs (.import sidecars), GD.Load returns null -> box; a later launch works.
public sealed partial class CatoSpriteVisual : EntityVisual
{
    private const string IdlePathFormat = "res://content/sprites/cato/idle/Cato_Idle_{0}.png";
    private const string WalkPathFormat = "res://content/sprites/cato/walk/Cato_Walk_{0}.png";
    private const int IdleFrameCount = 10;
    private const int WalkFrameCount = 17;

    private const string AnimIdle = "idle";
    private const string AnimWalk = "walk";

    // World size of one texture pixel. The art is 512px tall; PixelSize 0.0033 ≈ 1.7 tiles tall — a character
    // reading a bit taller than a tile. First-guess; human eyeballs it. TUNABLE.
    private const float SpritePixelSize = 0.0033f;

    // Lift the centered sprite so its feet sit on the ground plane (y=0). For a ~1.7-tile-tall centered sprite
    // the pivot is at the middle, so ~0.85 up puts the bottom edge on the floor. Recompute if PixelSize changes.
    // TUNABLE.
    private const float GroundOffset = 0.85f;

    // Animation playback rates (frames per second). TUNABLE.
    private const float IdleFps = 8f;
    private const float WalkFps = 12f;

    // Label sits just above the sprite. TUNABLE.
    private const float CatoLabelHeight = 2.0f;

    // Billboard mode: Y-billboard keeps the sprite upright (rotates only around the vertical axis to face the
    // camera) rather than tilting flat to the orthographic view — right for a 2.5D character. TUNABLE.
    private const BaseMaterial3D.BillboardModeEnum Billboard = BaseMaterial3D.BillboardModeEnum.FixedY;

    // Alpha-scissor (cutout) threshold — drop the fully-transparent border while keeping soft edges. TUNABLE.
    private const float AlphaScissorThreshold = 0.5f;

    // Keep the walk loop playing this long after the last detected positional change, bridging the brief idle
    // gap between confirmed tile steps so the loop doesn't stutter on/off. Mirrors PlayerVisual. TUNABLE.
    private const double WalkHoldSeconds = 0.2d;

    // A tile step is ~1 unit; treat per-frame displacement above this (squared) as "moving". Mirrors PlayerVisual.
    private const double MovingEpsilonSquared = 0.0000004d;

    // Loaded once on first Cato spawn so a build with no Cato never pays the ~27-texture load. A missing frame
    // leaves _frames null and the factory falls back to the box (the visual is never constructed in that case).
    private static SpriteFrames? _frames;
    private static bool _loadAttempted;
    private static bool _loadFailed;

    private AnimatedSprite3D? _sprite;
    private Vector3 _lastPosition;
    private double _movingUntilSeconds;
    private bool _walking;
    private bool _lastFlipH;

    protected override float LabelHeight => CatoLabelHeight;

    protected override bool TracksLabelHeight => true;

    // Build the shared SpriteFrames once (idle 10 + walk 17, both looping). Returns null if ANY frame fails to
    // load (not yet imported / missing), so the factory falls back to the box. Logged a single time.
    public static SpriteFrames? LoadFrames()
    {
        if (_loadFailed)
        {
            return null;
        }

        if (_loadAttempted)
        {
            return _frames;
        }

        _loadAttempted = true;

        var frames = new SpriteFrames();
        if (!BuildAnimation(frames, AnimIdle, IdlePathFormat, IdleFrameCount, IdleFps) ||
            !BuildAnimation(frames, AnimWalk, WalkPathFormat, WalkFrameCount, WalkFps))
        {
            _loadFailed = true;
            GD.PushWarning("S96 Cato: one or more sprite frames could not be loaded; falling back to the box " +
                           "(logged once). A relaunch after Godot imports content/sprites/cato should work.");
            return null;
        }

        _frames = frames;
        return _frames;
    }

    // Populate one looping animation on the shared SpriteFrames. SpriteFrames seeds a "default" animation; we
    // add our named ones. Returns false (so the whole load fails) if any texture is missing.
    private static bool BuildAnimation(SpriteFrames frames, string anim, string pathFormat, int count, float fps)
    {
        frames.AddAnimation(anim);
        frames.SetAnimationLoop(anim, true);
        frames.SetAnimationSpeed(anim, fps);

        for (var i = 1; i <= count; i++)
        {
            var texture = GD.Load<Texture2D>(string.Format(pathFormat, i));
            if (texture is null)
            {
                return false;
            }

            frames.AddFrame(anim, texture);
        }

        return true;
    }

    protected override void BuildChildren()
    {
        var frames = LoadFrames();
        if (frames is null)
        {
            return;
        }

        _sprite = new AnimatedSprite3D
        {
            Name = "Cato",
            SpriteFrames = frames,
            Animation = AnimIdle,
            PixelSize = SpritePixelSize,
            Position = new Vector3(0f, GroundOffset, 0f),
            Billboard = Billboard,
            // Explicit sorting like SpriteVisual: alpha-scissor (cutout) writes depth so the sprite occludes /
            // is occluded by the 3D models correctly instead of alpha-blending on top of them.
            AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
            AlphaScissorThreshold = AlphaScissorThreshold,
            Shaded = false,
            NoDepthTest = false
        };
        AddChild(_sprite);
    }

    protected override void OnAcquire(EntityRenderState state)
    {
        _lastPosition = ToWorld(state.Position);
        _movingUntilSeconds = 0d;
        _walking = false;
        _lastFlipH = CatoFacingFlip.Resolve(state.Facing, lastFlipH: false);
        if (_sprite is not null)
        {
            _sprite.FlipH = _lastFlipH;
            _sprite.Play(AnimIdle);
        }
    }

    protected override void OnReset()
    {
        // Re-bind to a different player: drop back to idle so the reused sprite doesn't keep walking. The next
        // Acquire reseeds the latch + flip.
        _walking = false;
        _sprite?.Play(AnimIdle);
    }

    protected override void OnUpdate(EntityRenderState state, double now)
    {
        if (_sprite is null)
        {
            return;
        }

        // Detect movement from the interpolated render position (the same position that drives the wrapper),
        // with a short hold to bridge the idle gap between confirmed steps — identical to PlayerVisual.
        var position = ToWorld(state.Position);
        var moved = position.DistanceSquaredTo(_lastPosition) > MovingEpsilonSquared;
        _lastPosition = position;
        if (moved)
        {
            _movingUntilSeconds = now + WalkHoldSeconds;
        }

        DriveAnimation(now <= _movingUntilSeconds);

        // Flip by facing (predicted facing already baked into state.Facing by Core for the local player). Only
        // horizontal facings change the latch; vertical-only facings + idle keep the last flip.
        _lastFlipH = CatoFacingFlip.Resolve(state.Facing, _lastFlipH);
        _sprite.FlipH = _lastFlipH;
    }

    private void DriveAnimation(bool moving)
    {
        if (_sprite is null || _walking == moving)
        {
            return;
        }

        _walking = moving;
        _sprite.Play(moving ? AnimWalk : AnimIdle);
    }
}
