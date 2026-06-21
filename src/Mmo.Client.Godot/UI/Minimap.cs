using Godot;
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
// Data sources (all REAL, read-only — see HudState):
//   - Static map: HudState.Map (width/height + blocked-tile set), regenerated client-side from the seed (S42).
//     We rasterise it ONCE into an ImageTexture and only re-bake when the map's Generation OR the current zoom
//     scale changes — never per frame. Only the player marker + map offset move each frame.
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

    // Pixels per world tile on the baked map. Larger = more zoomed-in / more detail per wall. The baked texture
    // is (Width*_mapScale) x (Height*_mapScale); a tile is _mapScale px. This is the SINGLE source of truth for
    // scale: the baked walls, the object squares, and the player offset all multiply tile coords by it, so they
    // stay aligned. S110 makes it live-tunable via the +/- buttons (was a const in S109).
    private const int DefaultMapScale = 6;
    private const int MinMapScale = 3;
    private const int MaxMapScale = 16;
    private const int ZoomStep = 2;
    private int _mapScale = DefaultMapScale;

    // Wall + border colours for the simplified raster (contrasting, readable — not a 1:1 terrain texture).
    private static readonly Color MapWall = new(0.62f, 0.66f, 0.72f, 1f);
    private static readonly Color MapBorder = new(0.30f, 0.34f, 0.40f, 1f);

    // Terrain overview colours from the design bitmap (TerrainPainter.LoadTerrainGrid): tan for terrain, green for
    // grass — a simplified read of the painted floor so the minimap conveys the terrain layout.
    private static readonly Color MapTerrain = new(0.80f, 0.73f, 0.55f, 0.92f);
    private static readonly Color MapGrass = new(0.42f, 0.55f, 0.32f, 0.92f);

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
    private int _bakedScale = -1;      // S110: which _mapScale the current _bakedMap was rasterised at (re-bake on change).

    // S110: the last HudState pushed in, retained so a zoom button can re-apply (re-bake + reposition) immediately.
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

        _mapView = new TextureRect
        {
            Name = "MapImage",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Keep,
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

    // S110: apply a zoom delta, clamp to [Min,Max], and (if it actually changed) re-bake the static map ONCE at the
    // new scale and reposition everything via the retained last state. EnsureBaked's scale guard makes the re-bake
    // a no-op when the scale is unchanged, so a clamped-out click costs nothing.
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
            Apply(_lastState); // re-bakes (scale changed) + rescales objects/offset; keeps the player centred.
        }
    }

    // Called from Hud.Refresh once per refresh with the current view-model. Re-bakes the static map only when the
    // map generation OR the zoom scale changes; otherwise just repositions the (already baked) map + objects under
    // the centred arrow.
    public void Apply(HudState state)
    {
        _lastState = state;
        EnsureBaked(state.Map);
        UpdatePlayer(state);
        UpdateObjects(state);
    }

    // Rasterise the static map (walls + bounds) into an ImageTexture ONCE per map+scale. No-op if the same
    // generation AND scale are already baked, so this does NOT run per frame — only the player marker / objects
    // move on a normal frame, and the re-bake only fires on a zone change or a zoom click.
    private void EnsureBaked(HudState.MinimapMap? map)
    {
        if (map is null || _mapView is null)
        {
            return;
        }

        if (_bakedMap is not null && _bakedGeneration == map.Generation && _bakedScale == _mapScale)
        {
            return;
        }

        var w = Mathf.Max(1, map.Width) * _mapScale;
        var h = Mathf.Max(1, map.Height) * _mapScale;
        var image = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);

        // Base layer = the terrain design (tan terrain over green grass) so the minimap conveys the terrain layout.
        // Grass dominates, so fill grass then stamp the (sparser) terrain cells. Same axes as the world/walls below.
        image.Fill(MapGrass);
        var terrain = Mmo.Client.Godot.Visuals.TerrainPainter.LoadTerrainGrid(map.Width, map.Height);
        for (var ty = 0; ty < map.Height; ty++)
        {
            for (var tx = 0; tx < map.Width; tx++)
            {
                if (!terrain[tx, ty])
                {
                    continue;
                }

                var bx = tx * _mapScale;
                var by = ty * _mapScale;
                for (var dy = 0; dy < _mapScale; dy++)
                {
                    for (var dx = 0; dx < _mapScale; dx++)
                    {
                        image.SetPixel(bx + dx, by + dy, MapTerrain);
                    }
                }
            }
        }

        // World bounds: a 1px inner border so the edge of the world reads on the minimap.
        for (var x = 0; x < w; x++)
        {
            image.SetPixel(x, 0, MapBorder);
            image.SetPixel(x, h - 1, MapBorder);
        }

        for (var y = 0; y < h; y++)
        {
            image.SetPixel(0, y, MapBorder);
            image.SetPixel(w - 1, y, MapBorder);
        }

        // Walls: fill each blocked tile as a _mapScale x _mapScale cell. +X = east (right), tile Y = south (down) —
        // the same axes as the world, so the baked image is a direct top-down view (north is up).
        foreach (var tile in map.Blocked)
        {
            if (tile.X < 0 || tile.Y < 0 || tile.X >= map.Width || tile.Y >= map.Height)
            {
                continue;
            }

            var px0 = tile.X * _mapScale;
            var py0 = tile.Y * _mapScale;
            for (var dy = 0; dy < _mapScale; dy++)
            {
                for (var dx = 0; dx < _mapScale; dx++)
                {
                    image.SetPixel(px0 + dx, py0 + dy, MapWall);
                }
            }
        }

        _bakedMap = ImageTexture.CreateFromImage(image);
        _bakedGeneration = map.Generation;
        _bakedScale = _mapScale;
        _mapView.Texture = _bakedMap;
        _mapView.OffsetRight = w;
        _mapView.OffsetBottom = h;
    }

    // Per-frame: translate the baked map so the live player tile sits under the centred arrow, and rotate the arrow
    // to the player's facing. Cheap (a couple of multiplies + a position/rotation set) — the static raster is reused.
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

        if (_mapView is null || _viewport is null || _bakedMap is null || !state.HasLocalPosition)
        {
            return;
        }

        // Player-centred: offset the map so the player's pixel lands at the viewport centre. This SAME offset is
        // applied to the object layer (see UpdateObjects) so objects and walls share one transform.
        _mapView.Position = MapOffset(state);
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

    // The single world->minimap translation for the baked map AND the object layer: shift everything so the local
    // player's tile-centre pixel lands at the inner-viewport centre (player-centred). One formula = no drift.
    private Vector2 MapOffset(HudState state)
    {
        var inner = PanelSize - (FrameInset * 2f);
        var playerPx = (state.LocalX + 0.5f) * _mapScale;
        var playerPy = (state.LocalY + 0.5f) * _mapScale;
        return new Vector2((inner / 2f) - playerPx, (inner / 2f) - playerPy);
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
                // Object footprint in pixels = footprint tiles * scale. Centre the square on the object's world
                // position (tile-centre via +0.5), then apply the shared player-centred map offset. Identical math
                // to the wall/player mapping, so a 2-tile object reads twice the side of a 1-tile one and stays put
                // relative to the walls.
                var sidePx = Mathf.Max(2f, obj.FootprintTiles * _scale);
                var centreX = ((obj.X + 0.5f) * _scale) + _offset.X;
                var centreY = ((obj.Y + 0.5f) * _scale) + _offset.Y;
                var rect = new Rect2(centreX - (sidePx / 2f), centreY - (sidePx / 2f), sidePx, sidePx);
                DrawRect(rect, obj.Depleted ? ObjectDepleted : ObjectAvailable, filled: true);
            }
        }
    }
}
