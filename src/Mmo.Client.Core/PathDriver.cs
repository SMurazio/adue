using System.Collections.Generic;
using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// What a PathDriver.Update tick wants the caller to do with the movement intent this frame. The driver
// itself never touches the network — it is pure, headless-testable logic; the host (Godot) translates a
// command into MmoClient.SendMoveIntent. None means "no change this frame" (the held intent already
// matches what the driver wants, so don't resend), Move means "hold this direction", Stop means "we have
// arrived (or were cancelled) — send moving:false once".
public enum PathDriveAction : byte
{
    None = 0,
    Move = 1,
    Stop = 2,
}

public readonly record struct PathDriveCommand(PathDriveAction Action, Direction8 Direction)
{
    public static readonly PathDriveCommand None = new(PathDriveAction.None, default);

    public static PathDriveCommand Move(Direction8 direction) => new(PathDriveAction.Move, direction);

    public static readonly PathDriveCommand Stop = new(PathDriveAction.Stop, default);
}

// Drives the existing held-direction MoveIntent along a precomputed tile path, advancing on SERVER
// CONFIRMATION rather than prediction: the avatar only moves when the server confirms a step, so the
// driver watches the confirmed local tile (MmoClient.LocalTile) and steps its waypoint cursor forward
// each time the confirmed tile reaches the next waypoint. It emits a Move command toward the next
// waypoint while travelling and a single Stop command on arrival. A manual cancel (WASD / new click)
// stops emission; the host decides whether to send the trailing moving:false.
//
// Allocation-light: the path is stored as a List<TileCoord> reused across calls; Update allocates
// nothing on the steady-state path. Update is deterministic and side-effect free, so it unit-tests by
// feeding a sequence of confirmed tiles and asserting the command sequence.
public sealed class PathDriver
{
    private readonly List<TileCoord> _path = new(64);
    private int _cursor;
    private bool _arrivedEmitted;

    public bool IsActive { get; private set; }

    // The tile the driver is currently steering toward (the next waypoint), or null when inactive.
    public TileCoord? CurrentWaypoint => IsActive && _cursor < _path.Count ? _path[_cursor] : null;

    public TileCoord? Goal => _path.Count > 0 ? _path[^1] : null;

    // Begins driving along path (the ordered tiles to step onto, excluding the start tile — exactly what
    // TilePathfinder.FindPath returns). An empty path is a no-op: the driver stays inactive so the caller
    // can fall back to its unreachable/already-there feedback.
    public void Start(IReadOnlyList<TileCoord> path)
    {
        System.ArgumentNullException.ThrowIfNull(path);
        _path.Clear();
        _cursor = 0;
        _arrivedEmitted = false;
        if (path.Count == 0)
        {
            IsActive = false;
            return;
        }

        _path.AddRange(path);
        IsActive = true;
    }

    // Cancels the active drive. Returns true if a drive was actually active (so the host knows it should
    // send a trailing moving:false to stop the avatar at the confirmed tile); false if already idle.
    public bool Cancel()
    {
        var wasActive = IsActive;
        IsActive = false;
        _path.Clear();
        _cursor = 0;
        _arrivedEmitted = false;
        return wasActive;
    }

    // Called each frame with the latest server-confirmed local tile. Advances the waypoint cursor past
    // any waypoints the confirmed tile has already reached, then returns the intent for this frame:
    //  * Stop once when the confirmed tile reaches the destination (then the driver goes inactive),
    //  * Move toward the next waypoint while travelling,
    //  * None when there is nothing to drive.
    // Advancing in a loop tolerates the confirmed tile jumping more than one waypoint (e.g. a snapshot
    // that skips a tile) and keeps the emitted direction pointed at the right next waypoint.
    public PathDriveCommand Update(TileCoord confirmedTile)
    {
        if (!IsActive)
        {
            return PathDriveCommand.None;
        }

        // Advance the cursor to just past the waypoint the confirmed tile currently sits on. Scanning
        // forward (not just matching the current cursor) tolerates the confirmed tile jumping AHEAD more
        // than one waypoint — e.g. a snapshot that skipped a tile — so we steer toward the correct NEXT
        // waypoint instead of emitting a backward direction toward an already-passed one. If the confirmed
        // tile is on no upcoming waypoint (drifted off-path), the cursor is left put and the adjacency
        // check below decides whether to keep steering or stop.
        for (var i = _cursor; i < _path.Count; i++)
        {
            if (_path[i] == confirmedTile)
            {
                _cursor = i + 1;
                break;
            }
        }

        if (_cursor >= _path.Count)
        {
            IsActive = false;
            if (_arrivedEmitted)
            {
                return PathDriveCommand.None;
            }

            _arrivedEmitted = true;
            return PathDriveCommand.Stop;
        }

        var next = _path[_cursor];
        if (!TryDirectionToward(confirmedTile, next, out var direction))
        {
            // The confirmed tile is neither the next waypoint nor one tile-step from it (path no longer
            // valid relative to where the avatar actually is — e.g. it drifted). Stop cleanly rather than
            // emit a bogus direction; a fresh click will repath from the real tile.
            IsActive = false;
            if (_arrivedEmitted)
            {
                return PathDriveCommand.None;
            }

            _arrivedEmitted = true;
            return PathDriveCommand.Stop;
        }

        return PathDriveCommand.Move(direction);
    }

    // Maps the one-tile delta from -> to onto a Direction8. Returns false when the two tiles are not
    // 8-adjacent (delta out of [-1,1] on either axis, or identical), which the driver treats as a desync.
    public static bool TryDirectionToward(TileCoord from, TileCoord to, out Direction8 direction)
    {
        var dx = System.Math.Sign(to.X - from.X);
        var dy = System.Math.Sign(to.Y - from.Y);
        var adx = System.Math.Abs(to.X - from.X);
        var ady = System.Math.Abs(to.Y - from.Y);
        direction = default;
        if (adx > 1 || ady > 1 || (dx == 0 && dy == 0))
        {
            return false;
        }

        direction = (dx, dy) switch
        {
            (0, -1) => Direction8.N,
            (1, -1) => Direction8.NE,
            (1, 0) => Direction8.E,
            (1, 1) => Direction8.SE,
            (0, 1) => Direction8.S,
            (-1, 1) => Direction8.SW,
            (-1, 0) => Direction8.W,
            (-1, -1) => Direction8.NW,
            _ => default,
        };
        return true;
    }
}
