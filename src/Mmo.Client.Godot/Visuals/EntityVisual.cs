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
        GetParent()?.RemoveChild(this);
        OnRelease();
    }

    // Per-frame update from the computed render state. Base drives wrapper position + label availability;
    // subclasses extend (facing, animation, depleted hide) via OnUpdate.
    public void UpdateFrom(EntityRenderState state, double now)
    {
        Position = ToWorld(state.Position);
        if (_label is not null)
        {
            SetLabelText(state.DisplayName);
            // Name label tracks availability: hide it when a resource is harvested (the model/box already
            // hides) so a mined node leaves no floating label, and show it again on respawn.
            _label.Visible = state.Kind != EntityKind.Resource || !state.Depleted;
        }

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
}
