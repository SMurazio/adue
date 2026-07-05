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

        // MONSTER-BEHAVIOR P6: apply the replicated PLACEHOLDER per-type visual AFTER OnAcquire (so the subclass has
        // built/bound its body + base material first). White (0xFFFFFF) + scale 1.0 are an exact no-op, so a pooled
        // visual reused for a default entity (slime/player) resets to unscaled + untinted. Done on (re)acquire only —
        // the tint/scale are constant per entity (they ride EntitySpawn, not the per-frame snapshot).
        ApplyAppearance(state);
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

    // Per-frame update from the computed render state. Base drives wrapper position + label; subclasses
    // extend (facing, animation) via OnUpdate.
    public void UpdateFrom(EntityRenderState state, double now)
    {
        // MOVEMENT-ACTIONS Phase B1 / Phase C: every kind tracks its horizontal render position and is LIFTED by the
        // REPLICATED real airborne height (state.VerticalOffset, world units) on a 1:1 unit->screen-height mapping — so a
        // real ballistic jump rises/lands for EVERY kind (the local player's own predicted jump AND a slime's hop, which
        // Phase C re-expressed as a real replicated Z, retiring the old cosmetic monster HopHeight arc). 0 grounded, so
        // the common case is the unchanged flat glide. Presentation-only — Position.Y here never affects the
        // authoritative tile/targeting.
        Position = ToWorld(state.Position) + new Vector3(0f, (float)state.VerticalOffset, 0f);
        if (_label is not null)
        {
            SetLabelText(state.DisplayName);
            // NODE-FIELD N2/N3 (docs/node-field-design.md D3/D6): this used to hide a resource's name label
            // once harvested (state.Depleted), but harvestable nodes are no longer WorldEntities (no
            // nameplates at field scale either way — D6) and Depleted is now a constant false on every
            // remaining entity, so the label is simply always shown.
            _label.Visible = true;
        }

        UpdateHealthBar(state);

        // BOSS-2 (P1 HUSK): apply the boss-plating steel tint on a STATE EDGE only (not every frame — a rebuild clones
        // a material). White/no-op for every non-boss entity, which never carries PlatingActive=true.
        if (state.PlatingActive != _platingApplied)
        {
            _platingApplied = state.PlatingActive;
            OnPlatingChanged(state.PlatingActive);
        }

        // BOSS legibility (2026-07-05 feel-test): the calm teach label — see UpdateProtectionLabel. Runs every frame
        // (cheap: a Text/Visible check-then-set) so the HP-fraction phase line can flip once the boss crosses 50%
        // between two protected windows without waiting for a fresh PlatingActive edge.
        UpdateProtectionLabel(state);

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

    // ---- MONSTER-BEHAVIOR P6 placeholder per-type visual -------------------------------------------

    // Applies the replicated placeholder appearance: SCALE the wrapper node by RenderScale (1.0 = no-op; the label +
    // HP bar children scale with the body, acceptable for a placeholder), and MODULATE the body by TintRgb via the
    // subclass tint hook (white = no-op). The wrapper's per-frame Position setter (UpdateFrom) leaves Scale untouched,
    // so applying it once on acquire holds. This is the seam where a real per-type model/animation mapping slots in
    // later (the wire fields TintRgb/RenderScale stay; only how the client renders them changes).
    private void ApplyAppearance(EntityRenderState state)
    {
        var s = state.RenderScale > 0f ? state.RenderScale : 1f;
        Scale = new Vector3(s, s, s);
        // BOSS-2 (P1): clear any pooled plating override BEFORE the P6 tint (a reused visual must not inherit the prior
        // entity's steel tint), then apply the per-type tint. UpdateFrom re-applies live plating on the next edge.
        _platingApplied = false;
        OnPlatingChanged(false);
        ApplyRenderTint(ColorFromRgb(state.TintRgb));
    }

    // Unpack a 0xRRGGBB into a Godot Color (alpha 1). 0xFFFFFF → white (the modulate no-op).
    protected static Color ColorFromRgb(uint rgb)
    {
        return new Color(
            ((rgb >> 16) & 0xFFu) / 255f,
            ((rgb >> 8) & 0xFFu) / 255f,
            (rgb & 0xFFu) / 255f);
    }

    // Modulate the body by the per-type tint. Default no-op (white is also a no-op for overriders): only archetypes
    // with a tintable body (BoxVisual — how monsters render today) override it. Real per-type monster models replace
    // this mapping later; the replicated hook (EntityRenderState.TintRgb) stays.
    protected virtual void ApplyRenderTint(Color tint) { }

    // BOSS-2 (P1 HUSK): the boss-plating steel tint toggled (BossPlatingMessage → EntityRenderState.PlatingActive).
    // Called on a state EDGE only (UpdateFrom gates it). Default no-op; only BoxVisual (the monster body) overrides it.
    protected virtual void OnPlatingChanged(bool active) { }

    // BOSS-2 (P1): the last-applied plating state, so UpdateFrom only rebuilds the material on a real change.
    private bool _platingApplied;

    // ---- BOSS legibility (2026-07-05 feel-test) teach label ------------------------------------------

    // Feel-test findings: "it's not evident that there is something that needs to be destroyed" (P1 plating) and
    // "p3 is unclear what I had to do to remove the immunity" (P3 ward). The mechanics already work; the ASK just
    // wasn't drawn in the world. This is a calm (NOT flashing) billboarded label that rides the SAME PlatingActive
    // edge as the steel tint — shown while a boss is protected, hidden the instant it drops (shatter/burst window).
    // Parented to the wrapper like the name label / HP bar, so it tracks the boss for free (no per-frame position
    // code needed — the wrapper's own per-frame Position assignment in UpdateFrom carries every child along).
    private Label3D? _protectionLabel;

    // Copy matches the voice of the encounter's own AnnounceAll lines (BossEncounterEngine.cs, e.g. "The Sunderer's
    // plating turns your blows!" / "its core seals!").
    private const string PlatingTeachText = "PLATED — cross your skillshots to shatter!";
    private const string WardTeachText = "CORE SEALED — detonate at its heart!";

    // The HP-fraction split between the two lines. Safe without reading server state: P1 plating only exists above
    // 70% HP and P3 ward only at/below 40% HP (BossEncounterEngine.PlatingHealthFraction / the P3 arm threshold), so
    // the two protected windows never overlap around this midpoint.
    private const float ProtectionPhaseSplitFraction = 0.5f;

    // Calm pale cyan-white — legible over any terrain, deliberately NOT the alarming red of a damage number and NOT
    // flashing/animated (a persistent, readable teaching cue, not an alert).
    private static readonly Color ProtectionLabelColor = new(0.80f, 0.92f, 0.96f);

    // A touch above the name label so the two never overlap.
    private float ProtectionLabelHeight => LabelHeight + 0.45f;

    // Called every frame from UpdateFrom. Hides the label the instant PlatingActive drops (or the entity is
    // released — Release() hides the whole wrapper, which cascades to every child including this one); while active,
    // picks the phase line from the live HP fraction and only writes Text on a real change.
    private void UpdateProtectionLabel(EntityRenderState state)
    {
        if (!state.PlatingActive)
        {
            if (_protectionLabel is { Visible: true })
            {
                _protectionLabel.Visible = false;
            }

            return;
        }

        EnsureProtectionLabel();
        if (_protectionLabel is null)
        {
            return;
        }

        var text = state.HealthFraction > ProtectionPhaseSplitFraction ? PlatingTeachText : WardTeachText;
        if (_protectionLabel.Text != text)
        {
            _protectionLabel.Text = text;
        }

        _protectionLabel.Visible = true;
    }

    // Lazily builds the teach label (only ever needed for the one boss that carries PlatingActive=true).
    private void EnsureProtectionLabel()
    {
        if (_protectionLabel is not null)
        {
            return;
        }

        _protectionLabel = new Label3D
        {
            Name = "ProtectionTeach",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FixedSize = true,
            PixelSize = Tuning.LabelPixelSize,
            FontSize = VisualTuning.LabelFontSize,
            OutlineSize = VisualTuning.LabelOutlineSize,
            OutlineModulate = VisualTuning.LabelOutlineColor,
            NoDepthTest = true,
            Modulate = ProtectionLabelColor,
            Position = new Vector3(0f, ProtectionLabelHeight, 0f),
            Visible = false,
        };
        AddChild(_protectionLabel);
    }

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
