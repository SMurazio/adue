using System;
using System.Collections.Generic;
using Godot;
using Mmo.Client.Core;
using Mmo.Client.Core.Population;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// PROCEDURAL-POPULATION P2 (docs/procedural-population-design.md D1 L1): the thin Godot shell over
// DecorPlacer — turns its headless (deterministic, Godot-free) instance lists into per-chunk MultiMeshes,
// following the SAME pattern TerrainPainter.BuildAuthoredFloor already established: one shared Mesh +
// Material per class (built ONCE), grouped into TerrainPainter.ChunkTiles-square chunks so each chunk's
// small bounded AABB frustum-culls independently, uploaded with ONE MultiMesh.Buffer bulk assignment per
// (class, chunk) pair instead of one SetInstanceTransform interop call per instance (M2 perf path, reused
// here via MultiMeshTileBuffer.PackDecorTransforms).
//
// D1 gate ("genVersion 1 zones: NO decor"): the caller (MmoClientRoot.BuildZone) only calls BuildDecor when
// zone.Authored is non-null, so there is no separate gate here — this class always assumes an authored map.
public static class DecorPainter
{
    // Just above the authored floor plane (TerrainPainter.FloorY = 0.03f) so decor never z-fights the
    // ground, and comfortably below the wall box's bottom (~-0.03..0.83, TerrainPainter comment) so decor
    // never pokes through a wall it happens to sit near.
    private const float GroundY = 0.032f;

    /// <summary>
    /// Builds every decor class as chunked MultiMeshes under a new "Decor" root added to
    /// <paramref name="parent"/>. Returns the root and the total instance count actually placed (for the
    /// zone-build timing print) — the design's ≤30k instance budget is a TargetCount cap on the class
    /// table (DecorClassTableTests), this is what actually landed for THIS map.
    /// </summary>
    public static (Node3D Root, int InstanceCount) BuildDecor(Node parent, AuthoredMap map, int zoneSeed)
    {
        var placements = DecorPlacer.PlaceAll(map, zoneSeed);

        var root = new Node3D { Name = "Decor" };
        var chunkNodes = new Dictionary<(int Cx, int Cz), Node3D>();
        var totalInstances = 0;

        foreach (var decorClass in DecorClassTable.Classes)
        {
            if (!placements.TryGetValue(decorClass.Id, out var instances) || instances.Count == 0)
            {
                continue;
            }

            var mesh = BuildMesh(decorClass);
            var material = BuildMaterial(decorClass);

            foreach (var (chunkKey, chunkInstances) in BucketByChunk(instances))
            {
                var mm = new MultiMesh
                {
                    Mesh = mesh,
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    InstanceCount = chunkInstances.Count, // allocates the buffer; MUST precede Buffer assignment
                };
                mm.Buffer = MultiMeshTileBuffer.PackDecorTransforms(chunkInstances, GroundY);

                if (!chunkNodes.TryGetValue(chunkKey, out var chunkNode))
                {
                    chunkNode = new Node3D { Name = $"DecorChunk_{chunkKey.Cx}_{chunkKey.Cz}" };
                    chunkNodes[chunkKey] = chunkNode;
                    root.AddChild(chunkNode);
                }

                chunkNode.AddChild(new MultiMeshInstance3D
                {
                    Name = "Decor_" + decorClass.Id,
                    Multimesh = mm,
                    MaterialOverride = material,
                });

                totalInstances += chunkInstances.Count;
            }
        }

        parent.AddChild(root);
        return (root, totalInstances);
    }

    // Buckets instances into TerrainPainter.ChunkTiles-square chunks by their ORIGIN tile, recovered by
    // rounding X/Z to the nearest integer (safe: DecorPlacer's sub-tile jitter is bounded well under half
    // a tile, so rounding always recovers the exact tile the instance was scattered onto — never a
    // neighbour). Bucketing by the rounded origin tile (not the raw jittered float) keeps chunk membership
    // exactly aligned with the tile grid, same as the floor/wall chunking.
    private static Dictionary<(int Cx, int Cz), List<DecorPlacer.DecorInstance>> BucketByChunk(
        IReadOnlyList<DecorPlacer.DecorInstance> instances)
    {
        const int chunkTiles = TerrainPainter.ChunkTiles;
        var buckets = new Dictionary<(int Cx, int Cz), List<DecorPlacer.DecorInstance>>();
        foreach (var instance in instances)
        {
            var tileX = (int)MathF.Round(instance.X);
            var tileZ = (int)MathF.Round(instance.Z);
            var key = (tileX / chunkTiles, tileZ / chunkTiles);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new List<DecorPlacer.DecorInstance>();
                buckets[key] = bucket;
            }

            bucket.Add(instance);
        }

        return buckets;
    }

    private static StandardMaterial3D BuildMaterial(DecorClass decorClass)
    {
        // Graybox flat-color unshaded material, CullMode Disabled — same recipe as
        // AuthoredSurfaceVisuals' floor materials. Disabled culling matters more here than for the floor:
        // the Cross shape's two quads are only ever seen from ONE side each at a given camera angle, and a
        // single-sided quad would vanish from the back.
        return new StandardMaterial3D
        {
            AlbedoColor = new Color(decorClass.Color.R, decorClass.Color.G, decorClass.Color.B),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    private static Mesh BuildMesh(DecorClass decorClass)
    {
        return decorClass.Shape switch
        {
            // PlaneMesh already faces +Y (lies flat in the local XZ plane) — same primitive
            // AuthoredSurfaceVisuals' floor uses, so a FlatQuad decor instance needs only the shared
            // rotate-about-Y + translate transform PackDecorTransforms already produces, no extra flatten.
            DecorShape.FlatQuad => new PlaneMesh { Size = new Vector2(decorClass.Width, decorClass.Height) },
            DecorShape.Cross => BuildCrossMesh(decorClass.Width, decorClass.Height),
            _ => throw new ArgumentOutOfRangeException(
                nameof(decorClass), decorClass.Shape, "DecorPainter: unhandled DecorShape."),
        };
    }

    // Two crossed vertical quads (classic billboard-cross grass tuft), pivot at the BOTTOM (local Y = 0)
    // so the instance transform's origin sits on the ground and the tuft grows upward to local Y = height.
    // Built with raw vertex arrays + AddSurfaceFromArrays, the same low-level ArrayMesh idiom already used
    // by MmoClientRoot.BuildAimWedgeMesh / DebugFacingBoxVisual.BuildArrowMesh in this codebase (no
    // SurfaceTool precedent elsewhere, so this matches rather than introducing a new mesh-building style).
    // Normals are a placeholder Vector3.Up on every vertex: the material is Unshaded (ignores normals for
    // lighting) and CullMode.Disabled (both faces render regardless of winding), so the exact normal never
    // affects the graybox look.
    private static ArrayMesh BuildCrossMesh(float width, float height)
    {
        var halfWidth = width / 2f;

        var vertices = new Vector3[12];
        var v = 0;

        // Quad A: spans local X, lies at local Z = 0.
        AddQuad(
            vertices, ref v,
            new Vector3(-halfWidth, 0f, 0f), new Vector3(halfWidth, 0f, 0f),
            new Vector3(halfWidth, height, 0f), new Vector3(-halfWidth, height, 0f));

        // Quad B: spans local Z, lies at local X = 0 — crossed 90 degrees against Quad A.
        AddQuad(
            vertices, ref v,
            new Vector3(0f, 0f, -halfWidth), new Vector3(0f, 0f, halfWidth),
            new Vector3(0f, height, halfWidth), new Vector3(0f, height, -halfWidth));

        var normals = new Vector3[12];
        Array.Fill(normals, Vector3.Up);

        // global:: — inside the Mmo.Client.Godot.* namespace the bare identifier "Godot" binds to our own
        // namespace segment, not the engine's root namespace.
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
