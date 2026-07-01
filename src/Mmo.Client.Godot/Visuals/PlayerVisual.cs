using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// A player avatar: the Cato cat model (Tripo FBX) driven by a state machine over its four clips —
//   idle  (idle_251105_remap)      : standing still
//   walk  (walk_normal_m_remap)    : the MOVING signal is true at walk pace
//   run   (move_run_m_remap)       : wired for a FUTURE sprint (dormant today — nothing sets sprinting yet)
//   attack(heavy-attack1-…)        : the kick, a ONE-SHOT fired by TriggerAttack() when the LOCAL player swings
// Clips are matched by keyword (the FBX/scn hides the exact names on disk; they're logged at load). idle/walk/run
// loop; the attack plays once then locomotion resumes. Facing tracks the entity's 8-way Facing plus the free-aim
// continuous-yaw override. Everything reads from the computed EntityRenderState — no game logic here.
//
// Build is pool-aware: the skinned model + AnimationTree are built ONCE in BuildChildren and reused across
// Acquire/Reset; only the per-entity latch (anim state, attack timer) resets.
public sealed partial class PlayerVisual : EntityVisual
{
    // Cato cat model (FBX + T_Cato.jpg imported into the same folder), substituting the old rig. Godot 4.6/4.7
    // imports FBX natively (ufbx); its AnimationPlayer clips are resolved by keyword below. If it imports grey the
    // embedded texture didn't assign — ApplyCatoTexture forces T_Cato.jpg as the albedo.
    private const string ModelPath = "res://content/characters/Cato.fbx";

    // TUNABLE — FIRST GUESS for the cat (native size unknown until imported; asset packs vary wildly). Adjust once
    // it's on screen.
    public const float ModelScale = 1.6f;

    // The cat's authored front is 90° off Godot's -Z forward: at offset 0 it faced North (up) while moving East
    // (right); -90 made it 180° (backward), so +90 lands its front on the travel direction (tuned in-client).
    private const float ForwardOffsetDegrees = 90f;

    // Vertical offset so the feet sit on the ground plane (y=0). Most rigs author the origin at the feet.
    private const float ModelYOffset = 0f;

    // Keep the walk loop playing this long after the MOVING signal last went false, bridging brief false gaps so the
    // loop doesn't flicker on/off. TUNABLE.
    private const double WalkHoldSeconds = 0.2d;

    private const string AnimStateIdle = "Idle";
    private const string AnimStateWalk = "Walk";
    private const string AnimStateRun = "Run";
    private const string AnimStateAttack = "Attack";

    // Cross-fade time (s) on state transitions so the rig blends instead of snapping. TUNABLE.
    private const float AnimCrossFadeSeconds = 0.13f;

    // Fallback attack length (s) if the clip resolves but its length can't be read, so the one-shot still ends.
    private const double AttackFallbackSeconds = 0.6d;

    // Loaded once on first player spawn so a build with no players never pays the load. A failed load leaves
    // _model null and the factory falls back to the box (the visual is never constructed in that case).
    private static PackedScene? _modelScene;
    private static bool _loadAttempted;
    private static bool _loadFailed;

    private Node3D? _model;
    private AnimationNodeStateMachinePlayback? _stateMachine;
    private string? _currentAnimState;
    private double _movingUntilSeconds;

    // Attack (kick) one-shot: TriggerAttack sets _pendingAttack; the driver travels to Attack and blocks locomotion
    // until _attackPlayingUntilSeconds so the swing plays fully, then resumes idle/walk.
    private bool _pendingAttack;
    private double _attackPlayingUntilSeconds;
    private bool _hasAttackClip;
    private double _attackClipLength = AttackFallbackSeconds;

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
        // The Tripo cat ships its texture EMBEDDED in the FBX (a .fbm), which Godot imports grey — force T_Cato.jpg
        // as the albedo so the model is textured.
        ApplyCatoTexture(model);

        var animationPlayer = FindAnimationPlayer(model);
        if (animationPlayer is not null)
        {
            // DIAG (Cato integration): dump the ACTUAL clip names to the Godot client log (compressed on disk).
            GD.Print($"[Cato anims] clips = [{string.Join(", ", animationPlayer.GetAnimationList())}]");
        }

        // Resolve the four clips by keyword (idle/walk/run loop; attack is a one-shot).
        var idleClip = ResolveClip(animationPlayer, loop: true, "idle");
        var walkClip = ResolveClip(animationPlayer, loop: true, "walk");
        var runClip = ResolveClip(animationPlayer, loop: true, "run");
        var attackClip = ResolveClip(animationPlayer, loop: false, "attack", "kick");

        _hasAttackClip = attackClip is not null;
        if (attackClip is not null && animationPlayer is not null)
        {
            var anim = animationPlayer.GetAnimation(attackClip);
            if (anim is not null && anim.Length > 0d)
            {
                _attackClipLength = anim.Length;
            }
        }

        _stateMachine = BuildAnimationTree(model, animationPlayer, idleClip, walkClip, runClip, attackClip);
    }

    protected override void OnAcquire(EntityRenderState state)
    {
        _movingUntilSeconds = 0d;
        _pendingAttack = false;
        _attackPlayingUntilSeconds = 0d;
        // Leave the latch UNSET so the first OnUpdate actually Travels into the locomotion state (Idle when still)
        // and PLAYS it. The state machine does NOT auto-play its first node, so pre-latching to Idle left the rig
        // frozen in its bind/T-pose at idle (walk/attack worked only because they were an explicit Travel).
        _currentAnimState = null;
        ApplyFacing(state.Facing);
    }

    protected override void OnReset()
    {
        // Re-bind to a different player: drop back to Idle so the reused rig doesn't keep walking/attacking. The
        // next Acquire reseeds the latch.
        _pendingAttack = false;
        _attackPlayingUntilSeconds = 0d;
        _stateMachine?.Travel(AnimStateIdle);
        _currentAnimState = _stateMachine is null ? null : AnimStateIdle;
    }

    protected override void OnUpdate(EntityRenderState state, double now)
    {
        // Attack (kick) one-shot: on a fresh trigger, travel to Attack and block locomotion until the clip finishes
        // so the swing plays fully. Facing still tracks the aim during the kick.
        if (_pendingAttack)
        {
            _pendingAttack = false;
            if (_hasAttackClip && _stateMachine is not null)
            {
                _stateMachine.Travel(AnimStateAttack);
                _currentAnimState = AnimStateAttack;
                _attackPlayingUntilSeconds = now + _attackClipLength;
            }
        }

        if (now < _attackPlayingUntilSeconds)
        {
            ApplyFacing(state.Facing);
            return;
        }

        // N (entity-collision walk anim): drive the walk/idle loop off the coherent MOVING signal Core computes
        // (the local player's predicted resolved velocity; a remote's replicated Velocity) — NOT the per-frame
        // render-position delta. KEEP the short hold so the loop doesn't flicker on brief false gaps.
        if (state.Moving)
        {
            _movingUntilSeconds = now + WalkHoldSeconds;
        }

        // Run is wired for a FUTURE sprint signal; nothing sets it today, so movement is always Walk.
        var target = now <= _movingUntilSeconds ? AnimStateWalk : AnimStateIdle;
        DriveLocomotion(target);
        ApplyFacing(state.Facing);
    }

    // Called (main thread) from MmoClientRoot.TryAttack when the LOCAL player swings: queues the kick one-shot.
    // The next OnUpdate travels to Attack and holds locomotion for the clip's length. Server stays authoritative on
    // damage — this is cosmetic. (Remote players' kicks would need a replicated attack event; not wired yet.)
    public void TriggerAttack()
    {
        _pendingAttack = true;
    }

    private void DriveLocomotion(string target)
    {
        if (_stateMachine is null || _currentAnimState == target)
        {
            return;
        }

        _stateMachine.Travel(target);
        _currentAnimState = target;
    }

    // Rotate the model so its forward axis points along the entity's 8-way Facing. Direction8 -> tile delta ->
    // world heading; yaw the model to it plus the tunable rig-forward correction.
    //
    // FREEAIM: when a continuous-yaw override is set (the local player aiming at the cursor), use THAT yaw instead of
    // the discrete facing — the avatar then looks continuously where you aim.
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

        // Godot's default model forward is -Z. A yaw θ about +Y turns -Z into (-sinθ, 0, -cosθ); solving that to
        // equal (delta.X, delta.Y) gives θ = atan2(-x, -y). N (0,-1) -> 0, E (1,0) -> -90°, S (0,1) -> 180°.
        var yaw = Mathf.Atan2(-delta.X, -delta.Y);
        _model.Rotation = new Vector3(0f, yaw + Mathf.DegToRad(ForwardOffsetDegrees), 0f);
    }

    // ---- model + texture + animation loading ------------------------------------------------------

    // Loads (once) the player model PackedScene. Failures are logged a single time; the factory then falls back to
    // the box for players rather than re-attempting/spamming the log. Returns null on failure.
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
            GD.PushWarning($"Cato: could not load player model '{ModelPath}'; falling back to capsule.");
        }

        return _modelScene;
    }

    // ---- Cato texture (embedded in the Tripo FBX; Godot leaves the mesh grey, so force T_Cato.jpg as albedo) ----
    private const string CatoTexturePath = "res://content/characters/T_Cato.jpg";
    private static Texture2D? _catoTexture;
    private static bool _catoTextureAttempted;

    // CEL SHADER: sample the albedo, then band N·L into just two tones — full-lit and a lifted shadow_floor — at a
    // FIXED magnitude that ignores the sun's 2.4 energy, so the cat neither blows out (too bright) nor goes near-black
    // (too dark). Tunables: band_threshold = where the shadow line falls; shadow_floor = how dark the shadow tone is.
    private const string CelShaderCode = @"
shader_type spatial;
render_mode specular_disabled;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
uniform float band_threshold : hint_range(0.0, 1.0) = 0.4;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.6;

void fragment() {
    ALBEDO = texture(albedo_tex, UV).rgb;
}

void light() {
    float ndotl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
    float lit = smoothstep(band_threshold - 0.03, band_threshold + 0.03, ndotl);
    float band = mix(shadow_floor, 1.0, lit);
    DIFFUSE_LIGHT += ALBEDO * band * ATTENUATION;
}
";

    private static ShaderMaterial? _catoMaterial;

    // Load T_Cato.jpg once, build the cel-shader material (albedo fed in as a uniform), and apply it as a material
    // override on every MeshInstance3D in the model. A failed texture load is warned once and leaves the model grey.
    private static void ApplyCatoTexture(Node root)
    {
        if (!_catoTextureAttempted)
        {
            _catoTextureAttempted = true;
            _catoTexture = GD.Load<Texture2D>(CatoTexturePath);
            if (_catoTexture is null)
            {
                GD.PushWarning($"Cato: could not load texture '{CatoTexturePath}'; model stays grey.");
            }
            else
            {
                _catoMaterial = new ShaderMaterial { Shader = new Shader { Code = CelShaderCode } };
                _catoMaterial.SetShaderParameter("albedo_tex", _catoTexture);
            }
        }

        if (_catoMaterial is null)
        {
            return;
        }

        ApplyMaterialRecursive(root, _catoMaterial);
    }

    private static void ApplyMaterialRecursive(Node node, Material material)
    {
        if (node is MeshInstance3D mesh)
        {
            mesh.MaterialOverride = material;
        }

        foreach (var child in node.GetChildren())
        {
            ApplyMaterialRecursive(child, material);
        }
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

    // First clip whose lowercased name CONTAINS any keyword (the Tripo names carry Armature| prefixes + _remap
    // suffixes, so exact matching is brittle). Sets it looping (idle/walk/run) or one-shot (attack). Null (no
    // match) leaves that state to a fallback clip in BuildAnimationTree.
    private static string? ResolveClip(AnimationPlayer? player, bool loop, params string[] keywords)
    {
        if (player is null)
        {
            return null;
        }

        foreach (var name in player.GetAnimationList())
        {
            var lower = name.ToLowerInvariant();
            foreach (var keyword in keywords)
            {
                if (!lower.Contains(keyword))
                {
                    continue;
                }

                var animation = player.GetAnimation(name);
                if (animation is not null)
                {
                    animation.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
                }

                return name;
            }
        }

        return null;
    }

    // Build an AnimationTree driving a four-state (Idle, Walk, Run, Attack) cross-fading state machine off the rig's
    // instanced AnimationPlayer. Run falls back to the walk clip and Attack to idle if that specific clip is missing,
    // so the states always exist. Returns the live playback so the per-frame driver can Travel(); null (logged) if
    // the player or the core idle/walk clips are missing, in which case the rig simply stands still.
    private static AnimationNodeStateMachinePlayback? BuildAnimationTree(
        Node3D model,
        AnimationPlayer? player,
        string? idleClip,
        string? walkClip,
        string? runClip,
        string? attackClip)
    {
        if (player is null || idleClip is null || walkClip is null)
        {
            GD.PushWarning("Cato: missing AnimationPlayer or idle/walk clip; player rig will not animate.");
            return null;
        }

        var stateMachine = new AnimationNodeStateMachine();
        stateMachine.AddNode(AnimStateIdle, new AnimationNodeAnimation { Animation = idleClip });
        stateMachine.AddNode(AnimStateWalk, new AnimationNodeAnimation { Animation = walkClip });
        stateMachine.AddNode(AnimStateRun, new AnimationNodeAnimation { Animation = runClip ?? walkClip });
        stateMachine.AddNode(AnimStateAttack, new AnimationNodeAnimation { Animation = attackClip ?? idleClip });

        // Direct cross-fades between all three locomotion states, plus enter/exit Attack from every locomotion state
        // (Travel walks the transition graph; direct edges keep the blend a single cross-fade).
        string[] locomotion = { AnimStateIdle, AnimStateWalk, AnimStateRun };
        foreach (var from in locomotion)
        {
            foreach (var to in locomotion)
            {
                if (from != to)
                {
                    stateMachine.AddTransition(from, to, MakeCrossFadeTransition());
                }
            }

            stateMachine.AddTransition(from, AnimStateAttack, MakeCrossFadeTransition());
            stateMachine.AddTransition(AnimStateAttack, from, MakeCrossFadeTransition());
        }

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
