namespace Mmo.Shared.Domain;

// COMBAT-S2B: the melee "shotgun" cone — a 3-tile fan one tile in front of the attacker, rotated by the
// attacker's Direction8 facing (docs/combat-design.md). The pattern is the forward tile PLUS its two 45°
// flank tiles, each exactly one tile out:
//
//   * forward = the facing direction's tile delta (Direction8.Delta()).
//   * left flank  = the Direction8 one step counter-clockwise from facing, one tile out.
//   * right flank = the Direction8 one step clockwise from facing, one tile out.
//
// "Rotated by facing" falls out for free because the flanks are just the adjacent Direction8 values (mod 8):
// rotating the whole fan is a single +/-1 step around the 8-way compass, so a diagonal facing produces a
// diagonal-centred fan with no special-casing. (The design's open "diagonal rotation feels off?" question is
// thus answered structurally — flag in the briefing if it looks wrong live.) The three tiles can never alias
// each other (three distinct adjacent compass directions), so a target standing on the centre tile is counted
// exactly once.
//
// Pure + deterministic + allocation-light (a fixed 3-element span), so it is trivially unit-tested per facing
// and reused identically by any future telegraph renderer that wants to light up the danger tiles.
public static class MeleeCone
{
    // Number of tiles in the fan (forward + two flanks).
    public const int TileCount = 3;

    // Writes the three ABSOLUTE world tiles of the cone (origin offset by each fan delta) into `destination`,
    // which MUST have room for TileCount entries. Returns the number written (always TileCount). No allocation.
    public static int Resolve(TileCoord origin, Direction8 facing, Span<TileCoord> destination)
    {
        var left = Rotate(facing, -1);
        var right = Rotate(facing, +1);

        var forwardDelta = facing.Delta();
        var leftDelta = left.Delta();
        var rightDelta = right.Delta();

        destination[0] = origin.Offset(forwardDelta.X, forwardDelta.Y);
        destination[1] = origin.Offset(leftDelta.X, leftDelta.Y);
        destination[2] = origin.Offset(rightDelta.X, rightDelta.Y);
        return TileCount;
    }

    // Steps `steps` positions clockwise (+) or counter-clockwise (-) around the 8-way compass, wrapping mod 8.
    // Direction8 is laid out clockwise (N=0, NE=1, E=2, ...), so +1 is a 45° clockwise turn.
    private static Direction8 Rotate(Direction8 facing, int steps)
    {
        var index = ((int)facing + steps) % 8;
        if (index < 0)
        {
            index += 8;
        }

        return (Direction8)index;
    }
}
