namespace Mmo.Shared.Domain.Actions;

// MOVEMENT-ACTIONS (Phase A): the ground-height HOOK (design §1.4.4). The world is FLAT today — ground height is 0
// everywhere — so this returns 0 for every XY. It exists as a clean SEAM so real elevation slots in later WITHOUT a
// redesign: when per-tile/per-region world height arrives, GroundHeightAt reads it and the SAME ballistic executor
// lands an entity on a ledge or at the bottom of a drop (only the boundary condition — where z stops — moves; the
// arc math in BallisticArc is unchanged). Phase A deliberately does NOT implement world height (that is a separate
// future feature with gap/void tiles + ledge collision). Shared because both the server executor and the Phase-B
// client predictor must agree on the landing height — today trivially (both read 0), tomorrow from the same data.
public static class GroundHeight
{
    // The ground-plane height at world XY. Returns 0 EVERYWHERE today (flat world). Do not implement world height
    // here — that is the future-elevation feature; this is only the seam it will plug into.
    public static double GroundHeightAt(WorldVector position) => 0d;
}
