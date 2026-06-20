using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// S96: the pure horizontal-flip rule for the Cato side-view sprite, extracted from the Godot visual so it is
// unit-testable without a Godot dependency. The sprite art faces RIGHT (E) when unflipped; the rule mirrors it
// for left-facing directions and keeps the last horizontal flip for the purely vertical facings, exactly as the
// user specified:
//   * E / NE / SE  (delta.X > 0) -> normal   (FlipH = false)
//   * W / NW / SW  (delta.X < 0) -> flipped  (FlipH = true)
//   * N / S        (delta.X == 0) -> keep the last horizontal flip
// A single InvertFlip switch reverses the whole mapping in one place if the live check shows it mirrored.
public static class CatoFacingFlip
{
    // Flip the mapping here (one line) if the art reads mirrored on screen during the live check. false = the
    // art's authored orientation faces E/right unflipped.
    public const bool InvertFlip = false;

    // Resolve the horizontal flip for a facing, given the previous flip to hold through vertical-only facings.
    // Returns the flip to apply AND keep as the new "last" value.
    public static bool Resolve(Direction8 facing, bool lastFlipH)
    {
        var deltaX = facing.Delta().X;
        if (deltaX == 0)
        {
            // N / S: no horizontal component — hold whatever we last latched.
            return lastFlipH;
        }

        // X < 0 (W/NW/SW) faces left -> flip; X > 0 (E/NE/SE) faces right -> normal.
        var flip = deltaX < 0;
        return InvertFlip ? !flip : flip;
    }
}
