using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// MONSTER-HOP: the render driver for EntityKind.Monster. Unlike the TileInterpolator (a jitter buffer that
// renders remote entities ~one tile IN THE PAST so a smooth glide can absorb arrival jitter), this renders a
// monster sitting EXACTLY ON its latest CONFIRMED server tile and, when that tile changes, plays a quick HOP
// to the new tile — a horizontal move plus a vertical sin arc (an up-and-down bounce) over a SHORT duration,
// then settles back on the tile. There is NO playout buffer (no rendering in the past): between hops the
// monster rests precisely on its authoritative tile, so it sits on the cyan "server position" debug marker and
// the player can hit it where it is drawn (the buffered-glide lag was missing combat — the slime was drawn ~a
// tile behind where the server checked the hit).
//
// Catch-up-to-newest: the resting position is ALWAYS the newest confirmed tile. If several tiles arrive while a
// hop is mid-flight (a fast monster, a hitch, a tab-out), the hop retargets to the NEWEST tile and the stale
// in-between tiles are dropped — the monster never trails its authoritative tile (the whole point). The hop's
// origin is wherever the render currently is, so a retarget mid-arc continues smoothly from the live position.
public sealed class MonsterHopInterpolator
{
    // The hop's vertical bounce height in WORLD UNITS (tiles) at the arc apex. A sensible fixed default — the
    // slime lifts ~a quarter tile and comes back down. Exposed as a settable so a second F1 knob can drive it
    // live if desired; kept a constant default so the common case needs only the duration knob.
    public const double DefaultHopHeight = 0.35d;

    private double _hopDurationMs;
    private double _hopHeight = DefaultHopHeight;

    // The tile the monster currently RESTS on (the latest confirmed tile we have committed to as the hop target).
    private TileCoord _restTile;

    // An in-flight hop, or null when at rest. From = where the render was when the hop began (may be mid-tile if
    // a prior hop was retargeted); To = the newest confirmed tile; StartedAt = hop start clock.
    private ActiveHop? _activeHop;

    // The live render position (ground-plane X/Y) and the current vertical arc offset (world units, 0 at rest).
    private RenderPosition _renderPosition;
    private double _verticalOffset;

    public MonsterHopInterpolator(TileCoord initialTile, double hopDurationMs)
    {
        _restTile = initialTile;
        _renderPosition = RenderPosition.FromTile(initialTile);
        _hopDurationMs = Math.Max(1, hopDurationMs);
    }

    // Ground-plane render position (X/Y). At rest this equals the latest confirmed tile (on the server marker).
    public RenderPosition RenderPosition => _renderPosition;

    // The vertical arc offset in world units (0 at rest, peaks at _hopHeight mid-hop). The Godot wrapper adds
    // this to the visual's world Y so the slime bounces; it never affects the authoritative tile or targeting.
    public double VerticalOffset => _verticalOffset;

    // True while a hop is animating (briefly after a tile change); false at rest. For tests/diagnostics.
    public bool IsHopping => _activeHop is not null;

    public double HopDurationMs => _hopDurationMs;

    public double HopHeight => _hopHeight;

    public void SetHopDurationMs(double hopDurationMs)
    {
        _hopDurationMs = Math.Max(1, hopDurationMs);
    }

    public void SetHopHeight(double hopHeight)
    {
        _hopHeight = Math.Max(0, hopHeight);
    }

    // Hard-reset onto a tile (respawn / AOI re-entry). Cancels any in-flight hop and snaps to ground.
    public void Reset(TileCoord tile)
    {
        _restTile = tile;
        _activeHop = null;
        _renderPosition = RenderPosition.FromTile(tile);
        _verticalOffset = 0d;
    }

    // A new server-confirmed tile arrived. Always retarget the hop toward this NEWEST tile (dropping any stale
    // backlog — there is no queue here, the target is simply "the latest tile"). A no-op if it's the tile we are
    // already resting on or already hopping toward, so a repeated confirm of the same tile doesn't re-trigger a
    // bounce. The hop's origin is the CURRENT render position, so a retarget mid-arc continues from where we are.
    public void Confirm(TileCoord tile, TimeSpan receivedAt)
    {
        // Already heading to (or resting on) this exact tile — nothing to do.
        if (tile == _restTile)
        {
            return;
        }

        _restTile = tile;
        _activeHop = new ActiveHop(_renderPosition, RenderPosition.FromTile(tile), receivedAt);
    }

    // Advance the render to `now`. While a hop is in flight, the ground position lerps From->To and the vertical
    // offset follows a half-sine arc (0 -> _hopHeight -> 0) over _hopDurationMs; when it completes the monster
    // rests EXACTLY on the (newest) confirmed tile with zero vertical offset. At rest this is a cheap no-op that
    // returns the resting tile position.
    public RenderPosition Sample(TimeSpan now)
    {
        if (_activeHop is not { } hop)
        {
            _verticalOffset = 0d;
            return _renderPosition;
        }

        var elapsedMs = Math.Max(0, (now - hop.StartedAt).TotalMilliseconds);
        var alpha = elapsedMs / _hopDurationMs;
        if (alpha >= 1d)
        {
            // Hop finished — settle exactly on the target tile at ground level.
            _renderPosition = hop.To;
            _verticalOffset = 0d;
            _activeHop = null;
            return _renderPosition;
        }

        _renderPosition = RenderPosition.Lerp(hop.From, hop.To, alpha);
        // Half-sine arc: 0 at alpha=0, peak _hopHeight at alpha=0.5, back to 0 at alpha=1 (no permanent offset).
        _verticalOffset = Math.Sin(alpha * Math.PI) * _hopHeight;
        return _renderPosition;
    }

    private sealed record ActiveHop(RenderPosition From, RenderPosition To, TimeSpan StartedAt);
}
