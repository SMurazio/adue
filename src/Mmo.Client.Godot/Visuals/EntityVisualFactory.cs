using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// The single place that maps an entity to its visual class. Stage 1 keeps the CURRENT string-based dispatch
// (EntityKind + DisplayName) — Stage 2 replaces the body of ChooseArchetype with a server-sent VisualArchetype
// without touching the renderer or the visual classes.
//
// Forward-compatible: an unknown archetype OR a failed asset load falls back to BoxVisual and never crashes
// (the existing player/rock posture). The pool keys off the archetype the factory chose, so Create returns
// both the archetype and the configured visual.
public sealed class EntityVisualFactory
{
    private readonly VisualTuning _tuning;
    private bool _loggedFallback;

    public EntityVisualFactory(VisualTuning tuning)
    {
        _tuning = tuning;
    }

    // Stage-1 dispatch. Resource subtype is still inferred from the replicated DisplayName string (the S58
    // fragility — no kind/subtype field in the protocol yet; that is the Stage 2 VisualArchetype on the wire).
    // S73: when the F5 "Debug facing box" toggle is on, a Player resolves to the debug box+arrow archetype
    // instead of the model rig — diagnostic-only, default off. S96: when the F5 "Cato sprite (player)" toggle is
    // on (and the debug box is off), a Player resolves to the Cato AnimatedSprite3D billboard instead. Precedence
    // for a Player: DebugFacingBox > CatoSprite > Player.
    public static VisualArchetype ChooseArchetype(
        EntityRenderState state, bool debugFacingBox = false, bool useCatoSprite = false)
    {
        if (state.Kind == EntityKind.Player)
        {
            return debugFacingBox
                ? VisualArchetype.DebugFacingBox
                : useCatoSprite ? VisualArchetype.CatoSprite : VisualArchetype.Player;
        }

        if (state.Kind == EntityKind.Resource)
        {
            return state.DisplayName switch
            {
                "Rock" => VisualArchetype.Rock,
                "Tree" => VisualArchetype.Tree,
                // No server entity is named Portal/House today; wired so content that adds one renders.
                "Portal" => VisualArchetype.Portal,
                "House" => VisualArchetype.HouseSprite,
                _ => VisualArchetype.Box
            };
        }

        // NPCs and anything else fall back to the box for now.
        return VisualArchetype.Box;
    }

    // Build a configured visual for the chosen archetype. An archetype whose asset failed to load falls back
    // to a Box (logged once) so a missing/broken asset never crashes or blanks the world.
    public EntityVisual Create(VisualArchetype archetype, EntityRenderState state)
    {
        var visual = Build(archetype, state);
        if (visual is null)
        {
            LogFallbackOnce(archetype);
            visual = new BoxVisual();
            archetype = VisualArchetype.Box;
        }

        visual.Configure(archetype, _tuning);
        return visual;
    }

    private EntityVisual? Build(VisualArchetype archetype, EntityRenderState state)
    {
        return archetype switch
        {
            VisualArchetype.Player => PlayerVisual.LoadModelScene() is null ? null : new PlayerVisual(),
            VisualArchetype.DebugFacingBox => new DebugFacingBoxVisual(),
            VisualArchetype.CatoSprite => CatoSpriteVisual.LoadFrames() is null ? null : new CatoSpriteVisual(),
            // DEBUG-CUBES: Rock/Tree render as the plain resource box (debug cube) instead of the GLB models.
            // BoxVisual keys _isResource off state.Kind == Resource, so they stay green/depleted-aware cubes.
            VisualArchetype.Rock => new BoxVisual(),
            VisualArchetype.Tree => new BoxVisual(),
            VisualArchetype.Portal => ModelVisual.CreatePortal(),
            VisualArchetype.HouseSprite => SpriteVisual.LoadTexture() is null ? null : new SpriteVisual(),
            _ => new BoxVisual()
        };
    }

    private void LogFallbackOnce(VisualArchetype archetype)
    {
        if (_loggedFallback)
        {
            return;
        }

        _loggedFallback = true;
        GD.PushWarning($"S61: archetype '{archetype}' asset unavailable; falling back to the box (logged once).");
    }
}
