using Godot;
using Mmo.Client.Core;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.Visuals;

// The single place that maps an entity to its visual class. Stage 1 keeps the CURRENT string-based dispatch
// (EntityKind + DisplayName) — Stage 2 replaces the body of ChooseArchetype with a server-sent VisualArchetype
// without touching the renderer or the visual classes.
//
// Forward-compatible: an unknown archetype OR a failed asset load falls back to BoxVisual and never crashes.
// The pool keys off the archetype the factory chose, so Create returns both the archetype and the configured
// visual.
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

        // LOOT P4b: a dropped corpse renders as the distinct ground sack (BoxVisual's Corpse path), not a capsule.
        if (state.Kind == EntityKind.Corpse)
        {
            return VisualArchetype.Corpse;
        }

        // DUO-SKILLSHOT: a projectile renders as the small bright sphere (BoxVisual's projectile path), tinted+scaled
        // by the server's replicated per-tier visual.
        if (state.Kind == EntityKind.Projectile)
        {
            return VisualArchetype.Projectile;
        }

        // NODE-FIELD N2/N3 (docs/node-field-design.md D3/D6): House/Portal are the only Resource-kind entities
        // the server still spawns (SpawnAuthoredProps) — harvestable Tree/Rock/Plant nodes are catalogue-only
        // now (NodeFieldPainter renders them, never per-entity), so this dispatch no longer has Rock/Tree cases.
        if (state.Kind == EntityKind.Resource)
        {
            return state.DisplayName switch
            {
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
            VisualArchetype.Portal => ModelVisual.CreatePortal(),
            VisualArchetype.HouseSprite => SpriteVisual.LoadTexture() is null ? null : new SpriteVisual(),
            // LOOT P4b: the corpse sack is a BoxVisual variant (it keys off EntityKind.Corpse internally for the
            // distinct low dark mesh), so it shares the pooled-box machinery and never needs an asset load.
            VisualArchetype.Corpse => new BoxVisual(),
            // DUO-SKILLSHOT: the projectile sphere is a BoxVisual variant (keys off EntityKind.Projectile for the
            // distinct bright sphere mesh + unshaded material), so it shares the pooled-box machinery, no asset load.
            VisualArchetype.Projectile => new BoxVisual(),
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
