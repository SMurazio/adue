using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// S53 click-to-move, the UO/ClassicUO greedy-heading way. Instead of A*-pathing on confirmed tiles (the S52
// PathDriver, which lagged a round-trip behind and overshot turns once prediction was on), this drives the
// SAME held-direction MoveIntent the keyboard does: each step, pick the Direction8 pointing toward the goal
// from the CURRENT tile and hold it; stop when the current tile reaches the goal. Re-aiming every frame off
// the predicted tile makes the avatar steer toward what the player sees, with no waypoint machinery.
//
// No wall routing in v1: clicking across a wall just heads toward the goal and stalls at the obstacle (the
// predictor/server both refuse the blocked step) — acceptable per the task; add A* routing later if it
// annoys. Pure and headless-testable: feed a goal + a sequence of current tiles, assert the command sequence.
public sealed class ClickMoveController
{
    public bool IsActive { get; private set; }

    public TileCoord Goal { get; private set; }

    // True once a Stop has been emitted for the current goal, so we never re-emit it.
    private bool _arrivedEmitted;

    // Begin steering toward goal. A goal equal to the start is a no-op (already there): the controller stays
    // inactive so the caller can skip sending any intent.
    public void Start(TileCoord start, TileCoord goal)
    {
        Goal = goal;
        _arrivedEmitted = false;
        IsActive = start != goal;
    }

    // Cancel the active drive (WASD pre-empt / a new click replacing this one). Returns true if a drive was
    // actually active, so the host knows to send a trailing moving:false to stop the avatar.
    public bool Cancel()
    {
        var wasActive = IsActive;
        IsActive = false;
        _arrivedEmitted = false;
        return wasActive;
    }

    // Called each frame with the latest tile to steer FROM (the predicted local tile — what the player sees).
    // Emits Move toward the goal while travelling, a single Stop on arrival (then goes inactive), or None when
    // there is nothing to drive.
    public PathDriveCommand Update(TileCoord currentTile)
    {
        if (!IsActive)
        {
            return PathDriveCommand.None;
        }

        if (currentTile == Goal)
        {
            IsActive = false;
            if (_arrivedEmitted)
            {
                return PathDriveCommand.None;
            }

            _arrivedEmitted = true;
            return PathDriveCommand.Stop;
        }

        return PathDriveCommand.Move(HeadingToward(currentTile, Goal));
    }

    // The greedy 8-direction heading from -> to: sign of each axis delta, mapped to a Direction8. Unlike
    // PathDriver.TryDirectionToward (which requires 8-adjacency and is for stepping a known path), this
    // tolerates ANY non-zero delta — a multi-tile goal yields the diagonal/orthogonal heading that closes the
    // larger gap first, exactly like holding a key toward the target.
    public static Direction8 HeadingToward(TileCoord from, TileCoord to)
    {
        var dx = Math.Sign(to.X - from.X);
        var dy = Math.Sign(to.Y - from.Y);
        return (dx, dy) switch
        {
            (0, -1) => Direction8.N,
            (1, -1) => Direction8.NE,
            (1, 0) => Direction8.E,
            (1, 1) => Direction8.SE,
            (0, 1) => Direction8.S,
            (-1, 1) => Direction8.SW,
            (-1, 0) => Direction8.W,
            (-1, -1) => Direction8.NW,
            // from == to is handled by the arrival check before this is called; default keeps the compiler
            // happy and yields N if ever reached.
            _ => Direction8.N,
        };
    }
}
