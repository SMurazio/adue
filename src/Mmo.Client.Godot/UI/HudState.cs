using System.Collections.Generic;
using Mmo.Shared.Domain;

namespace Mmo.Client.Godot.UI;

// S107 (HUD slice 1): the single client-side view-model the HUD renders from. The HUD reads ONLY from this
// object; MmoClientRoot (the additive hook) populates it from already-available, read-only client state, and
// everything not yet replicated is stubbed with local/placeholder values. This is the seam that keeps the
// ui/hud branch off the server and off the protocol entirely — see docs/hud-ui-design.md ("The HudState seam").
//
// Presentation-only: nothing here writes back into Mmo.Client.Core or the movement/snapshot pipeline. Fields
// marked TODO(server) are placeholders for data that a FUTURE branch will replicate; this slice never touches it.
public sealed class HudState
{
    // The portrait state machine from the mockup (docs/hud-ui-design.md): plain character, character + mount,
    // or a low-health variant (red ring + worried face). Derived from health on this branch (see PortraitState
    // helper), stubbed until real health/mount replication exists.
    public enum Portrait
    {
        Normal,
        Mount,
        LowHealth,
    }

    // --- Vitals -------------------------------------------------------------------------------------------
    // TODO(server): stubbed — no health/resource replication exists on the client yet. Varied live via the F5
    // debug control so later slices (vitals bars + portrait) can be exercised without a server.
    public float Health { get; set; } = 75f;
    public float MaxHealth { get; set; } = 100f;
    public float Resource { get; set; } = 40f;
    public float MaxResource { get; set; } = 100f;

    // --- Cooldowns ----------------------------------------------------------------------------------------
    // TODO(server): client-local timers only on this branch (S-HUD-3 will start them on keypress/click). Maps a
    // slot id (e.g. "Q", "E", "F", "R", "1", "2", "LMB", "RMB") to remaining cooldown seconds. Empty == all ready.
    // The HUD seeds its SlotButton timers from this map; per-frame countdown then runs client-only inside the
    // SlotButtons' _Process (see UI/SlotButton.cs), so this dictionary is only the START value, not ticked here.
    public Dictionary<string, float> Cooldowns { get; } = new();

    // --- Action-bar slot data (S108) ----------------------------------------------------------------------
    // TODO(server): stack counts are stubbed. The two consumable slots (keys "1","2") show a bottom-right stack
    // badge; later slices feed these from the real client item registry (S37-S39). -1 == hide the badge.
    public Dictionary<string, int> Counts { get; } = new();

    // TODO(server): the currently selected/active spell slot id (mockup shows "R"/Ultimate selected). Stubbed
    // here; a future ability system makes this server-authoritative. Empty string == nothing selected.
    public string SelectedSlot { get; set; } = "R";

    // --- Portrait -----------------------------------------------------------------------------------------
    // TODO(server): the mount flag is stubbed (no mount system). The effective portrait is derived from health +
    // this flag via PortraitState() so the derivation lives in one place and downstream slices share it.
    public bool Mounted { get; set; }

    // Below this fraction of max health the portrait shows the low-health variant (docs: low-health <25%).
    public const float LowHealthFraction = 0.25f;

    // The effective portrait state, derived from the (stubbed) vitals + mount flag. Mount takes precedence over
    // low-health to match the mockup's sub-badge behaviour; tweak in S-HUD-2 if the art dictates otherwise.
    public Portrait PortraitState()
    {
        if (Mounted)
        {
            return Portrait.Mount;
        }

        if (MaxHealth > 0f && Health / MaxHealth < LowHealthFraction)
        {
            return Portrait.LowHealth;
        }

        return Portrait.Normal;
    }

    // --- Minimap (local player) ---------------------------------------------------------------------------
    // REAL on this branch: the local player's continuous rendered position (X = east, Y = south in tile space)
    // and last-sent facing, both already client-side and read-only. The minimap uses ONLY this local sample —
    // never the snapshot/AOI pipeline. Has a value only once the local entity has a render state this frame.
    public bool HasLocalPosition { get; set; }
    public float LocalX { get; set; }
    public float LocalY { get; set; }
    public Direction8 LocalFacing { get; set; }

    // --- Minimap (world objects, S110) --------------------------------------------------------------------
    // REAL, read-only: the trees/rocks/resource-nodes the client currently knows about (AOI-scoped — only the
    // "current environment"). MmoClientRoot rebuilds this list each refresh from the same per-frame render-state
    // collection the 3D world renders from (read-only — no movement/world state is mutated). The minimap plots
    // each as a filled square sized to its footprint, through the SAME world->minimap transform the player marker
    // uses. Cleared + refilled in place so we don't churn allocations every frame.
    public List<MinimapObject> MinimapObjects { get; } = new();

    // One world object on the minimap. X/Y are the object's continuous world position (tile space; X=east,
    // Y=south) — identical axes to LocalX/LocalY so objects line up with the player. FootprintTiles is the side
    // length (in world tiles) of the square to draw — a 2-tile object reads as twice a 1-tile one. Depleted lets
    // the minimap tint a harvested node distinctly from an available one.
    public readonly record struct MinimapObject(float X, float Y, float FootprintTiles, bool Depleted);

    // --- Minimap (static environment, S109) ---------------------------------------------------------------
    // REAL, read-only: the static map the minimap rasterises ONCE (walls + world bounds). The world is
    // local/seed-based (S42) — the client already regenerates the blocked-tile set into a ZoneModel, so we
    // expose it here as a read-only snapshot for the minimap to bake. MmoClientRoot sets this once when the
    // zone is built; the minimap detects the change (Generation bump) and re-bakes its ImageTexture, never
    // per frame. Null until the zone exists. Nothing here is written back into movement/world state.
    public MinimapMap? Map { get; set; }

    // An immutable description of the static map for the minimap to bake from. Width/Height are the tile grid
    // extents; Blocked is the wall set (tile coords). Generation lets the minimap cheaply detect a new map.
    public sealed record MinimapMap(int Width, int Height, IReadOnlySet<TileCoord> Blocked, int Generation);

    // --- Inventory ----------------------------------------------------------------------------------------
    // TODO(server): placeholder reference only. The inventory grid (S-HUD-4) reads the EXISTING client item
    // registry + InventoryUpdate data (S37-S39) — already real — so this seam is wired in that slice, not here.
    // Typed as object so this scaffold slice adds no new dependency on the inventory model.
    public object? Inventory { get; set; }
}
