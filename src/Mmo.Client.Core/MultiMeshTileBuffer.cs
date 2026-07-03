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
}
