using Mmo.Client.Core.Population;
using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// M2 perf (docs/town-floor1-blockout-design.md, 384-scale criteria): packs per-instance MultiMesh
// transform data as ONE flat float array so the Godot side can upload a whole chunk with a single
// `MultiMesh.Buffer = ...` assignment instead of one SetInstanceTransform interop call per instance —
// at 384×384 that is ~147k floor tiles, and per-instance C#→Godot marshaling is a login hitch.
//
// Buffer layout contract (Godot 4, TransformFormat=3D, no colors, no custom data): 12 floats per
// instance — the Transform3D's basis ROWS interleaved with the origin, i.e.
//   [ row0.x row0.y row0.z origin.x   row1.x row1.y row1.z origin.y   row2.x row2.y row2.z origin.z ]
// (matches the engine's multimesh storage; buffer length MUST equal instanceCount * 12). Pinned by
// test so a layout regression is caught headlessly, not as a silently garbled floor.
public static class MultiMeshTileBuffer
{
    public const int FloatsPerInstance = 12;

    // Pack one instance per tile with an IDENTITY basis at origin (tile.X, y, tile.Y) — the tile→world
    // mapping every static zone MultiMesh uses (upright wall boxes, flat +Y-facing floor planes). Identity
    // basis rows make the layout trivial: only the diagonal ones and the origin column are non-zero.
    public static float[] PackUprightTileTransforms(IReadOnlyList<TileCoord> tiles, float y)
    {
        var buffer = new float[tiles.Count * FloatsPerInstance];
        var o = 0;
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            buffer[o + 0] = 1f;      // row0 = (1, 0, 0)
            buffer[o + 3] = tile.X;  // origin.x
            buffer[o + 5] = 1f;      // row1 = (0, 1, 0)
            buffer[o + 7] = y;       // origin.y
            buffer[o + 10] = 1f;     // row2 = (0, 0, 1)
            buffer[o + 11] = tile.Y; // origin.z
            o += FloatsPerInstance;
        }

        return buffer;
    }

    // PROCEDURAL-POPULATION P2 (docs/procedural-population-design.md D1 L1): packs decor instances into
    // the SAME 12-floats-per-instance layout as PackUprightTileTransforms above, but with a real basis
    // instead of identity — a Y-axis rotation (in-plane jitter, D1 "for life") composed with a uniform
    // scale, at a fixed ground Y shared by the whole class. Y-axis rotation leaves the Y row untouched
    // (rotating about Y does not move the Y axis) and only mixes X/Z, matching Godot's own row convention
    // for `new Basis(Vector3.Up, angle)`: row0 = (cos, 0, sin), row1 = (0, 1, 0), row2 = (-sin, 0, cos).
    // Multiplying every row by the instance's uniform scale is equivalent to `basis.Scaled(...)` for a
    // uniform scale (rotate and scale commute when the scale is the same on every axis), so this stays a
    // pure float computation with no Godot dependency — MultiMeshTileBufferTests pins the exact values.
    public static float[] PackDecorTransforms(IReadOnlyList<DecorPlacer.DecorInstance> instances, float groundY)
    {
        var buffer = new float[instances.Count * FloatsPerInstance];
        var o = 0;
        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            var cos = MathF.Cos(instance.RotationRadians) * instance.Scale;
            var sin = MathF.Sin(instance.RotationRadians) * instance.Scale;

            buffer[o + 0] = cos;             // row0.x
            buffer[o + 2] = sin;             // row0.z
            buffer[o + 3] = instance.X;      // origin.x
            buffer[o + 5] = instance.Scale;  // row1.y (Y axis untouched by a Y-rotation, just scaled)
            buffer[o + 7] = groundY;         // origin.y
            buffer[o + 8] = -sin;            // row2.x
            buffer[o + 10] = cos;            // row2.z
            buffer[o + 11] = instance.Z;     // origin.z
            o += FloatsPerInstance;
        }

        return buffer;
    }
}
