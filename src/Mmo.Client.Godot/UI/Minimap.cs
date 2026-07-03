using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.UI;

// S109 (HUD slice 5): the top-right framed minimap. A pure client-side presentation node, mounted by Hud.cs and
// fed from HudState. It shows a SIMPLIFIED top-down view of the current environment (walls + world bounds) plus a
// player arrow that points in the character's facing direction and tracks the live local player position as the
// human walks.
//
// S110: it also plots the world OBJECTS the client knows about (trees/rocks/resource nodes, AOI-scoped) as filled
// squares sized to their footprint, and adds live +/- zoom buttons that change the pixels-per-tile scale without a
// restart.
//
// N (todo/N-minimap-384-bake-cost.md): two build-time fixes once the 384-tile authored world went live.
//   1. The base layer used to re-bake (a full tile scan, per-pixel SetPixel calls) on EVERY zoom click — up to
//      ~5M interop calls at 384x384. It now bakes ONCE per zone at a FIXED internal resolution (BakeScale,
//      independent of the live zoom) into a raw RGBA8 byte buffer (batched array writes, no per-pixel interop),
//      and a zoom click just resizes the TextureRect's display rect — Godot's GPU stretches the SAME texture
//      (StretchMode.Scale + TextureFilter.Nearest keeps tile edges crisp). Safe because every tile is a flat
//      color: up/down-scaling never loses fidelity, it only changes how many screen pixels a tile occupies.
//   2. The base layer used to always read the legacy terrain.png design bitmap — meaningless (and actively
//      misleading) on an AUTHORED (genVersion 2+) zone, whose real road/terrain layout can be tiles away from
//      the bitmap's fictional one. An authored zone now bakes from the SAME per-tile SurfaceCategory palette
//      the 3D floor is painted with (AuthoredSurfaceVisuals.Albedo via MinimapAuthoredPalette, both Godot-free
//      and headlessly tested); genVersion 1 (HudState.Map.Authored is null) keeps the terrain.png path.
//
// Data sources (all REAL, read-only — see HudState):
//   - Static map: HudState.Map (width/height + blocked-tile set + Authored map when genVersion 2+), regenerated
//     client-side from the seed (S42). We rasterise it ONCE into an ImageTexture per Generation — never per
//     frame, never per zoom. Only the player marker + map offset + display size move on a zoom/frame.
//   - Player position/facing: HudState.LocalX/LocalY (continuous render position, X=east, Y=south in tile space)
//     and HudState.LocalFacing (Direction8). Both already client-side; the minimap never touches the snapshot/AOI
//     pipeline or any movement logic.
//   - World objects: HudState.MinimapObjects (continuous world position + footprint + depleted), refilled each
//     refresh by MmoClientRoot from the SAME per-frame render-state list the 3D world uses (read-only).
//
// Layout: PLAYER-CENTRED. The arrow stays pinned at the viewport centre and the baked map (plus the object layer)
// translate underneath it. The map texture and the object layer live inside a clip-contents viewport cut out of
// the frame art and share ONE world->minimap transform (the same MapScale + player offset), so objects, the
// player marker, and the baked walls stay aligned at every zoom level.
public partial class Minimap : Control
{
    private const string FrameTexPath = "res://content/ui/minimap/Minimap_frame.png";
    // NOTE: the space in the filename is intentional and must match the file on disk exactly (case-sensitive).
    private const string ArrowTexPath = "res://content/ui/minimap/Player arrow.png";

    // Overall panel size (the frame art is 280x280). The map viewport is inset by the frame's border thickness.
    private const float PanelSize = 200f;
    // How much of the frame art is decorative border; the live map shows inside this inset (tuned to the art).
    private const float FrameInset = 14f;

    // Pixels per world tile the map is currently DISPLAYED at. Larger = more zoomed-in / more detail per wall.
    // This is the SINGLE source of truth for on-screen scale: the displayed map rect, the object squares, and
    // the player offset all multiply tile coords by it, so they stay aligned. S110 makes it live-tunable via the
    // +/- buttons (was a const in S109). N: this no longer drives the BAKE resolution (see BakeScale) — changing
    // it only resizes the TextureRect's display rect (GPU-stretches the cached texture), never re-bakes.
    private const int DefaultMapScale = 6;
    private const int MinMapScale = 3;
    private const int MaxMapScale = 16;
    private const int ZoomStep = 2;
    private int _mapScale = DefaultMapScale;

    // N: the FIXED pixels-per-tile the base layer is baked at, independent of _mapScale. Baking happens ONCE per
    // zone (see EnsureBaked), so this only has to be "good enough" at every live zoom (3..16) via GPU stretch —
    // and since every tile is a flat color, up/down-scaling from ANY resolution is lossless (no gradients to
    // blur), so a small fixed value keeps the baked buffer small (384x384 tiles * 4 px/tile = a 1536x1536 RGBA8
    // image, ~9 MB) without sacrificing visual fidelity at MaxMapScale.
    private const int BakeScale = 4;

    // Wall + border colours for the simplified raster (contrasting, readable — not a 1:1 terrain texture). Also
    // the "blocked tile" color on AUTHORED zones (N item 2 — "existing wall color").
    private static readonly Color MapWall = new(0.62f, 0.66f, 0.72f, 1f);
    private static readonly Color MapBorder = new(0.30f, 0.34f, 0.40f, 1f);

    // Terrain overview colours from the design bitmap (TerrainPainter.LoadTerrainGrid): tan for terrain, green for
    // grass — a simplified read of the painted floor so the minimap conveys the terrain layout. genVersion 1 ONLY
    // (HudState.Map.Authored is null) — see BakeLegacyBaseLayer. Authored zones use AuthoredSurfaceVisuals.Albedo
    // instead (via MinimapAuthoredPalette), the SAME palette the 3D floor is painted with.
    private static readonly Color MapTerrain = new(0.80f, 0.73f, 0.55f, 0.92f);
    private static readonly Color MapGrass = new(0.42f, 0.55f, 0.32f, 0.92f);

    // N: opacity applied to authored floor tiles when baking — matches MapTerrain/MapGrass's existing 0.92 alpha
    // so an authored zone's minimap keeps the same translucent-overlay look a genVersion 1 zone has.
    private const byte AuthoredFloorAlphaByte = 234; // round(0.92 * 255)

    // S110: object square colours. Available resource = warm amber (reads against the cool grey walls); depleted
    // = dim grey-green so a harvested node is still visible but clearly spent. The depleted/available bit is
    // already on EntityRenderState (read-only), so the tint is trivial.
    private static readonly Color ObjectAvailable = new(0.85f, 0.62f, 0.22f, 0.95f);
    private static readonly Color ObjectDepleted = new(0.40f, 0.46f, 0.40f, 0.80f);

    private TextureRect? _mapView; // holds the baked full-map ImageTexture; translated under the arrow each frame.
    private Control? _viewport;    // clip-contents window inside the frame; the map scrolls within it.
    private ObjectLayer? _objects; // S110: filled-square overlay above the map, below the arrow; shares the map offset.
    private TextureRect? _arrow;   // pinned at the viewport centre, rotated to the player's facing.

    private ImageTexture? _bakedMap;
    private int _bakedGeneration = -1; // which HudState.Map.Generation the current _bakedMap was rasterised from.

    // S110: the last HudState pushed in, retained so a zoom button can re-apply (reposition + resize) immediately.
    private HudState? _lastState;

    public override void _Ready()
    {
        // Anchor the whole panel to the top-right corner of the screen, with a small margin.
        AnchorLeft = 1f;
        AnchorRight = 1f;
        AnchorTop = 0f;
        AnchorBottom = 0f;
        GrowHorizontal = GrowDirection.Begin;
        OffsetLeft = -PanelSize - 16f;
        OffsetTop = 16f;
        OffsetRight = -16f;
        OffsetBottom = 16f + PanelSize;
        CustomMinimumSize = new Vector2(PanelSize, PanelSize);
        MouseFilter = MouseFilterEnum.Ignore;

        var inner = PanelSize - (FrameInset * 2f);

        // The clip window the map scrolls inside. ClipContents keeps the translated map from spilling over the frame.
        _viewport = new Control
        {
            Name = "MapViewport",
            ClipContents = true,
            OffsetLeft = FrameInset,
            OffsetTop = FrameInset,
            OffsetRight = FrameInset + inner,
            OffsetBottom = FrameInset + inner,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_viewport);

        // N: StretchMode.Scale (was .Keep) so the control's rect (set to Width*_mapScale x Height*_mapScale in
        // ApplyDisplayRect) stretches the SAME baked-once texture on the GPU when the live zoom changes — no
        // re-bake. TextureFilter.Nearest keeps tile edges crisp at any stretch factor (every tile is a flat
        // color, so nearest-neighbor loses nothing and avoids the blur a Linear filter would add).
        _mapView = new TextureRect
        {
            Name = "MapImage",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _viewport.AddChild(_mapView);

        // S110: the object overlay. A full-viewport canvas that draws the world-object squares each time the set
        // (or the map offset / zoom) changes. Added AFTER the map so it draws above the walls; the arrow is added
        // last so it draws above the objects. It shares the map's translation so squares track the walls exactly.
        _objects = new ObjectLayer
        {
            Name = "Objects",
            OffsetRight = inner,
            OffsetBottom = inner,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _viewport.AddChild(_objects);

        // The frame art drawn ON TOP of the map so the border sits over the clipped edge.
        var frame = new TextureRect
        {
            Name = "Frame",
            Texture = Load(FrameTexPath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            OffsetRight = PanelSize,
            OffsetBottom = PanelSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(frame);

        // Player arrow, pinned at the viewport centre. PivotOffset = its own half-size so Rotation spins it about
        // its centre. The art points UP (north) at zero rotation (tip is the narrow top end), so a Direction8 maps
        // straight to clockwise rotation: N=0, E=+90deg, S=+180deg, W=+270deg (see UpdatePlayer).
        _arrow = new TextureRect
        {
            Name = "Arrow",
            Texture = Load(ArrowTexPath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        // Render the arrow a touch larger than its 14x28 native size so it reads on the map.
        const float arrowW = 18f;
        const float arrowH = 28f;
        _arrow.OffsetRight = arrowW;
        _arrow.OffsetBottom = arrowH;
        _arrow.PivotOffset = new Vector2(arrowW / 2f, arrowH / 2f);
        // Centre of the inner viewport.
        _arrow.Position = new Vector2((inner - arrowW) / 2f, (inner - arrowH) / 2f);
        _viewport.AddChild(_arrow);

        BuildZoomControls();
    }

    // S110: two small +/- buttons in the top-right of the panel that change the zoom (pixels per tile) live. They
    // sit ON TOP of the frame art (added to the panel after the viewport/frame) and accept mouse input, so they
    // are reachable in normal, non-admin play — the minimap is a normal HUD element.
    private void BuildZoomControls()
    {
        var cluster = new VBoxContainer
        {
            Name = "ZoomControls",
            OffsetLeft = PanelSize - 30f,
            OffsetTop = 6f,
            OffsetRight = PanelSize - 6f,
            OffsetBottom = 56f,
            MouseFilter = MouseFilterEnum.Pass,
        };
        cluster.AddThemeConstantOverride("separation", 4);

        var plus = MakeZoomButton("MinimapZoomIn", "+");
        plus.Pressed += () => ChangeZoom(+ZoomStep);
        cluster.AddChild(plus);

        var minus = MakeZoomButton("MinimapZoomOut", "−"); // U+2212 MINUS SIGN reads cleaner than a hyphen.
        minus.Pressed += () => ChangeZoom(-ZoomStep);
        cluster.AddChild(minus);

        AddChild(cluster);
    }

    private static Button MakeZoomButton(string name, string text)
    {
        return new Button
        {
            Name = name,
            Text = text,
            CustomMinimumSize = new Vector2(24f, 22f),
            FocusMode = FocusModeEnum.None, // never steal keyboard focus from movement input.
            TooltipText = text == "+" ? "Zoom in" : "Zoom out",
        };
    }

    // S110/N: apply a zoom delta, clamp to [Min,Max], and (if it actually changed) reposition everything via the
    // retained last state. N: this NEVER re-bakes — the baked texture is fixed-resolution (BakeScale) and only
    // the display rect (ApplyDisplayRect) + object/player offsets change, so a zoom click is O(1), not another
    // tile scan.
    private void ChangeZoom(int delta)
    {
        var next = Mathf.Clamp(_mapScale + delta, MinMapScale, MaxMapScale);
        if (next == _mapScale)
        {
            return;
        }

        _mapScale = next;
        if (_lastState is not null)
        {
            Apply(_lastState); // resizes the display rect + rescales objects/offset; keeps the player centred.
        }
    }

    // Called from Hud.Refresh once per refresh with the current view-model. Re-bakes the static map only when the
    // map GENERATION changes (a new zone); the live zoom scale never triggers a re-bake (N — see BakeScale).
    // ApplyDisplayRect then (cheaply, every call) rewrites the already-baked texture's display rect — position
    // AND size together — for the current zoom, and the objects reposition under the centred arrow as before.
    public void Apply(HudState state)
    {
        _lastState = state;
        EnsureBaked(state.Map);
        if (state.Map is not null)
        {
            ApplyDisplayRect(state.Map, state);
        }

        UpdatePlayer(state);
        UpdateObjects(state);
    }

    // Rasterise the static map (walls + bounds) into an ImageTexture ONCE per zone (keyed by Generation only).
    // Builds a raw RGBA8 byte buffer via batched array writes (MinimapRasterBytes / MinimapAuthoredPalette —
    // Godot-free, headlessly tested) instead of per-pixel SetPixel calls, then creates the Image in ONE
    // Image.CreateFromData call. Baked at the FIXED BakeScale resolution, independent of _mapScale — see the
    // class-level N comment and ApplyDisplayRect for how the live zoom is applied without re-baking.
    private void EnsureBaked(HudState.MinimapMap? map)
    {
        if (map is null || _mapView is null)
        {
            return;
        }

        if (_bakedMap is not null && _bakedGeneration == map.Generation)
        {
            return;
        }

        var w = Mathf.Max(1, map.Width) * BakeScale;
        var h = Mathf.Max(1, map.Height) * BakeScale;

        // N item 2: an AUTHORED zone (genVersion 2+) bakes from the SAME SurfaceCategory palette the 3D floor
        // is painted with, so the minimap shows the real ground truth instead of the legacy terrain.png bitmap.
        // genVersion 1 (Authored is null) keeps the pre-N terrain.png read, unchanged.
        var bytes = map.Authored is { } authored
            ? MinimapAuthoredPalette.BakeBaseLayer(authored, BakeScale, ToRgba(MapWall), AuthoredFloorAlphaByte)
            : BakeLegacyBaseLayer(map, BakeScale);

        var (br, bg, bb, ba) = ToRgba(MapBorder);
        MinimapRasterBytes.StampBorder(bytes, w, h, br, bg, bb, ba);

        var image = Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
        _bakedMap = ImageTexture.CreateFromImage(image);
        _bakedGeneration = map.Generation;
        _mapView.Texture = _bakedMap;
    }

    // genVersion 1 (no authored map): the pre-N terrain design (tan terrain over green grass, from the design
    // bitmap TerrainPainter.LoadTerrainGrid) plus walls — identical colors/layout to the old per-pixel version,
    // just batched into a raw byte buffer instead of SetPixel/GetPixel interop calls.
    private static byte[] BakeLegacyBaseLayer(HudState.MinimapMap map, int scale)
    {
        var pxWidth = Mathf.Max(1, map.Width) * scale;
        var pxHeight = Mathf.Max(1, map.Height) * scale;
        var bytes = new byte[pxWidth * pxHeight * 4];

        // Grass dominates, so fill grass then stamp the (sparser) terrain cells over it — same as the old
        // image.Fill(MapGrass) + per-cell SetPixel loop, now array writes. Same axes as the walls below.
        var (gr, gg, gb, ga) = ToRgba(MapGrass);
        MinimapRasterBytes.FillAll(bytes, gr, gg, gb, ga);

        var terrain = Mmo.Client.Godot.Visuals.TerrainPainter.LoadTerrainGrid(map.Width, map.Height);
        var (tr, tg, tb, ta) = ToRgba(MapTerrain);
        for (var ty = 0; ty < map.Height; ty++)
        {
            for (var tx = 0; tx < map.Width; tx++)
            {
                if (terrain[tx, ty])
                {
                    MinimapRasterBytes.StampBlock(bytes, pxWidth, tx * scale, ty * scale, scale, tr, tg, tb, ta);
                }
            }
        }

        // Walls: fill each blocked tile as a scale x scale cell. +X = east (right), tile Y = south (down) — the
        // same axes as the world, so the baked image is a direct top-down view (north is up).
        var (wr, wg, wb, wa) = ToRgba(MapWall);
        foreach (var tile in map.Blocked)
        {
            if (tile.X < 0 || tile.Y < 0 || tile.X >= map.Width || tile.Y >= map.Height)
            {
                continue;
            }

            MinimapRasterBytes.StampBlock(bytes, pxWidth, tile.X * scale, tile.Y * scale, scale, wr, wg, wb, wa);
        }

        return bytes;
    }

    // N: the display rect the baked texture is stretched into (StretchMode.Scale) — the live-zoom half of the
    // world->minimap transform. Runs every Apply() call (cheap: a few field writes), independent of
    // EnsureBaked's generation-gated re-bake, so a zoom click resizes the SAME cached texture instead of
    // rebuilding it.
    //
    // TRANSFORM FIX (live repro 2026-07-03, review/review-request-minimap-transform-fix.md): Size and Position
    // are written TOGETHER here, both derived from the ONE pure transform (MinimapTransform.DisplayRect,
    // headlessly tested). ROOT CAUSE of the shipped bug: the old code wrote the size as ABSOLUTE edge offsets
    // (OffsetRight/OffsetBottom = Width*_mapScale) while OffsetLeft/OffsetTop still held the PREVIOUS frame's
    // player-centring translation, and UpdatePlayer then wrote Position — which in Godot 4 PRESERVES the (now
    // corrupted) size by rewriting all four offsets. From the second frame on the map rendered at
    // (Width*scale - prevOffset.x, Height*scale - prevOffset.y): an anisotropic, player-position-dependent
    // stretch (approaching ~2x per axis at far coords) that dragged the viewed window toward the map origin —
    // mid-world the whole viewport landed ~60 tiles away on featureless grass ("minimap is empty/dark", live
    // repro A) while the object layer, which has its own correct transform, kept plotting dots. NEVER mix
    // absolute edge-offset writes with Position writes on the same control: a Control's four offsets are one
    // coupled rect — set the whole rect, from one formula, in one place.
    private void ApplyDisplayRect(HudState.MinimapMap map, HudState state)
    {
        if (_mapView is null)
        {
            return;
        }

        var inner = PanelSize - (FrameInset * 2f);
        var (x, y, w, h) = MinimapTransform.DisplayRect(
            map.Width, map.Height, _mapScale, state.LocalX, state.LocalY, inner);

        // Size first, then Position: each Godot setter rewrites all four offsets keeping the other property,
        // so after this pair the rect is EXACTLY (x, y, w, h) regardless of any previous state.
        _mapView.Size = new Vector2(w, h);
        if (_bakedMap is not null && state.HasLocalPosition)
        {
            // Player-centred translation — the SAME offset UpdateObjects hands the object layer, so walls,
            // object squares, and the pinned arrow share one transform. No local position yet -> keep the
            // last translation (the size above stays correct either way).
            _mapView.Position = new Vector2(x, y);
        }
    }

    private static (byte R, byte G, byte B, byte A) ToRgba(Color c)
    {
        return (Quantize(c.R), Quantize(c.G), Quantize(c.B), Quantize(c.A));
    }

    private static byte Quantize(float channel)
    {
        return (byte)Mathf.Clamp(Mathf.RoundToInt(channel * 255f), 0, 255);
    }

    // Per-frame: rotate the arrow to the player's facing. The map's player-centred translation is written in
    // ApplyDisplayRect, TOGETHER with the display size — never here (see the TRANSFORM FIX comment there for
    // why a lone Position write on the map view is exactly how the live placement bug shipped).
    private void UpdatePlayer(HudState state)
    {
        if (_arrow is null)
        {
            return;
        }

        // Facing -> rotation. The arrow art points UP (north) at rotation 0, and Direction8 is ordered clockwise
        // from N (N=0, NE=1, E=2, ... NW=7) at 45deg steps, which is exactly Godot's clockwise screen rotation
        // (Y-down). So rotation = ordinal * 45deg. "Facing up in the world" therefore reads as up on the map.
        _arrow.Rotation = (int)state.LocalFacing * Mathf.Pi / 4f;
    }

    // S110: push the current object set + the map offset onto the object overlay so its squares track the walls.
    private void UpdateObjects(HudState state)
    {
        if (_objects is null || _viewport is null)
        {
            return;
        }

        // No baked map / no local position yet: nothing to anchor objects to — clear so we don't draw stale squares.
        var offset = (_bakedMap is not null && state.HasLocalPosition) ? MapOffset(state) : (Vector2?)null;
        _objects.SetData(state.MinimapObjects, offset, _mapScale);
    }

    // The single world->minimap translation for the baked map AND the object layer: shift everything so the
    // local player's pixel lands at the inner-viewport centre (player-centred). One formula = no drift — it
    // lives in the Godot-free MinimapTransform so it is headlessly pinned by MinimapTransformTests.
    // TRANSFORM FIX (secondary find): LocalX/LocalY are CONTINUOUS coords (a player standing on tile (19,24)
    // is at (19.5, 24.5)); the old formula added ANOTHER +0.5 — a leftover from the integer-tile era —
    // displacing the whole map/object plane half a tile against reality. The +0.5 now lives only where tile
    // INDICES are converted to centres (MinimapTransform.TileCentrePixel).
    private Vector2 MapOffset(HudState state)
    {
        var (x, y) = MinimapTransform.MapOffset(
            _mapScale, state.LocalX, state.LocalY, PanelSize - (FrameInset * 2f));
        return new Vector2(x, y);
    }

    private static Texture2D? Load(string path)
    {
        var tex = GD.Load<Texture2D>(path);
        if (tex is null)
        {
            GD.PushWarning($"S109 minimap: texture failed to load: {path} (was the headless --import run?)");
        }

        return tex;
    }

    // S110: the object overlay node. Draws each world object as a filled square in viewport-local pixels, using the
    // SAME (offset + _mapScale) transform the baked map uses, so squares line up with the walls and the player at
    // every zoom. ClipContents on the parent viewport already keeps off-screen squares from spilling over the frame;
    // we redraw only when the data/offset/scale actually changes (cheap, AOI-bounded object count).
    private sealed partial class ObjectLayer : Control
    {
        private readonly System.Collections.Generic.List<HudState.MinimapObject> _items = new();
        private Vector2 _offset;
        private bool _visible;
        private float _scale = DefaultMapScale;

        public ObjectLayer()
        {
            ClipContents = false; // the parent viewport clips; the layer itself spans the inner viewport.
        }

        // Copy the current objects + the shared map offset + scale, then redraw if anything changed. Copying keeps
        // the layer independent of HudState's churned list and lets _Draw run on Godot's schedule.
        public void SetData(
            System.Collections.Generic.IReadOnlyList<HudState.MinimapObject> items, Vector2? offset, int scale)
        {
            _items.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                _items.Add(items[i]);
            }

            _offset = offset ?? Vector2.Zero;
            _visible = offset.HasValue;
            _scale = scale;
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (!_visible)
            {
                return;
            }

            for (var i = 0; i < _items.Count; i++)
            {
                var obj = _items[i];
                // Object footprint in pixels = footprint tiles * scale. Centre the square on the object's
                // CONTINUOUS world position, then apply the shared player-centred map offset — the same
                // WorldPixel math MinimapTransform defines (obj.X is already continuous; the old +0.5 here was
                // the integer-tile-era convention, removed in lockstep with MapOffset's half-tile fix so the
                // squares stay glued to the walls). Identical math to the wall/player mapping, so a 2-tile
                // object reads twice the side of a 1-tile one and stays put relative to the walls.
                var sidePx = Mathf.Max(2f, obj.FootprintUnits * _scale);
                var centreX = (obj.X * _scale) + _offset.X;
                var centreY = (obj.Y * _scale) + _offset.Y;
                var rect = new Rect2(centreX - (sidePx / 2f), centreY - (sidePx / 2f), sidePx, sidePx);
                DrawRect(rect, obj.Depleted ? ObjectDepleted : ObjectAvailable, filled: true);
            }
        }
    }
}
