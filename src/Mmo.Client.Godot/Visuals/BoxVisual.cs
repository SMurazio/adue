using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// The placeholder primitive: a capsule for players/NPCs that have no model, a chunky box for resources that
// have no model yet (Plant), and the forward-compatible fallback for any unknown archetype or a failed asset
// load. Behaviour is lifted verbatim from MmoClientRoot's old CreateEntityNode box path: resources read as
// scenery (box, green/grey by availability, hidden when depleted); players/NPCs read as avatars (capsule,
// blue local / orange remote).
public sealed partial class BoxVisual : EntityVisual
{
    // Shared meshes/materials — built once per visual. Distinct mesh + colour so a resource box is
    // unmistakable from a player capsule at a glance (same values as the pre-refactor consts).
    private static readonly CapsuleMesh EntityMesh = new() { Radius = 0.28f, Height = 0.9f };
    private static readonly BoxMesh ResourceMesh = new() { Size = new Vector3(0.7f, 0.7f, 0.7f) };
    // LOOT P4b: a corpse is a flat, low loot sack hugging the ground — clearly NOT a standing capsule. Dark
    // greyish-brown so it reads as a dropped bag at a glance.
    private static readonly BoxMesh CorpseMesh = new() { Size = new Vector3(0.6f, 0.3f, 0.6f) };
    private static readonly StandardMaterial3D CorpseMaterial = Material(new Color(0.40f, 0.28f, 0.18f));
    private static readonly StandardMaterial3D LocalEntityMaterial = Material(new Color(0.22f, 0.70f, 1.0f));
    private static readonly StandardMaterial3D RemoteEntityMaterial = Material(new Color(0.94f, 0.68f, 0.22f));
    // Available = lush green; depleted = dim grey (also hidden when depleted, but the material keeps it
    // readable if a future build shows stumps instead of hiding them).
    private static readonly StandardMaterial3D ResourceAvailableMaterial = Material(new Color(0.32f, 0.78f, 0.30f));
    private static readonly StandardMaterial3D ResourceDepletedMaterial = Material(new Color(0.28f, 0.30f, 0.28f));

    private MeshInstance3D _body = null!;

    protected override float LabelHeight => _isResource ? 1.3f : (_isCorpse ? 0.6f : 0.9f);

    private bool _isResource;

    // LOOT P4b: this box is rendering a dropped corpse (the low loot-sack mesh). Distinct from _isResource so the
    // depleted-availability logic (resource-only) never touches it, and it picks the corpse mesh/material.
    private bool _isCorpse;

    protected override void BuildChildren()
    {
        _body = new MeshInstance3D { Name = "Body" };
        AddChild(_body);
    }

    protected override void OnAcquire(EntityRenderState state)
    {
        _isResource = state.Kind == EntityKind.Resource;
        _isCorpse = state.Kind == EntityKind.Corpse;
        _body.Mesh = _isResource ? ResourceMesh : (_isCorpse ? CorpseMesh : EntityMesh);
        // S65: the "Plant" resource box is live-tunable via VisualTuning.PlantModelScale (default 1.0 = native
        // 0.7³). Re-applied on every (re)acquire so a pooled box reflects the current F5 panel scale. Players /
        // NPCs (capsule) and corpses keep unit scale — the knob is plant-only.
        _body.Scale = _isResource ? new Vector3(Tuning.PlantModelScale, Tuning.PlantModelScale, Tuning.PlantModelScale) : Vector3.One;
        ApplyMaterial(state);
        _body.Visible = !(_isResource && state.Depleted);
    }

    protected override void OnUpdate(EntityRenderState state, double now)
    {
        if (!_isResource)
        {
            return;
        }

        // Box-rendered resources (Plant, or a Rock/Tree that fell back to the box): drive availability purely
        // off the replicated Depleted bit — hide + grey a harvested node; restore (show + green) when the
        // server respawns it. No prediction.
        _body.Visible = !state.Depleted;
        _body.MaterialOverride = state.Depleted ? ResourceDepletedMaterial : ResourceAvailableMaterial;
    }

    // S65: re-apply the live "Plant" box scale to an already-spawned resource box so an F5 apply lands instantly
    // without a respawn. Players / NPCs (capsule) keep unit scale.
    public override void ApplyModelScale()
    {
        _body.Scale = _isResource
            ? new Vector3(Tuning.PlantModelScale, Tuning.PlantModelScale, Tuning.PlantModelScale)
            : Vector3.One;
    }

    private void ApplyMaterial(EntityRenderState state)
    {
        _body.MaterialOverride = _isResource
            ? (state.Depleted ? ResourceDepletedMaterial : ResourceAvailableMaterial)
            : (_isCorpse ? CorpseMaterial : (state.IsLocal ? LocalEntityMaterial : RemoteEntityMaterial));
    }

    private static StandardMaterial3D Material(Color color)
    {
        return new StandardMaterial3D { AlbedoColor = color, Roughness = 0.82f };
    }
}
