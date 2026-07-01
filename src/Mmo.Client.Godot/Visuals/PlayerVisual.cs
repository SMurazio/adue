using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// A player avatar: the rigged humanoid GLB (S54) + an AnimationTree cross-fading idle<->walk (S55) + facing
// driven by the entity's 8-way Facing, including the S59 predicted-turn rotation. ALL of this is read from
// the computed EntityRenderState: Core bakes the predictor's live facing into state.Facing for the local
// entity, and "moving" is the coherent MOVING signal Core computes (N: the local player's predicted resolved
// velocity; a remote's replicated Velocity) — no game logic lives here. The behaviour is lifted from
// MmoClientRoot (N swapped the old render-delta detection for state.Moving).
//
// Build is pool-aware but conservative: re-instancing a skinned GLB + rebuilding its AnimationTree is the
// expensive part, so the model rig is built ONCE in BuildChildren and reused across Acquire/Reset; only the
// per-entity latch (position/facing/anim state) resets.
public sealed partial class PlayerVisual : EntityVisual
{
    private const string ModelPath = "res://content/characters/ProvaPersonaggioWalkLoop.glb";

    // TUNABLE. Model native height ~1.086 units (grid = 1 unit/tile); scale ~1.6 renders it ~1.74 tiles tall.
    public const float ModelScale = 1.6f;

    // TUNABLE. glTF/Godot forward is -Z; N maps to -Z (tile delta 0,-1). 180 corrects a rig that play-tested
    // facing front-to-back relative to movement.
    private const float ForwardOffsetDegrees = 180f;

    // Vertical offset so the feet sit on the ground plane (y=0). Most rigs author the origin at the feet.
    private const float ModelYOffset = 0f;

    // Keep the walk loop playing this long after the MOVING signal last went false, bridging brief false gaps so the
    // loop doesn't flicker on/off. TUNABLE.
    private const double WalkHoldSeconds = 0.2d;

    private const string AnimStateIdle = "Idle";
    private const string AnimStateWalk = "Walk";

    // Cross-fade time (s) on the Idle<->Walk transitions so the rig blends instead of snapping. TUNABLE.
    private const float AnimCrossFadeSeconds = 0.13f;

    // Loaded once on first player spawn so a build with no players never pays the load. A failed load leaves
    // _model null and the factory falls back to the box (the visual is never constructed in that case).
    private static PackedScene? _modelScene;
    private static bool _loadAttempted;
    private static bool _loadFailed;

    private Node3D? _model;
    private AnimationNodeStateMachinePlayback? _stateMachine;
    private string? _currentAnimState;
    private double _movingUntilSeconds;

    protected override bool TracksLabelHeight => true;

    // The factory only constructs a PlayerVisual when the model scene is available (LoadModelScene non-null),
    // so BuildChildren can assume a successful instantiate; a null instantiate (corrupt scene) is still
    // guarded and leaves the rig empty (player renders as a bare wrapper + label rather than crashing).
    protected override void BuildChildren()
    {
        var scene = LoadModelScene();
        if (scene is null || scene.Instantiate() is not Node3D model)
        {
            return;
        }

        model.Name = "Model";
        model.Scale = new Vector3(ModelScale, ModelScale, ModelScale);
        model.Position = new Vector3(0f, ModelYOffset, 0f);
        AddChild(model);
        _model = model;

        var animationPlayer = FindAnimationPlayer(model);
        var walkClip = ResolveWalkClip(animationPlayer);
        var idleClip = ResolveIdleClip(animationPlayer);
        _stateMachine = BuildAnimationTree(model, animationPlayer, idleClip, walkClip);
    }

    protected override void OnAcquire(EntityRenderState state)
    {
        _movingUntilSeconds = 0d;
        // The state machine auto-starts in the first-added node (Idle); seed the latch to match so the first
        // detected movement Travels to Walk and a stop Travels back to Idle.
        _currentAnimState = _stateMachine is null ? null : AnimStateIdle;
        ApplyFacing(state.Facing);
    }

    protected override void OnReset()
    {
        // Re-bind to a different player: drop back to Idle so the reused rig doesn't keep walking. The next
        // Acquire reseeds the latch.
        _stateMachine?.Travel(AnimStateIdle);
        _currentAnimState = _stateMachine is null ? null : AnimStateIdle;
    }

    protected override void OnUpdate(EntityRenderState state, double now)
    {
        // N (entity-collision walk anim): drive the walk/idle loop off the coherent MOVING signal Core computes
        // (the local player's predicted resolved velocity; a remote's replicated Velocity) — NOT the per-frame
        // render-position delta, which latched "walk" on the sub-pixel jitter left when pushing into a body. A
        // player pinned against a wall / monster / another player has Moving false → idles, exactly like a flat
        // wall already does; a walk or a slide keeps Moving true. KEEP the short hold so the loop doesn't flicker
        // on brief false gaps. Rotate the model to the entity's 8-way facing (predicted facing already baked in).
        if (state.Moving)
        {
            _movingUntilSeconds = now + WalkHoldSeconds;
        }

        DriveAnimation(now <= _movingUntilSeconds);
        ApplyFacing(state.Facing);
    }

    private void DriveAnimation(bool moving)
    {
        if (_stateMachine is null)
        {
            return;
        }

        // Latch to the target state and only Travel() on a change. When the MOVING signal has been false for the
        // hold, moving latches false, so the machine cross-fades into Idle and holds the standing pose.
        var target = moving ? AnimStateWalk : AnimStateIdle;
        if (_currentAnimState == target)
        {
            return;
        }

        _stateMachine.Travel(target);
        _currentAnimState = target;
    }

    // Rotate the model so its forward axis points along the entity's 8-way Facing. Direction8 -> tile delta
    // -> world heading (X=tileX, Z=tileY); yaw the model to it plus the tunable rig-forward correction.
    //
    // FREEAIM: when a continuous-yaw override is set (the local player aiming at the cursor), use THAT yaw instead of
    // the discrete facing — the avatar then looks continuously where you aim. The override is already in the same
    // model-forward convention (θ such that -Z maps to the aim), so it just gets the rig-forward correction added.
    private void ApplyFacing(Direction8 facing)
    {
        if (_model is null)
        {
            return;
        }

        if (ContinuousYaw is float continuousYaw)
        {
            _model.Rotation = new Vector3(0f, continuousYaw + Mathf.DegToRad(ForwardOffsetDegrees), 0f);
            return;
        }

        var delta = facing.Delta();
        if (delta.X == 0 && delta.Y == 0)
        {
            return;
        }

        // Godot's default model forward is -Z. A yaw θ about +Y turns -Z into (-sinθ, 0, -cosθ); solving that
        // to equal (delta.X, delta.Y) gives θ = atan2(-x, -y). N (0,-1) -> 0, E (1,0) -> -90°, S (0,1) -> 180°.
        var yaw = Mathf.Atan2(-delta.X, -delta.Y);
        _model.Rotation = new Vector3(0f, yaw + Mathf.DegToRad(ForwardOffsetDegrees), 0f);
    }

    // ---- model + animation loading (lifted verbatim from MmoClientRoot) ----------------------------

    // Loads (once) the player model PackedScene. Failures are logged a single time; the factory then falls
    // back to the box for players rather than re-attempting/spamming the log. Returns null on failure.
    public static PackedScene? LoadModelScene()
    {
        if (_loadFailed)
        {
            return null;
        }

        if (_loadAttempted)
        {
            return _modelScene;
        }

        _loadAttempted = true;
        _modelScene = GD.Load<PackedScene>(ModelPath);
        if (_modelScene is null)
        {
            _loadFailed = true;
            GD.PushWarning($"S54: could not load player model '{ModelPath}'; falling back to capsule.");
        }

        return _modelScene;
    }

    private static AnimationPlayer? FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer player)
        {
            return player;
        }

        foreach (var child in node.GetChildren())
        {
            if (FindAnimationPlayer(child) is AnimationPlayer found)
            {
                return found;
            }
        }

        return null;
    }

    // Robustly pick the walk clip: prefer a name that looks like a walk loop, else the first non-T-pose clip,
    // and set it to loop. Null (logged once) if there is no usable clip -> the model stands still, no crash.
    private static string? ResolveWalkClip(AnimationPlayer? player)
    {
        if (player is null)
        {
            GD.PushWarning("S54: player model has no AnimationPlayer; rig will not animate.");
            return null;
        }

        var clips = player.GetAnimationList();
        string? firstNonTPose = null;
        foreach (var name in clips)
        {
            if (IsTPoseClip(name))
            {
                continue;
            }

            firstNonTPose ??= name;
            var lower = name.ToLowerInvariant();
            if (lower.Contains("catwalk") || lower.Contains("loop") || lower.Contains("walk"))
            {
                SetClipLooping(player, name);
                return name;
            }
        }

        if (firstNonTPose is not null)
        {
            SetClipLooping(player, firstNonTPose);
            return firstNonTPose;
        }

        GD.PushWarning("S54: no non-T-pose animation found on the player model; rig will not walk.");
        return null;
    }

    // The placeholder idle is the rig's T-pose (human-OK'd). Prefer a T-pose-named clip, else the first clip.
    private static string? ResolveIdleClip(AnimationPlayer? player)
    {
        if (player is null)
        {
            return null;
        }

        var clips = player.GetAnimationList();
        string? first = null;
        foreach (var name in clips)
        {
            first ??= name;
            if (IsTPoseClip(name))
            {
                SetClipLooping(player, name);
                return name;
            }
        }

        if (first is not null)
        {
            SetClipLooping(player, first);
        }

        return first;
    }

    private static bool IsTPoseClip(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("t-pose") || lower.Contains("tpose") || lower.Contains("t_pose");
    }

    private static void SetClipLooping(AnimationPlayer player, string clipName)
    {
        var animation = player.GetAnimation(clipName);
        if (animation is not null)
        {
            animation.LoopMode = Animation.LoopModeEnum.Linear;
        }
    }

    // Build an AnimationTree driving a two-state (Idle, Walk) cross-fading state machine off the rig's
    // instanced AnimationPlayer. Returns the live playback so the per-frame driver can Travel(); null
    // (logged) if the player/clips are missing, in which case the rig simply stands still.
    private static AnimationNodeStateMachinePlayback? BuildAnimationTree(
        Node3D model, AnimationPlayer? player, string? idleClip, string? walkClip)
    {
        if (player is null || idleClip is null || walkClip is null)
        {
            GD.PushWarning("S55: missing AnimationPlayer or idle/walk clip; player rig will not animate.");
            return null;
        }

        var idleNode = new AnimationNodeAnimation { Animation = idleClip };
        var walkNode = new AnimationNodeAnimation { Animation = walkClip };

        var stateMachine = new AnimationNodeStateMachine();
        stateMachine.AddNode(AnimStateIdle, idleNode);
        stateMachine.AddNode(AnimStateWalk, walkNode);
        stateMachine.AddTransition(AnimStateIdle, AnimStateWalk, MakeCrossFadeTransition());
        stateMachine.AddTransition(AnimStateWalk, AnimStateIdle, MakeCrossFadeTransition());

        var tree = new AnimationTree
        {
            Name = "AnimTree",
            TreeRoot = stateMachine
        };
        model.AddChild(tree);
        tree.AnimPlayer = tree.GetPathTo(player);
        tree.Active = true;

        return tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
    }

    private static AnimationNodeStateMachineTransition MakeCrossFadeTransition()
    {
        return new AnimationNodeStateMachineTransition
        {
            XfadeTime = AnimCrossFadeSeconds,
            SwitchMode = AnimationNodeStateMachineTransition.SwitchModeEnum.Immediate,
            AdvanceMode = AnimationNodeStateMachineTransition.AdvanceModeEnum.Disabled
        };
    }
}
