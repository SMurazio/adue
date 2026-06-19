using Mmo.Shared.Domain;

namespace Mmo.Shared.Protocol;

// Pure per-entity delta-encoding policy (S47b, protocol v16): given an entity's current absolute state and
// the baseline the viewer has ACKED, produce the delta-coded EntityStateSnapshot row (a changed-field
// bitmask + only the changed fields). Lives in the protocol layer so it is shared truth between the server
// (which emits rows) and tests (which verify the encoding round-trips and converges) — keeping the wire
// policy in one testable place instead of buried in the server hot path.
//
// Position is a single-tile STEP when the move is exactly one of the 8 unit directions (the common
// tile-step case), ABSOLUTE on a non-unit move (teleport / spawn-relocate) or when there is no baseline,
// and OMITTED when the tile is unchanged. Step deltas are only ever emitted against a baseline the viewer
// provably holds (its acked baseline), which S47a's contiguous ack guarantees — a cumulative step against a
// baseline the client lacked would permanently corrupt its position.
public static class EntityStateDelta
{
    // A complete/baseline/AOI-entry row: absolute coordinates + every field, establishing the baseline.
    public static EntityStateSnapshot EncodeAbsolute(uint networkId, TileCoord tile, Direction8 facing, bool depleted)
    {
        return EntityStateSnapshot.Absolute(networkId, tile, facing, depleted);
    }

    // An incremental delta row relative to the viewer's acked baseline state. Sends position as a step when
    // it is a unit move, absolute on a non-unit move, omitted when unchanged; facing/depleted only when they
    // differ from the baseline.
    public static EntityStateSnapshot EncodeDelta(
        uint networkId,
        TileCoord currentTile,
        Direction8 currentFacing,
        bool currentDepleted,
        TileCoord baselineTile,
        Direction8 baselineFacing,
        bool baselineDepleted)
    {
        var changes = EntityStateChange.None;
        var step = Direction8.N;

        if (currentTile != baselineTile)
        {
            if (TryGetUnitStep(baselineTile, currentTile, out step))
            {
                changes |= EntityStateChange.PositionStep;
            }
            else
            {
                changes |= EntityStateChange.PositionAbsolute;
            }
        }

        if (currentFacing != baselineFacing)
        {
            changes |= EntityStateChange.Facing;
        }

        if (currentDepleted != baselineDepleted)
        {
            changes |= EntityStateChange.Depleted;
        }

        return new EntityStateSnapshot(networkId, currentTile, currentFacing, currentDepleted, changes, step);
    }

    // True when `to` is exactly one of the 8 unit-step neighbours of `from`, yielding the Direction8 step.
    public static bool TryGetUnitStep(TileCoord from, TileCoord to, out Direction8 step)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (dx is < -1 or > 1 || dy is < -1 or > 1 || (dx == 0 && dy == 0))
        {
            step = Direction8.N;
            return false;
        }

        step = (dx, dy) switch
        {
            (0, -1) => Direction8.N,
            (1, -1) => Direction8.NE,
            (1, 0) => Direction8.E,
            (1, 1) => Direction8.SE,
            (0, 1) => Direction8.S,
            (-1, 1) => Direction8.SW,
            (-1, 0) => Direction8.W,
            (-1, -1) => Direction8.NW,
            _ => Direction8.N,
        };
        return true;
    }
}
