using System;
using System.Collections.Generic;
using Godot;
using Mmo.Client.Core;
using Mmo.Client.Core.Population;
using Mmo.Shared.Domain.Population;

namespace Mmo.Client.Godot.Visuals;

// NODE-FIELD N3 (docs/node-field-design.md D6): the thin Godot shell that turns one 32-tile chunk's worth of
// catalogue entries into MultiMeshes — one draw per (NodeType, availability) per chunk, following the SAME
// per-chunk-MultiMesh pattern DecorPainter/TerrainPainter already established (a shared Mesh + Material per
// group built ONCE, one bulk Buffer upload per group instead of a per-instance transform call). Stateless and
// static, like DecorPainter; NodeFieldView (separate type) owns the per-chunk Node3D lifecycle so a depleted
// flip can rebuild ONE chunk without touching the rest of the field.
//
// GRAYBOX DECISION (implementer's call, flagged in the review request): renders every NodeType as a coloured
// box, NOT the real Tree/Rock GLB models ModelVisual already loads (unreachable today — see its own comment).
// Before node-field, EntityVisualFactory ALSO rendered every live Rock/Tree entity as a plain box (a
// now-removed debug override) — that box is the visual standard this reuses. Swapping in the GLB meshes here
// would need their per-surface transforms composed out of the instantiated scene and re-baked into a
// MultiMesh-safe ArrayMesh (variant selection, ground-offset-that-scales-with-jitter, etc.) — real work with
// real risk of a subtly wrong ground offset that only a live walk would catch, so it is left as an explicit
// follow-up rather than guessed at here.
public static class NodeFieldPainter
{
    // Matches DecorPainter.GroundY exactly (just above the floor plane, comfortably below the wall box).
    private const float GroundY = 0.032f;

    // Depleted "stump": the SAME footprint, flattened to a fraction of the available height, in a darker/
    // greyer material — the D6-suggested graybox treatment ("greyed/flattened stump-like variant using the
    // same mesh scaled down + darker material").
    private const float StumpHeightFactor = 0.30f;

    private static readonly Dictionary<NodeType, Vector3> AvailableSize = new()
    {
        [NodeType.Tree] = new Vector3(0.55f, 1.40f, 0.55f),
        [NodeType.Rock] = new Vector3(0.65f, 0.55f, 0.65f),
        [NodeType.Plant] = new Vector3(0.40f, 0.45f, 0.40f),
    };

    private static readonly Dictionary<NodeType, Color> AvailableColor = new()
    {
        [NodeType.Tree] = new Color(0.20f, 0.45f, 0.18f),
        [NodeType.Rock] = new Color(0.55f, 0.55f, 0.58f),
        [NodeType.Plant] = new Color(0.45f, 0.70f, 0.30f),
    };

    // One shared "harvested" tint for every type's stump — legible as "gone" at a glance regardless of the
    // available colour, a flat grey in the same family as the pre-N3 entity-based resource box used to grey
    // toward when depleted.
    private static readonly Color DepletedColor = new(0.30f, 0.30f, 0.28f);

    // Built ONCE (not per chunk) and reused across every chunk's MultiMeshInstance3D — same discipline as
    // DecorPainter's per-class BuildMesh/BuildMaterial, just keyed by (NodeType, depleted) instead of a decor
    // class id.
    private static readonly IReadOnlyDictionary<(NodeType Type, bool Depleted), Mesh> Meshes = BuildMeshes();
    private static readonly IReadOnlyDictionary<(NodeType Type, bool Depleted), StandardMaterial3D> Materials = BuildMaterials();

    // Builds one chunk's worth of MultiMeshInstance3D children (one per (NodeType, availability) group that
    // actually has instances) under a fresh Node3D. `placements` must be the SAME list NodeFieldPlacer.PlaceAll
    // produced for the zone's catalogue (index-aligned: placements[entry.Index] is that entry's own jitter) —
    // `entries` themselves came from NodeFieldChunkIndex, built from that SAME catalogue, so entry.Index is
    // always safely in range for `placements` (unlike an arbitrary wire index — see NodeFieldChunkIndex's own
    // bounds-safety comment).
    public static Node3D BuildChunkNode(
        (int Cx, int Cz) chunkKey,
        IReadOnlyList<NodeCatalogEntry> entries,
        IReadOnlyList<NodeFieldPlacer.PlacedNode> placements,
        IReadOnlySet<ushort> depletedIndices,
        out int instanceCount)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(depletedIndices);

        var chunkNode = new Node3D { Name = $"NodeFieldChunk_{chunkKey.Cx}_{chunkKey.Cz}" };
        instanceCount = 0;

        var buckets = new Dictionary<(NodeType Type, bool Depleted), List<DecorPlacer.DecorInstance>>();
        foreach (var entry in entries)
        {
            var depleted = depletedIndices.Contains((ushort)entry.Index);
            var key = (entry.NodeType, depleted);
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<DecorPlacer.DecorInstance>();
                buckets[key] = list;
            }

            list.Add(placements[entry.Index].Instance);
        }

        foreach (var (key, instances) in buckets)
        {
            if (instances.Count == 0)
            {
                continue;
            }

            var mm = new MultiMesh
            {
                Mesh = Meshes[key],
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = instances.Count, // allocates the buffer; MUST precede Buffer assignment
            };
            mm.Buffer = MultiMeshTileBuffer.PackDecorTransforms(instances, GroundY);

            chunkNode.AddChild(new MultiMeshInstance3D
            {
                Name = key.Type + (key.Depleted ? "_Depleted" : "_Available"),
                Multimesh = mm,
                MaterialOverride = Materials[key],
            });

            instanceCount += instances.Count;
        }

        return chunkNode;
    }

    private static IReadOnlyDictionary<(NodeType, bool), Mesh> BuildMeshes()
    {
        var result = new Dictionary<(NodeType, bool), Mesh>();
        foreach (var (type, size) in AvailableSize)
        {
            result[(type, false)] = BuildBoxMesh(size.X, size.Y, size.Z);
            result[(type, true)] = BuildBoxMesh(size.X, size.Y * StumpHeightFactor, size.Z);
        }

        return result;
    }

    private static IReadOnlyDictionary<(NodeType, bool), StandardMaterial3D> BuildMaterials()
    {
        var result = new Dictionary<(NodeType, bool), StandardMaterial3D>();
        foreach (var (type, color) in AvailableColor)
        {
            result[(type, false)] = BuildMaterial(color);
            result[(type, true)] = BuildMaterial(DepletedColor);
        }

        return result;
    }

    private static StandardMaterial3D BuildMaterial(Color color)
    {
        // Graybox flat-color unshaded material — same recipe as DecorPainter.BuildMaterial / the authored
        // floor's materials. CullMode stays Enabled (default): unlike decor's paper-thin cross quads, this is
        // a real solid box, so backface culling is the normal/cheaper choice.
        return new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
    }

    // A solid box, pivot at the BOTTOM centre (local Y = 0), extending up to local Y = height and spanning
    // +-width/2, +-depth/2 in X/Z — so MultiMeshTileBuffer.PackDecorTransforms' existing (rotateY, scale,
    // groundY) packing plants it on the floor exactly like a DecorPlacer cross tuft, with no new buffer
    // layout. Unshaded material + (default) backface culling means winding matters for the visible side only;
    // every face below is wound outward. Raw vertex arrays, mirrors DecorPainter.BuildCrossMesh's low-level
    // ArrayMesh idiom (no SurfaceTool precedent in this codebase).
    private static ArrayMesh BuildBoxMesh(float width, float height, float depth)
    {
        var halfWidth = width / 2f;
        var halfDepth = depth / 2f;

        var vertices = new Vector3[36];
        var v = 0;

        // Bottom (-Y face, wound to face down).
        AddQuad(vertices, ref v,
            new Vector3(-halfWidth, 0f, -halfDepth), new Vector3(-halfWidth, 0f, halfDepth),
            new Vector3(halfWidth, 0f, halfDepth), new Vector3(halfWidth, 0f, -halfDepth));

        // Top (+Y face).
        AddQuad(vertices, ref v,
            new Vector3(-halfWidth, height, -halfDepth), new Vector3(halfWidth, height, -halfDepth),
            new Vector3(halfWidth, height, halfDepth), new Vector3(-halfWidth, height, halfDepth));

        // Front (+Z face).
        AddQuad(vertices, ref v,
            new Vector3(-halfWidth, 0f, halfDepth), new Vector3(halfWidth, 0f, halfDepth),
            new Vector3(halfWidth, height, halfDepth), new Vector3(-halfWidth, height, halfDepth));

        // Back (-Z face).
        AddQuad(vertices, ref v,
            new Vector3(halfWidth, 0f, -halfDepth), new Vector3(-halfWidth, 0f, -halfDepth),
            new Vector3(-halfWidth, height, -halfDepth), new Vector3(halfWidth, height, -halfDepth));

        // Right (+X face).
        AddQuad(vertices, ref v,
            new Vector3(halfWidth, 0f, halfDepth), new Vector3(halfWidth, 0f, -halfDepth),
            new Vector3(halfWidth, height, -halfDepth), new Vector3(halfWidth, height, halfDepth));

        // Left (-X face).
        AddQuad(vertices, ref v,
            new Vector3(-halfWidth, 0f, -halfDepth), new Vector3(-halfWidth, 0f, halfDepth),
            new Vector3(-halfWidth, height, halfDepth), new Vector3(-halfWidth, height, -halfDepth));

        var normals = new Vector3[vertices.Length];
        Array.Fill(normals, Vector3.Up); // unshaded material: the exact normal never affects the graybox look.

        // global:: -- inside the Mmo.Client.Godot.* namespace the bare identifier "Godot" binds to our own
        // namespace segment, not the engine's root namespace (same note as DecorPainter.BuildCrossMesh).
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // Appends two triangles (a,b,c) and (a,c,d) for quad corners a->b->c->d, advancing the write index.
    private static void AddQuad(Vector3[] vertices, ref int v, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        vertices[v++] = a;
        vertices[v++] = b;
        vertices[v++] = c;
        vertices[v++] = a;
        vertices[v++] = c;
        vertices[v++] = d;
    }
}
