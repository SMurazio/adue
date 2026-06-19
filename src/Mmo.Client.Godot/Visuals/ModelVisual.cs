using Godot;
using Mmo.Client.Core;

namespace Mmo.Client.Godot.Visuals;

// A static / variant GLB grounded on the tile plane. Covers three configured archetypes:
//   * Rock     — one of three variant models chosen deterministically by NetworkId hash, with a per-node
//                spin, a per-variant ground offset, the live-tunable RockModelScale, and the depleted hide
//                (S58). Verbatim behaviour from MmoClientRoot.TryCreateRockNode / the rock UpdateEntities arm.
//   * Tree     — the single alberello model, grounded + scaled, with the same depleted hide (it is a
//                harvestable "Tree" resource, replacing the old box).
//   * Portal   — the single portalemagico model, decorative: grounded + scaled, NO depleted/harvest (it has
//                no server resource entity today; wired so a "Portal"-named entity would render).
//
// The model variant/scale/offset config is data on the instance (chosen by the factory), so adding another
// static model later is one more factory entry reusing this class — no new subclass.
public sealed partial class ModelVisual : EntityVisual
{
    // ---- Rock variant config (S58) -----------------------------------------------------------------
    // Native bounds (grid = 1 unit/tile); each *GroundOffset is the base offset (-Ymin) at scale 1, multiplied
    // by the live RockModelScale so the base stays on the floor at any scale.
    private const string RockMossPath = "res://content/resources/M_Rock_Moss_Overgrowth.glb";
    private const float RockMossGroundOffset = 0.32f;
    private const string RockFloatingPath = "res://content/resources/M_Rock_Floating_Monolith.glb";
    private const float RockFloatingGroundOffset = 0.49f;
    private const string RockEngravedPath = "res://content/resources/M_Rock_Engraved_Monolith_L.glb";
    private const float RockEngravedGroundOffset = 0.96f;
    // Park the label above the tallest variant (engraved ~1.34 tiles) so it clears every rock. TUNABLE.
    private const float RockLabelHeight = 1.5f;

    // ---- Tree config (alberello, NEW) --------------------------------------------------------------
    // Native H~1.43, Ymin -0.61 (grid = 1 unit/tile). Scale 1.2 ≈ 1.72 tiles tall; ground offset 0.61 × scale
    // drops the trunk base onto y=0. First-guess sizing — human eyeballs on relaunch. TUNABLE.
    private const string TreePath = "res://content/resources/alberello.glb";
    private const float TreeScale = 1.2f;
    private const float TreeGroundOffset = 0.61f;
    private const float TreeLabelHeight = 1.9f;

    // ---- Portal config (portalemagico, NEW) --------------------------------------------------------
    // Native H~1.49, Ymin -0.74. Scale 1.4 ≈ 2.09 tiles tall (a portal should read large); ground offset
    // 0.74 × scale. Decorative — no harvest/depleted. First-guess sizing. TUNABLE.
    private const string PortalPath = "res://content/props/portalemagico.glb";
    private const float PortalScale = 1.4f;
    private const float PortalGroundOffset = 0.74f;
    private const float PortalLabelHeight = 2.3f;

    // Three rock scenes loaded once on first rock spawn and cached. A failed load is logged once and disables
    // all rock models for the session (rocks then fall back to the box, same posture as the player fallback).
    private static readonly PackedScene?[] _rockScenes = new PackedScene?[3];
    private static bool _rockLoadAttempted;
    private static bool _rockLoadFailed;

    // Single-model (Tree/Portal) scenes, loaded once and cached.
    private static PackedScene? _treeScene;
    private static bool _treeLoadAttempted;
    private static PackedScene? _portalScene;
    private static bool _portalLoadAttempted;

    private ModelKind _kind;
    private float _labelHeight = RockLabelHeight;
    private Node3D? _model;

    protected override float LabelHeight => _labelHeight;

    private enum ModelKind { Rock, Tree, Portal }

    // ---- factory entry points: each returns a configured instance (asset loaded), or null on load failure
    // so the factory falls back to the box ----------------------------------------------------------

    public static ModelVisual? CreateRock(EntityRenderState state)
    {
        // NetworkId % 3 alone doesn't vary (resource ids share a residue), so mix the id to distribute BOTH
        // the variant AND the yaw while staying deterministic (identical across clients).
        var hash = MixId(state.NetworkId);
        var variant = (int)(hash % 3u);
        return LoadRockScene(variant) is null
            ? null
            : new ModelVisual { _kind = ModelKind.Rock, _variant = variant, _hash = hash, _labelHeight = RockLabelHeight };
    }

    public static ModelVisual? CreateTree()
    {
        return LoadSingleScene(TreePath, ref _treeScene, ref _treeLoadAttempted, "S61 tree") is null
            ? null
            : new ModelVisual { _kind = ModelKind.Tree, _labelHeight = TreeLabelHeight };
    }

    public static ModelVisual? CreatePortal()
    {
        return LoadSingleScene(PortalPath, ref _portalScene, ref _portalLoadAttempted, "S61 portal") is null
            ? null
            : new ModelVisual { _kind = ModelKind.Portal, _labelHeight = PortalLabelHeight };
    }

    private int _variant;
    private uint _hash;

    protected override void BuildChildren()
    {
        var scene = _kind switch
        {
            ModelKind.Rock => LoadRockScene(_variant),
            ModelKind.Tree => _treeScene,
            _ => _portalScene
        };
        if (scene is null || scene.Instantiate() is not Node3D model)
        {
            return;
        }

        model.Name = "Model";
        var (scale, groundOffset) = _kind switch
        {
            ModelKind.Rock => (Tuning.RockModelScale, RockGroundOffset(_variant)),
            ModelKind.Tree => (TreeScale, TreeGroundOffset),
            _ => (PortalScale, PortalGroundOffset)
        };
        model.Scale = new Vector3(scale, scale, scale);
        // Ground offset scales with the model so the base stays on the floor at any scale.
        model.Position = new Vector3(0f, groundOffset * scale, 0f);
        if (_kind == ModelKind.Rock)
        {
            // Deterministic per-node spin around up so rocks don't all face the same way (decorrelated from
            // the variant by dividing out the % 3).
            model.RotationDegrees = new Vector3(0f, (_hash / 3u) % 360u, 0f);
        }

        AddChild(model);
        _model = model;
    }

    protected override void OnAcquire(EntityRenderState state)
    {
        // Rock scale is live-tunable; re-apply on (re)acquire so a pooled rock reflects the current scale.
        if (_kind == ModelKind.Rock && _model is not null)
        {
            _model.Scale = new Vector3(Tuning.RockModelScale, Tuning.RockModelScale, Tuning.RockModelScale);
            _model.Position = new Vector3(0f, RockGroundOffset(_variant) * Tuning.RockModelScale, 0f);
        }

        // Harvestable models (Rock/Tree) start hidden if spawned already depleted; the decorative Portal
        // ignores the bit and is always visible.
        if (_model is not null)
        {
            _model.Visible = _kind == ModelKind.Portal || !state.Depleted;
        }
    }

    protected override void OnUpdate(EntityRenderState state, double now)
    {
        if (_model is null || _kind == ModelKind.Portal)
        {
            return;
        }

        // Rock/Tree rendered as a static GLB (no override material to grey): drive availability off the
        // replicated Depleted bit — hide when harvested, show again on respawn. No prediction.
        _model.Visible = !state.Depleted;
    }

    private static float RockGroundOffset(int variant)
    {
        return variant switch
        {
            0 => RockMossGroundOffset,
            1 => RockFloatingGroundOffset,
            _ => RockEngravedGroundOffset
        };
    }

    // Avalanche bit-mix (Murmur-style) so a sequential / type-clustered NetworkId yields a well-distributed
    // variant + yaw. Deterministic — same id, same result on every client.
    private static uint MixId(uint id)
    {
        id ^= id >> 16;
        id *= 0x7feb352du;
        id ^= id >> 15;
        id *= 0x846ca68bu;
        id ^= id >> 16;
        return id;
    }

    // Loads (once) and caches the rock PackedScene for the variant (0=moss, 1=floating, 2=engraved). A failed
    // load is logged once and disables all rock models for the session (rocks fall back to the box).
    public static PackedScene? LoadRockScene(int variant)
    {
        if (_rockLoadFailed)
        {
            return null;
        }

        if (!_rockLoadAttempted)
        {
            _rockLoadAttempted = true;
            _rockScenes[0] = GD.Load<PackedScene>(RockMossPath);
            _rockScenes[1] = GD.Load<PackedScene>(RockFloatingPath);
            _rockScenes[2] = GD.Load<PackedScene>(RockEngravedPath);
            if (_rockScenes[0] is null || _rockScenes[1] is null || _rockScenes[2] is null)
            {
                _rockLoadFailed = true;
                GD.PushWarning("S58: could not load one or more rock models; rocks fall back to the box.");
                return null;
            }
        }

        return _rockScenes[variant];
    }

    private static PackedScene? LoadSingleScene(string path, ref PackedScene? cache, ref bool attempted, string tag)
    {
        if (attempted)
        {
            return cache;
        }

        attempted = true;
        cache = GD.Load<PackedScene>(path);
        if (cache is null)
        {
            GD.PushWarning($"{tag}: could not load model '{path}'; falling back to the box.");
        }

        return cache;
    }
}
