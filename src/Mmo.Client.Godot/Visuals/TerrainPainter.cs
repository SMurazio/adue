using System;
using Godot;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// S112: paints the zone floor with the tiling textures, driven by the res://content/maps/terrain.png design
// bitmap. Client-side VISUAL only — it does not touch movement, collision, snapshots, or the server. Per texture
// it builds a MultiMeshInstance3D over a flat 1x1 quad, with per-instance rotation/flip baked into the transform
// basis and the chosen tile texture as the material albedo. Built once per zone from BuildZone. If the bitmap or
// any texture is missing it logs a warning and falls back to all-grass — it never hard-fails.
//
// CHUNKING (this pass): instead of ONE MultiMesh per texture spanning the WHOLE map (one giant never-culled
// AABB), the floor is partitioned into a GRID OF SQUARE CHUNKS of CHUNK_TILES per side. Each chunk gets its own
// Node3D holding its own per-texture MultiMeshInstance3D(s) covering ONLY the tiles in that chunk. Each instance
// therefore has a SMALL bounded AABB, so Godot frustum-culls whole chunks when they are off-screen. The painted
// result is identical to the unified version: classification + autotiling run once over the WHOLE map (so a
// tile's chosen texture/orientation still depends on its true neighbours across chunk seams), only the
// per-instance partition differs. Every tile belongs to exactly one chunk (chunkX = tileX / CHUNK_TILES), so
// there are no gaps or double-draws at chunk boundaries.
public static class TerrainPainter
{
    // Square chunk size in tiles. A W×H map yields ceil(W/CHUNK_TILES)×ceil(H/CHUNK_TILES) chunks. Chosen at 32:
    // big enough that chunk-node/MultiMesh overhead stays small, small enough that each chunk's AABB is a useful
    // frustum-cull unit. Shared by the floor here and (mirrored) by the wall chunking in MmoClientRoot.BuildZone.
    public const int ChunkTiles = 32;

    // ---- Tunable orientation constants (flagged for live verification) -------------------------------------
    // The terrain.png pixel→tile mapping uses x→east, y→south (matches TileToWorld / minimap). If the painted
    // floor looks mirrored or transposed versus the design, flip these. They are intentionally simple toggles.
    private const bool FlipX = false; // mirror the sampled column (x)
    private const bool FlipY = false; // mirror the sampled row (y)

    // Classification threshold: terrain is yellow (R-B high), grass is grey (R-B≈0). 8-bit R-B>40 → terrain.
    // Expressed normalized (0..1) because Godot Image.GetPixel returns floats: 40/255 ≈ 0.157.
    private const float YellownessThreshold = 40f / 255f;

    private const string DesignBitmapPath = "res://content/maps/terrain.png";
    private const string TileDir = "res://content/sprites/tiling/";

    // Floor height: just above the solid ground box (top at y=0) and at/above the grid plane (0.02). The
    // painted quads are the visible ground; the ground box stays for picking.
    private const float FloorY = 0.03f;

    // Fixed texture-array layer indices. The Texture2DArray is built in exactly this order; INSTANCE_CUSTOM.x
    // carries the chosen layer per instance.
    private static readonly string[] LayerFiles =
    {
        "Terrain_middle_01.jpg", // 0
        "Terrain_middle_02.jpg", // 1
        "grass_01.jpg",          // 2
        "grass_02.jpg",          // 3
        "grass_03.jpg",          // 4
        "Terrain_side_left_01.jpg",  // 5  green on LEFT half
        "Terrain_side_left_02.jpg",  // 6
        "Terrain_side_right_01.jpg", // 7  green on RIGHT half
        "Terrain_side_right_02.jpg", // 8
        "Terrain_corner.jpg",        // 9  grass on top+left (N+W), terrain bottom-right (S+E)
    };

    private const int LayerMiddle0 = 0;
    private const int LayerGrass0 = 2;
    private const int LayerSideLeft0 = 5;
    private const int LayerSideRight0 = 7;
    private const int LayerCorner = 9;

    private enum Cell : byte { Grass, Terrain }

    // Build the painted floor under `parent` for a zone of (width x height) tiles. Returns the created node, or
    // null if the tile textures could not be loaded (caller keeps the grid plane visible as a fallback in that
    // case). The design bitmap missing is NOT fatal — that falls back to all-grass and still paints.
    //
    // The floor is partitioned into a grid of CHUNK_TILES-square chunks under a single "FloorChunks" root. Each
    // chunk is a child Node3D ("FloorChunk_<cx>_<cz>") holding that chunk's per-texture MultiMeshInstance3D(s).
    // Classification + autotiling run ONCE over the whole map (so cross-seam neighbours are honoured); only the
    // per-instance partitioning is per chunk, so the painted result is byte-identical to the unified version.
    public static Node3D? BuildFloor(Node parent, int width, int height)
    {
        var textures = LoadTileTextures();
        if (textures is null)
        {
            GD.PushWarning("S112 TerrainPainter: tile textures could not be loaded; keeping the grid floor. " +
                           "A relaunch after Godot imports content/sprites/tiling should work.");
            return null;
        }

        // Classify the WHOLE map once. ChooseTile reads 4-neighbours, so partitioning the *build* (below) must not
        // partition the *classification*: a tile at a chunk edge still sees its true neighbour in the next chunk.
        var cells = ClassifyDesign(width, height);

        // Shared flat quad + flatten basis (see ChooseTile/SideTile comments). One quad mesh reused by every chunk.
        var quad = new QuadMesh { Size = new Vector2(1f, 1f) };
        // Tip the quad flat facing up: it faces +Z and lies in its local XY plane; -90° about X points it +Y.
        // The per-tile in-plane orientation `basis` (rotation about the quad's local Z + optional mirror) is
        // applied FIRST, then this flatten. After flatten: texture U → world +X (east).
        var flatten = new Basis(Vector3.Right, -Mathf.Pi / 2f);

        // One material per texture layer, built ONCE and shared across all chunks (identical look, no per-chunk
        // material churn). A chunk only references the materials its own tiles use.
        var materials = new StandardMaterial3D[LayerFiles.Length];
        for (var layer = 0; layer < materials.Length; layer++)
        {
            materials[layer] = new StandardMaterial3D
            {
                AlbedoTexture = textures[layer],
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            };
        }

        var root = new Node3D { Name = "FloorChunks" };

        var chunksX = (width + ChunkTiles - 1) / ChunkTiles;   // ceil(W / CHUNK_TILES)
        var chunksZ = (height + ChunkTiles - 1) / ChunkTiles;  // ceil(H / CHUNK_TILES)

        // TODO(streaming): chunks are keyed by (cx, cz) and each is a self-contained, individually-freeable
        // Node3D, so a follow-up can build/free chunks by player distance instead of building all up front. This
        // pass builds ALL chunks (partition + per-chunk frustum culling only). To stream, replace this nested loop
        // with a "build the chunks near the player, free the far ones" scheduler keyed off the same (cx, cz) grid.
        for (var cz = 0; cz < chunksZ; cz++)
        {
            for (var cx = 0; cx < chunksX; cx++)
            {
                var x0 = cx * ChunkTiles;
                var y0 = cz * ChunkTiles;
                var x1 = Math.Min(x0 + ChunkTiles, width);   // exclusive
                var y1 = Math.Min(y0 + ChunkTiles, height);  // exclusive

                var chunk = BuildFloorChunk(cells, width, height, x0, y0, x1, y1, quad, flatten, materials);
                if (chunk is not null)
                {
                    chunk.Name = $"FloorChunk_{cx}_{cz}";
                    root.AddChild(chunk);
                }
            }
        }

        parent.AddChild(root);
        return root;
    }

    // Build one floor chunk covering the half-open tile range [x0,x1) × [y0,y1). Classification (`cells`) is the
    // WHOLE-map grid so autotiling at the chunk edge still reads the real neighbour in the adjacent chunk. Returns
    // a Node3D holding one MultiMeshInstance3D per texture layer present in this chunk, or null if the chunk has
    // no tiles (empty range). Every tile lands in exactly one chunk (x0..x1 are disjoint across chunks), so the
    // union of all chunks reproduces the full floor with no gaps or overlaps.
    private static Node3D? BuildFloorChunk(
        Cell[,] cells, int width, int height, int x0, int y0, int x1, int y1,
        QuadMesh quad, Basis flatten, StandardMaterial3D[] materials)
    {
        if (x0 >= x1 || y0 >= y1)
        {
            return null;
        }

        var perLayer = new System.Collections.Generic.List<Transform3D>[LayerFiles.Length];
        for (var i = 0; i < perLayer.Length; i++)
        {
            perLayer[i] = new System.Collections.Generic.List<Transform3D>();
        }

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var (layer, basis) = ChooseTile(cells, width, height, x, y);
                perLayer[layer].Add(new Transform3D(flatten * basis, new Vector3(x, FloorY, y)));
            }
        }

        var chunk = new Node3D();
        for (var layer = 0; layer < perLayer.Length; layer++)
        {
            var list = perLayer[layer];
            if (list.Count == 0)
            {
                continue;
            }

            var mm = new MultiMesh
            {
                Mesh = quad,
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = list.Count,
            };
            for (var i = 0; i < list.Count; i++)
            {
                mm.SetInstanceTransform(i, list[i]);
            }

            chunk.AddChild(new MultiMeshInstance3D
            {
                Name = "TerrainFloor_" + layer,
                Multimesh = mm,
                MaterialOverride = materials[layer],
            });
        }

        return chunk;
    }

    // Public classification for the minimap: true where the design bitmap says TERRAIN, false for grass. Reuses
    // the same bitmap load + yellowness rule the floor uses (one source of truth), so the minimap matches the
    // painted ground. Falls back to all-grass (all false) if the bitmap is missing. Cheap (one read per call).
    public static bool[,] LoadTerrainGrid(int width, int height)
    {
        var cells = ClassifyDesign(width, height);
        var grid = new bool[width, height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                grid[x, y] = cells[x, y] == Cell.Terrain;
            }
        }

        return grid;
    }

    // ---- Design bitmap → binary classification grid -------------------------------------------------------
    private static Cell[,] ClassifyDesign(int width, int height)
    {
        var cells = new Cell[width, height];

        var image = LoadDesignImage();
        if (image is null)
        {
            // Fallback: all grass. Already zero-initialized (Cell.Grass == 0), but be explicit.
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    cells[x, y] = Cell.Grass;
                }
            }

            return cells;
        }

        // GetPixel requires an uncompressed format; a VRAM-compressed import would otherwise error. Project
        // imports are lossless (compress/mode=0) so this is normally a no-op, but convert defensively.
        if (image.IsCompressed())
        {
            image.Decompress();
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        var imgW = image.GetWidth();
        var imgH = image.GetHeight();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Sample the pixel at the CENTRE of this tile's region (handles arbitrary bitmap size, not
                // assuming 1px/tile). px=(int)((x+0.5)*imgW/width), py similarly.
                var sx = FlipX ? (width - 1 - x) : x;
                var sy = FlipY ? (height - 1 - y) : y;
                var px = (int)((sx + 0.5f) * imgW / width);
                var py = (int)((sy + 0.5f) * imgH / height);
                px = Math.Clamp(px, 0, imgW - 1);
                py = Math.Clamp(py, 0, imgH - 1);

                var c = image.GetPixel(px, py);
                cells[x, y] = (c.R - c.B) > YellownessThreshold ? Cell.Terrain : Cell.Grass;
            }
        }

        return cells;
    }

    private static Image? LoadDesignImage()
    {
        // Prefer the imported texture (so it honours the project's import pipeline); fall back to a raw file
        // load if the .import sidecar hasn't been generated yet.
        var tex = ResourceLoader.Load<Texture2D>(DesignBitmapPath);
        if (tex is not null)
        {
            var img = tex.GetImage();
            if (img is not null)
            {
                return img;
            }
        }

        var image = new Image();
        var globalPath = ProjectSettings.GlobalizePath(DesignBitmapPath);
        var err = image.Load(globalPath);
        if (err == Error.Ok)
        {
            return image;
        }

        GD.PushWarning($"S112 TerrainPainter: design bitmap '{DesignBitmapPath}' not found/loadable " +
                       $"(err={err}); falling back to all-grass floor.");
        return null;
    }

    // ---- Tile textures (used directly as StandardMaterial3D albedo) ---------------------------------------
    // Load the imported Texture2D for each layer file. Used directly as material albedo (rendering handles any
    // VRAM compression natively — no GetImage/Decompress/Texture2DArray needed). Returns null if any is missing
    // so the caller keeps the grid floor as a fallback.
    private static Texture2D[]? LoadTileTextures()
    {
        var textures = new Texture2D[LayerFiles.Length];
        for (var i = 0; i < LayerFiles.Length; i++)
        {
            var path = TileDir + LayerFiles[i];
            var tex = ResourceLoader.Load<Texture2D>(path);
            if (tex is null)
            {
                GD.PushWarning($"S112 TerrainPainter: tile texture '{path}' could not be loaded.");
                return null;
            }

            textures[i] = tex;
        }

        return textures;
    }

    // ---- Autotiling + deterministic variation -----------------------------------------------------------
    // Returns the texture-array layer index and the in-plane (pre-flatten) Basis carrying rotation/flip.
    private static (int layer, Basis basis) ChooseTile(Cell[,] cells, int width, int height, int x, int y)
    {
        var here = cells[x, y];

        var n = IsTerrain(cells, width, height, x, y - 1);
        var s = IsTerrain(cells, width, height, x, y + 1);
        var e = IsTerrain(cells, width, height, x + 1, y);
        var w = IsTerrain(cells, width, height, x - 1, y);

        var h = Hash(x, y);

        // TERRAIN cell: a convex corner (grass on exactly two ADJACENT sides) uses the corner tile with its grass
        // corner facing the grass diagonal. Every other terrain cell is plain middle terrain (no grass, no sides).
        if (here == Cell.Terrain)
        {
            var gn = !n; var gs = !s; var ge = !e; var gw = !w;
            var grassNeighbours = (gn ? 1 : 0) + (gs ? 1 : 0) + (ge ? 1 : 0) + (gw ? 1 : 0);
            if (grassNeighbours == 2 && !(gn && gs) && !(ge && gw))
            {
                return CornerTile(gn, gs, ge, gw);
            }

            return MiddleTile(h);
        }

        // GRASS cell: exactly one terrain side → straight side tile (green toward the grass); open grass → grass
        // tile; anything more enclosed (inner corner / pinch / nearly surrounded) → filled with plain terrain so
        // inner corners read solid.
        var terrainNeighbours = (n ? 1 : 0) + (s ? 1 : 0) + (e ? 1 : 0) + (w ? 1 : 0);
        return terrainNeighbours switch
        {
            0 => GrassTile(h),
            1 => SideTile(n, s, e, w, h),
            _ => MiddleTile(h),
        };
    }

    // Middle terrain, WEIGHTED: terrain_middle_01 appears at ~20% the rate of terrain_middle_02.
    private static (int layer, Basis basis) MiddleTile(uint h)
    {
        var variant = UnitFloat(h) < (0.2f / 1.2f) ? LayerMiddle0 : LayerMiddle0 + 1;
        return (variant, FullTileBasis(h));
    }

    // Open grass, WEIGHTED: grass_03 appears at ~20% the rate of grass_01 / grass_02.
    private static (int layer, Basis basis) GrassTile(uint h)
    {
        var u = UnitFloat(h);
        int variant;
        if (u < (1f / 2.2f)) { variant = LayerGrass0; }          // grass_01
        else if (u < (2f / 2.2f)) { variant = LayerGrass0 + 1; } // grass_02
        else { variant = LayerGrass0 + 2; }                      // grass_03 (~9%)
        return (variant, FullTileBasis(h));
    }

    // Terrain convex corner: the base texture has GRASS on N+W (terrain on S+E). Rotate it (local-Z; a +90° step
    // maps tile edges E→N→W→S) so the GRASS quadrant faces the two adjacent GRASS sides: N+W→0, S+W→1, S+E→2,
    // N+E→3. Args are which sides are GRASS (exactly two adjacent). Only one corner texture, so no variant.
    private static (int layer, Basis basis) CornerTile(bool gn, bool gs, bool ge, bool gw)
    {
        int steps;
        if (gn && gw) { steps = 0; }
        else if (gs && gw) { steps = 1; }
        else if (gs && ge) { steps = 2; }
        else { steps = 3; } // gn && ge
        return (LayerCorner, ZSteps(steps));
    }

    // A stable [0,1) value from the per-tile hash, using high bits so it does not correlate with the low-bit
    // rotation/flip/flavour choices. Drives weighted variant selection.
    private static float UnitFloat(uint h)
    {
        return ((h >> 8) & 0xFFFFu) / 65536f;
    }

    // The side textures are split VERTICALLY in their own UV space: "left" has green (grass) on the LEFT half,
    // "right" has green on the RIGHT half. Orientation is handled entirely in the quad's LOCAL plane (rotation
    // about the quad's local Z normal + optional local mirror), then BuildFloor tips the quad flat. After the
    // flatten, texture U → world +X (east). So with ZERO local rotation:
    //   side_left  → green on the WEST half, terrain half faces EAST  (+X)
    //   side_right → green on the EAST half, terrain half faces WEST  (-X)
    // A +90° rotation about the local Z normal (CCW in the texture plane) maps, after flatten, world directions
    // EAST→NORTH→WEST→SOUTH (each step). So Zsteps advances the faced direction E→N→W→S.
    //   dir index: 0=E, 1=N, 2=W, 3=S  (the order a +90° local-Z step walks through).
    private static (int layer, Basis basis) SideTile(bool n, bool s, bool e, bool w, uint h)
    {
        // Direction the TERRAIN half must face. Plain edge → exactly one neighbour is terrain. Corner (two
        // adjacent cardinals) → pick by fixed priority E>N>W>S (deterministic, best-effort; flagged — there is
        // no corner tile in the set). Indices match the E→N→W→S walk of a +90° local-Z step.
        int targetDir;
        if (e) targetDir = 0;       // E
        else if (n) targetDir = 1;  // N
        else if (w) targetDir = 2;  // W
        else targetDir = 3;         // S

        // side_left un-rotated faces terrain EAST (dir 0). steps to reach target = targetDir.
        var steps = targetDir;

        // Two visual flavours that both satisfy the constraint: side_left rotated to face the target, OR
        // side_right rotated by +2 steps (side_right faces WEST un-rotated = dir 2). Hash bit picks the flavour;
        // another bit picks the _01/_02 variant.
        var useRight = ((h >> 3) & 1u) == 1u;
        // WEIGHTED variant: the _02 side textures appear at ~10% the rate of the _01 ones.
        var variant = UnitFloat(h) < (0.1f / 1.1f) ? 1 : 0;
        int layer;
        int zsteps;
        if (!useRight)
        {
            layer = LayerSideLeft0 + variant;
            zsteps = steps;
        }
        else
        {
            layer = LayerSideRight0 + variant;
            zsteps = (steps + 2) & 3;
        }

        return (layer, ZSteps(zsteps));
    }

    // A Basis rotating `steps` * 90° about the quad's LOCAL Z (its normal). Applied pre-flatten so it rotates
    // the texture within the quad's plane; composes with the -90°-about-X flatten in BuildFloor.
    private static Basis ZSteps(int steps)
    {
        return new Basis(Vector3.Back, (steps & 3) * (Mathf.Pi / 2f));
    }

    // One of the 8 dihedral transforms (4 rotations × optional mirror) for a full tile, baked into the in-plane
    // Basis. Rotation is about the quad's local Z normal; mirror is a negative local-X scale (in-plane flip).
    // Deterministic from the tile hash. Side tiles never use this (mirror/180 would break the green/terrain
    // constraint) — only grass and middle tiles, where any of the 8 is legal.
    private static Basis FullTileBasis(uint h)
    {
        var rot = (int)(h % 4);
        var mirror = ((h >> 2) & 1u) == 1u;
        var basis = ZSteps(rot);
        if (mirror)
        {
            basis *= Basis.FromScale(new Vector3(-1f, 1f, 1f));
        }

        return basis;
    }

    private static bool IsTerrain(Cell[,] cells, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return false; // out-of-bounds reads as grass (no terrain border at the world edge)
        }

        return cells[x, y] == Cell.Terrain;
    }

    // Deterministic, cross-client-identical per-tile hash (integer mix; no Math.random / time). Stable across
    // frames and clients for the same (x,y).
    private static uint Hash(int x, int y)
    {
        unchecked
        {
            var hh = (uint)(x * 0x1f1f1f1f) ^ (uint)(y * 0x85ebca6b);
            hh ^= hh >> 16;
            hh *= 0x7feb352d;
            hh ^= hh >> 15;
            hh *= 0x846ca68b;
            hh ^= hh >> 16;
            return hh;
        }
    }
}
