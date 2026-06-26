using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// S73 debug visual: a plain box for a Player entity (local blue / remote orange) with a small flat arrow at
// its base pointing along the entity's 8-way Facing. Swapped in for PlayerVisual when the F5 "Debug facing
// box" toggle is on, so facing + per-step movement are legible while debugging movement feel (the character
// model makes facing hard to read). Diagnostic-only; reuses shared static meshes/materials and reads the same
// EntityRenderState.Facing the avatar does — so the arrow tracks the way the avatar actually walks.
//
// Pool-keyed by VisualArchetype.DebugFacingBox so a parked debug box is only ever reused for another player
// debug box, never mixed with a real PlayerVisual.
public sealed partial class DebugFacingBoxVisual : EntityVisual
{
    // Box body sized like a player capsule's footprint so it reads as an avatar at a glance. Shared statics
    // (built once, reused across every instance) — never allocated per entity/frame.
    private static readonly BoxMesh BodyMesh = new() { Size = new Vector3(0.56f, 0.9f, 0.56f) };
    private static readonly StandardMaterial3D LocalBodyMaterial = Material(new Color(0.22f, 0.70f, 1.0f));
    private static readonly StandardMaterial3D RemoteBodyMaterial = Material(new Color(0.94f, 0.68f, 0.22f));
    // Bright facing arrow — a flat triangular prism authored pointing along -Z (Godot's conventional forward),
    // so a yaw of Atan2(-dx,-dy) about +Y aims it at the world heading the avatar walks (see ApplyFacing).
    private static readonly ArrayMesh ArrowMesh = BuildArrowMesh();
    // Bright yellow, and (facing-arrow fix) Unshaded + NoDepthTest so the flat arrow always draws ON TOP of the box
    // body / ground instead of being occluded or z-fighting and reading as "missing". Body materials stay normal.
    private static readonly StandardMaterial3D ArrowMaterial = ArrowMaterialOnTop(new Color(1.0f, 0.95f, 0.20f));

    private MeshInstance3D _body = null!;
    private MeshInstance3D _arrow = null!;

    // Box is 0.9 tall, origin centered, so its base sits at y=-0.45 and top at y=+0.45. Park the label just
    // above the top.
    protected override float LabelHeight => 1.0f;

    protected override bool TracksLabelHeight => true;

    protected override void BuildChildren()
    {
        _body = new MeshInstance3D
        {
            Name = "DebugBody",
            Mesh = BodyMesh,
            // Lift the centered box so its base sits on the ground plane (y=0), matching the avatar's feet.
            Position = new Vector3(0f, 0.45f, 0f)
        };
        AddChild(_body);

        // The arrow is a small flat wedge laid on the ground pointing in facing. FIX (facing-arrow): it was parked at
        // y=0.02 — flush with the terrain plane (y=0) and the other ground markers, so it z-fought / sank under them
        // and read as "missing / too low". Lift it clear of the ground band (above the cyan server marker at 0.06 and
        // the spawner anchors at 0.03); the NoDepthTest material below also draws it on top so it always shows.
        _arrow = new MeshInstance3D
        {
            Name = "FacingArrow",
            Mesh = ArrowMesh,
            MaterialOverride = ArrowMaterial,
            Position = new Vector3(0f, 0.1f, 0f)
        };
        AddChild(_arrow);
    }

    protected override void OnAcquire(EntityRenderState state)
    {
        _body.MaterialOverride = state.IsLocal ? LocalBodyMaterial : RemoteBodyMaterial;
        ApplyFacing(state.Facing);
    }

    protected override void OnUpdate(EntityRenderState state, double now)
    {
        // Rotate the arrow each frame to the entity's current 8-way facing (predicted facing already baked into
        // state.Facing by Core for the local player), so it tracks turns + direction changes live.
        ApplyFacing(state.Facing);
    }

    // Yaw the arrow about +Y so its authored -Z forward points along the entity's facing in WORLD space. The
    // world mapping matches the avatar's: ToWorld is (tileX -> +X, tileY -> +Z), and a yaw θ turns -Z into
    // (-sinθ, 0, -cosθ); solving that to equal (delta.X, delta.Y) gives θ = atan2(-dx, -dy). N (0,-1) -> 0,
    // E (1,0) -> -90°, S (0,1) -> 180°, W (-1,0) -> 90° — identical to PlayerVisual.ApplyFacing MINUS the GLB
    // rig's 180° ForwardOffset correction (we author the arrow ourselves, so no rig correction is needed).
    private void ApplyFacing(Direction8 facing)
    {
        var delta = facing.Delta();
        if (delta.X == 0 && delta.Y == 0)
        {
            return;
        }

        var yaw = Mathf.Atan2(-delta.X, -delta.Y);
        _arrow.Rotation = new Vector3(0f, yaw, 0f);
    }

    // A flat triangular arrowhead lying in the X/Z plane, apex at -Z (forward), base spanning ±X behind it.
    // Built once as a shared ArrayMesh. Kept small + cheap (3 verts, single tri) — it's a debug marker.
    private static ArrayMesh BuildArrowMesh()
    {
        const float length = 0.9f; // apex distance ahead of the box center
        const float halfWidth = 0.35f; // half the base width behind the apex
        const float baseZ = 0.1f; // base sits slightly in front of the box center

        var vertices = new Vector3[]
        {
            new(0f, 0f, -length), // apex (forward, -Z)
            new(-halfWidth, 0f, baseZ), // back-left
            new(halfWidth, 0f, baseZ) // back-right
        };
        var normals = new Vector3[] { Vector3.Up, Vector3.Up, Vector3.Up };

        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static StandardMaterial3D Material(Color color)
    {
        // CullDisabled so the flat single-sided arrow is visible from above regardless of winding.
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.82f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
    }

    // The facing arrow's material: like Material() but Unshaded + NoDepthTest so the flat ground arrow ALWAYS renders
    // on top (a diagnostic overlay must never be hidden by the box body it sits under or z-fight the terrain). Only
    // the arrow uses this; the box body keeps the normal shaded, depth-tested Material().
    private static StandardMaterial3D ArrowMaterialOnTop(Color color)
    {
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            Roughness = 0.82f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            NoDepthTest = true
        };
    }
}
