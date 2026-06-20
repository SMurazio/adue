using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// S96/S100: the pure horizontal-flip rule for the Cato side-view sprite, extracted from the Godot visual so it
// is unit-testable without a Godot dependency. The sprite art faces RIGHT when unflipped; the rule mirrors it
// for screen-left facings and keeps the last horizontal flip for the purely screen-vertical facings.
//
// S100 — CAMERA-relative, not world-X. The orthographic camera is fixed at a 45 deg iso angle (offset
// (24,28,24)), so SCREEN-right does NOT point along world +X. Its screen-right unit direction is world
// (approx +X, -Z) = tile (1, -1) (world NE). The screen-horizontal component of a facing whose world delta is
// (dx, dy) is therefore the projection onto that axis: screenH = dx - dy.
//   * screenH > 0  -> faces screen-right -> normal   (FlipH = false):  N (0,-1), E (1,0),  NE (1,-1)
//   * screenH < 0  -> faces screen-left  -> flipped  (FlipH = true):   S (0,1),  W (-1,0), SW (-1,1)
//   * screenH == 0 -> screen-vertical    -> keep last flip:            NW (-1,-1) screen-up, SE (1,1) screen-down
// The camera is fixed, so this constant projection is exact. If the camera angle ever becomes tunable, revisit.
// A single InvertFlip switch reverses the whole (screen-relative) mapping in one place if the live check shows
// it mirrored.
public static class CatoFacingFlip
{
    // Flip the mapping here (one line) if the art reads mirrored on screen during the live check. false = the
    // art's authored orientation faces screen-right unflipped.
    public const bool InvertFlip = false;

    // Resolve the horizontal flip for a facing, given the previous flip to hold through screen-vertical facings.
    // Returns the flip to apply AND keep as the new "last" value.
    public static bool Resolve(Direction8 facing, bool lastFlipH)
    {
        var delta = facing.Delta();
        // Project the world facing onto the fixed camera's screen-right axis (world tile (1, -1)).
        var screenH = delta.X - delta.Y;
        if (screenH == 0)
        {
            // NW (screen-up) / SE (screen-down): no screen-horizontal component — hold whatever we last latched.
            return lastFlipH;
        }

        // screenH < 0 faces screen-left -> flip; screenH > 0 faces screen-right -> normal.
        var flip = screenH < 0;
        return InvertFlip ? !flip : flip;
    }
}
