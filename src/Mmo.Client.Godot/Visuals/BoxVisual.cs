using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// The placeholder primitive: a capsule for players/NPCs that have no model, a chunky box for resources that
// have no model yet (Plant, and the House/Portal PROP fallback if their real visual fails to load), and the
// forward-compatible fallback for any unknown archetype or a failed asset load. Resources read as scenery
// (green box); players/NPCs read as avatars (capsule, blue local / orange remote).
//
// NODE-FIELD N2/N3 (docs/node-field-design.md D3/D6): harvestable Tree/Rock/Plant nodes are no longer
// WorldEntities at all (they render via the catalogue field's MultiMeshes — NodeFieldPainter — not per-entity
// visuals), and the ONE remaining Resource-kind entity family (House/Portal props) never depletes. The
// Depleted-driven hide/greyed-stump behaviour this class used to have (state.Depleted is now a constant false
// on every entity, per EntityStateSnapshot's own comment) was removed as dead code — a resource box is always
// shown, always the "available" colour.
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
    // DUO-SKILLSHOT: a small bright sphere for an in-flight skillshot. White + UNSHADED base so the server's replicated
    // per-tier tint (multiplied in ApplyRenderTint) reads as its exact colour (cyan/amber/magenta) and it glows over
    // the terrain regardless of lighting. The per-tier SIZE rides the replicated RenderScale (ApplyAppearance).
    private static readonly SphereMesh ProjectileMesh = new() { Radius = 0.22f, Height = 0.44f, RadialSegments = 12, Rings = 6 };
    private static readonly StandardMaterial3D ProjectileMaterial = UnshadedMaterial(new Color(1f, 1f, 1f));
    private static readonly StandardMaterial3D LocalEntityMaterial = Material(new Color(0.22f, 0.70f, 1.0f));
    private static readonly StandardMaterial3D RemoteEntityMaterial = Material(new Color(0.94f, 0.68f, 0.22f));
    private static readonly StandardMaterial3D ResourceAvailableMaterial = Material(new Color(0.32f, 0.78f, 0.30f));

    private MeshInstance3D _body = null!;

    // MONSTER-BEHAVIOR P6: a per-instance tinted material clone, built lazily when a non-white tint is applied. Kept
    // off the SHARED static materials (mutating those would tint every entity that shares them).
    private StandardMaterial3D? _tintedMaterial;

    protected override float LabelHeight => _isResource ? 1.3f : (_isCorpse ? 0.6f : 0.9f);

    private bool _isResource;

    // LOOT P4b: this box is rendering a dropped corpse (the low loot-sack mesh). Distinct from _isResource so
    // mesh/material selection picks the corpse variant instead of the resource one.
    private bool _isCorpse;

    // DUO-SKILLSHOT: this box is rendering an in-flight skillshot (the bright sphere). Distinct flag so mesh/material
    // selection picks the projectile variant.
    private bool _isProjectile;

    protected override void BuildChildren()
    {
        _body = new MeshInstance3D { Name = "Body" };
        AddChild(_body);
    }

    protected override void OnAcquire(EntityRenderState state)
    {
        _isResource = state.Kind == EntityKind.Resource;
        _isCorpse = state.Kind == EntityKind.Corpse;
        _isProjectile = state.Kind == EntityKind.Projectile;
        _body.Mesh = _isProjectile ? ProjectileMesh : (_isResource ? ResourceMesh : (_isCorpse ? CorpseMesh : EntityMesh));
        // S65: the "Plant" resource box is live-tunable via VisualTuning.PlantModelScale (default 1.0 = native
        // 0.7³). Re-applied on every (re)acquire so a pooled box reflects the current F5 panel scale. Players /
        // NPCs (capsule) and corpses keep unit scale — the knob is plant-only.
        _body.Scale = _isResource ? new Vector3(Tuning.PlantModelScale, Tuning.PlantModelScale, Tuning.PlantModelScale) : Vector3.One;
        ApplyMaterial(state);
    }

    // S65: re-apply the live "Plant" box scale to an already-spawned resource box so an F5 apply lands instantly
    // without a respawn. Players / NPCs (capsule) keep unit scale.
    public override void ApplyModelScale()
    {
        _body.Scale = _isResource
            ? new Vector3(Tuning.PlantModelScale, Tuning.PlantModelScale, Tuning.PlantModelScale)
            : Vector3.One;
    }

    // MONSTER-BEHAVIOR P6: modulate the body by the replicated per-type tint. White is an exact no-op — keep the
    // SHARED static material ApplyMaterial set in OnAcquire (so the slime/players/resources are untouched). A non-white
    // tint MODULATES (multiplies) the body's base albedo via a per-instance clone of that shared material, so a gnoll
    // reads as a distinct colour without ever mutating the shared materials. Called by the base after OnAcquire, so
    // _body.MaterialOverride is already the chosen shared material. Placeholder — real per-type models replace it later.
    protected override void ApplyRenderTint(Color tint)
    {
        if (tint == Colors.White)
        {
            return;
        }

        if (_body.MaterialOverride is not StandardMaterial3D baseMaterial)
        {
            return;
        }

        _tintedMaterial = (StandardMaterial3D)baseMaterial.Duplicate();
        _tintedMaterial.AlbedoColor = baseMaterial.AlbedoColor * tint;
        _body.MaterialOverride = _tintedMaterial;
    }

    private void ApplyMaterial(EntityRenderState state)
    {
        _body.MaterialOverride = _isProjectile
            ? ProjectileMaterial
            : (_isResource
                ? ResourceAvailableMaterial
                : (_isCorpse ? CorpseMaterial : (state.IsLocal ? LocalEntityMaterial : RemoteEntityMaterial)));
    }

    private static StandardMaterial3D Material(Color color)
    {
        return new StandardMaterial3D { AlbedoColor = color, Roughness = 0.82f };
    }

    // DUO-SKILLSHOT: an UNSHADED (self-lit) material so a projectile reads as a bright bolt over any terrain lighting.
    private static StandardMaterial3D UnshadedMaterial(Color color)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
    }
}
