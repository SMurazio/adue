using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// Base class for every entity rendered in the world. An EntityVisual IS the interp-driven wrapper Node3D
// (its Position is set from the EntityRenderState each frame, exactly as MmoClientRoot did before Stage 1);
// subclasses add their per-type child (a GLB rig, a static model, a sprite, a box) and override the bits
// that differ.
//
// Lifecycle is pool-ready: Acquire / Reset / Release rather than raw create/free, so the EntityRenderer can
// park a released visual and reuse it instead of QueueFree-ing skinned rigs on every AOI-boundary churn.
//   * the ctor builds the (archetype-specific) child nodes ONCE — they are reusable;
//   * Acquire binds the visual to a freshly-spawned entity (id, name, initial state);
//   * Reset returns it to a clean state for reuse (re-bind to a different entity);
//   * Release detaches it from the scene tree and clears per-entity references for parking.
//
// Presentation-only: a visual READS the computed EntityRenderState (position/facing/depleted) and holds no
// game logic — interpolation/prediction stay in Mmo.Client.Core.
public abstract partial class EntityVisual : Node3D
{
    private Label3D? _label;

    // COMBAT-S2A: the overhead red HP bar — a SINGLE billboarded, unshaded quad whose red fill grows left->right
    // in UV space up to current/max (the rest a dark background). UV-space fill is billboard-invariant, so the bar
    // stays centred over the entity and aligned under the iso camera (the earlier two-quad version drifted: the
    // fill used a world-space X-offset + non-uniform scale under billboard). Built lazily like the label; shown
    // only for entities with public HP (dummies + other players), hidden for resources and (by default) the local
    // player, which has the HUD bars.
    private MeshInstance3D? _healthBar;
    private ShaderMaterial? _healthBarMaterial;

    // Bar geometry (world units at the visual's base scale). FullWidth = 100%-HP width, Height = thickness.
    private const float HealthBarFullWidth = 1.0f;
    private const float HealthBarHeight = 0.12f;

    // The HP-bar shader: a vertex billboard (always faces the camera) + a UV-based left->right fill by `fraction`
    // (red) over a dark background. Unshaded, no cull, no depth test so it reads on top like the name label.
    private static readonly Shader HealthBarShader = new()
    {
        Code = @"shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled;
uniform float fraction : hint_range(0.0, 1.0) = 1.0;
uniform vec3 fill_color : source_color = vec3(0.85, 0.12, 0.12);
uniform vec3 bg_color : source_color = vec3(0.05, 0.05, 0.05);
void vertex() {
    // Billboard: keep the quad's world position, take orientation from the camera so it always faces us.
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
}
void fragment() {
    ALBEDO = (UV.x <= fraction) ? fill_color : bg_color;
}
",
    };

    // FREEAIM: a render-only continuous facing yaw (radians about +Y) that, when set, OVERRIDES the discrete
    // movement facing for this visual's body. Local-only and not replicated — the root sets it on the local player
    // each frame so the avatar looks toward the cursor; null restores the normal 8-way facing. Subclasses read it in
    // their facing application (PlayerVisual); others ignore it.
    private float? _continuousYaw;

    // Set/clear the continuous facing override. Idempotent and cheap; the next OnUpdate applies it.
    public void SetContinuousYaw(float yawRadians) => _continuousYaw = yawRadians;
    public void ClearContinuousYaw() => _continuousYaw = null;

    // The current continuous-yaw override (null = use discrete facing). Read by subclasses that support it.
    protected float? ContinuousYaw => _continuousYaw;

    protected VisualTuning Tuning { get; private set; } = null!;

    // The NetworkId this visual is currently bound to (0 when parked in the pool).
    public uint NetworkId { get; private set; }

    // The archetype key the factory chose for this visual; the renderer pools by this key so a released
    // visual is only ever reused for the same archetype.
    public VisualArchetype Archetype { get; private set; }

    // Height of the name label above the wrapper. Subclasses override (player labels track the tunable
    // player height; resources park their label above the tallest variant).
    protected virtual float LabelHeight => Tuning.PlayerLabelHeight;

    // One-time wiring of the shared tuning + the archetype this visual was built for. Separate from the ctor
    // so subclasses keep a parameterless ctor and the factory controls binding order.
    internal void Configure(VisualArchetype archetype, VisualTuning tuning)
    {
        Archetype = archetype;
        Tuning = tuning;
        BuildChildren();
    }

    // Build the reusable per-type child nodes (model/sprite/box). Called exactly once, from Configure.
    protected abstract void BuildChildren();

    // Bind this (possibly pooled) visual to a freshly-spawned entity: set the wrapper name, position, the
    // name label, and let subclasses initialise their per-entity state (facing, variant, depleted, ...).
    public void Acquire(EntityRenderState state)
    {
        NetworkId = state.NetworkId;
        Name = $"Entity_{state.NetworkId}";
        Position = ToWorld(state.Position);
        Visible = true;
        EnsureLabel();
        if (_label is not null)
        {
            _label.Text = state.DisplayName;
            _label.Position = new Vector3(0f, LabelHeight, 0f);
            _label.PixelSize = Tuning.LabelPixelSize;
            _label.Visible = true;
        }

        EnsureHealthBar();
        UpdateHealthBar(state);

        OnAcquire(state);
    }

    // Return the visual to a clean reusable state and re-bind it to a (different) entity. Default is the same
    // path as Acquire; subclasses override OnAcquire/OnReset for any per-type teardown that differs.
    public void Reset(EntityRenderState state)
    {
        OnReset();
        Acquire(state);
    }

    // Detach from the scene tree and clear per-entity references so the visual can be parked in the pool.
    // The reusable child nodes (model/label) stay built and attached to THIS wrapper; only the wrapper is
    // unparented from the entity root.
    public void Release()
    {
        NetworkId = 0;
        Visible = false;
        // FREEAIM: drop any continuous-facing override so a pooled visual reused for a different entity doesn't
        // inherit the previous (local-player) aim yaw.
        _continuousYaw = null;
        GetParent()?.RemoveChild(this);
        OnRelease();
    }

    // Per-frame update from the computed render state. Base drives wrapper position + label availability;
    // subclasses extend (facing, animation, depleted hide) via OnUpdate.
    public void UpdateFrom(EntityRenderState state, double now)
    {
        // HOP-ARC (cosmetic, Phase-8 Option B): every kind tracks its horizontal render position; MONSTERS also get
        // a vertical lift (state.HopHeight, world units) so a slime visibly ARCS up-and-over its server hop and
        // lands flat. HopHeight is 0 for every other kind and for a resting monster, so the common case is the
        // unchanged flat glide. Presentation-only — Position.Y here never affects the authoritative tile/targeting.
        //
        // MOVEMENT-ACTIONS Phase B1: ADD the REPLICATED real airborne height (state.VerticalOffset, world units) on
        // the SAME 1:1 unit->screen-height mapping the cosmetic hop uses — so a real ballistic jump rises/lands for
        // EVERY kind (the local player's own server-confirmed jump included). 0 grounded, so the common case is
        // unchanged. The two add: today the cosmetic hop is monster-only and the replicated offset is 0 for monsters
        // (no action triggers a monster jump until Phase C), so they never overlap; Phase C retires HopHeight.
        Position = ToWorld(state.Position) + new Vector3(0f, (float)(state.HopHeight + state.VerticalOffset), 0f);
        if (_label is not null)
        {
            SetLabelText(state.DisplayName);
            // Name label tracks availability: hide it when a resource is harvested (the model/box already
            // hides) so a mined node leaves no floating label, and show it again on respawn.
            _label.Visible = state.Kind != EntityKind.Resource || !state.Depleted;
        }

        UpdateHealthBar(state);

        OnUpdate(state, now);
    }

    // Push live label-tuning (pixel size + player height) onto this visual without a respawn. Called by the
    // EntityRenderer when the F4 panel applies. Only player labels move vertically; resource labels keep
    // their per-kind height, so the height push is gated on the subclass opting in via TracksLabelHeight.
    public void ApplyLabelTuning()
    {
        if (_label is null)
        {
            return;
        }

        _label.PixelSize = Tuning.LabelPixelSize;
        if (TracksLabelHeight)
        {
            _label.Position = new Vector3(0f, LabelHeight, 0f);
        }
    }

    // True when the visual's label height follows a live-tunable value (players). Resources fix their label
    // height at spawn, so they ignore the height push.
    protected virtual bool TracksLabelHeight => false;

    // S65: push the live per-archetype model scale (Tuning.RockModelScale / TreeModelScale / PlantModelScale)
    // onto this already-spawned visual so an F5-panel apply is visible WITHOUT a respawn. Default no-op (a
    // player capsule / sprite has no tunable scale); ModelVisual + BoxVisual override it.
    public virtual void ApplyModelScale() { }

    // S99: push the live Cato placement (Tuning.CatoPixelSize / CatoXOffset / CatoYOffset) onto this already-
    // spawned visual so an F5-panel apply moves/resizes the sprite WITHOUT a respawn. Default no-op; only
    // CatoSpriteVisual overrides it.
    public virtual void ApplyCatoPlacement() { }

    // ---- subclass extension points -----------------------------------------------------------------
    protected virtual void OnAcquire(EntityRenderState state) { }
    protected virtual void OnReset() { }
    protected virtual void OnRelease() { }
    protected virtual void OnUpdate(EntityRenderState state, double now) { }

    // ---- shared helpers ----------------------------------------------------------------------------
    protected static Vector3 ToWorld(RenderPosition position)
    {
        return new Vector3((float)position.X, 0f, (float)position.Y);
    }

    // Builds the shared name label (S57 styling): small, outlined, render-on-top (NoDepthTest so it never
    // z-fights with or hides behind the body), FixedSize for a constant readable on-screen size at distance.
    // Lazily created on first Acquire and reused thereafter.
    private void EnsureLabel()
    {
        if (_label is not null)
        {
            return;
        }

        _label = new Label3D
        {
            Name = "Name",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FixedSize = true,
            PixelSize = Tuning.LabelPixelSize,
            FontSize = VisualTuning.LabelFontSize,
            OutlineSize = VisualTuning.LabelOutlineSize,
            OutlineModulate = VisualTuning.LabelOutlineColor,
            NoDepthTest = true
        };
        AddChild(_label);
    }

    private void SetLabelText(string value)
    {
        if (_label is not null && _label.Text != value)
        {
            _label.Text = value;
        }
    }

    // ---- COMBAT-S2A overhead HP bar ----------------------------------------------------------------

    // Where the HP bar sits above the wrapper — a touch below the name label so the two don't overlap.
    private float HealthBarHeightValue => LabelHeight - 0.35f;

    // Lazily builds the single-quad bar (a QuadMesh with the HP-bar shader), parented at HealthBarHeight above
    // the wrapper. The shader billboards it AND fills it by `fraction`, so there are no child quads to misalign.
    private void EnsureHealthBar()
    {
        if (_healthBar is not null)
        {
            return;
        }

        _healthBarMaterial = new ShaderMaterial { Shader = HealthBarShader };
        _healthBar = new MeshInstance3D
        {
            Name = "HealthBar",
            Mesh = new QuadMesh { Size = new Vector2(HealthBarFullWidth, HealthBarHeight) },
            MaterialOverride = _healthBarMaterial,
            Position = new Vector3(0f, HealthBarHeightValue, 0f),
        };
        AddChild(_healthBar);
    }

    // Shows the bar only for entities that carry public HP and aren't the local player; sets the shader fill to
    // current/max (the shader fills left->right in UV space, so no node scaling/offset is needed).
    private void UpdateHealthBar(EntityRenderState state)
    {
        if (_healthBar is null || _healthBarMaterial is null)
        {
            return;
        }

        // Local player uses the HUD bars (S1), so it skips its own overhead bar. Resources/stat-less entities
        // replicate MaxHealth==0 (HasHealth=false) and are hidden too.
        var show = state.HasHealth && !state.IsLocal;
        _healthBar.Visible = show;
        if (!show)
        {
            return;
        }

        _healthBarMaterial.SetShaderParameter("fraction", Mathf.Clamp(state.HealthFraction, 0f, 1f));
    }
}
