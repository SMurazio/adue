using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// MONSTER-HOP: the hop render driver for EntityKind.Monster. Unlike the buffered TileInterpolator (renders a remote
// entity ~a tile IN THE PAST so a smooth glide hides jitter), this rests the monster EXACTLY on its latest confirmed
// server tile and hops (with a vertical arc) to a new tile when one arrives — so it sits on its authoritative tile
// (no playout lag) and a melee hit lands where the slime is drawn. These tests assert: rest == latest tile (on the
// marker), a backlog catches up to the NEWEST tile (no accumulated lag), the arc returns to ground (no drift), and a
// hop is in progress only briefly after a tile change.
public sealed class MonsterHopInterpolatorTests
{
    private const double HopMs = 160d;

    // At REST (between hops) the render position is EXACTLY the latest confirmed tile — on the cyan server marker,
    // NOT a buffered-past tile. A hop animates only briefly after a tile change, then settles back on the tile.
    [Fact]
    public void RestsExactlyOnLatestConfirmedTileBetweenHops()
    {
        var hop = new MonsterHopInterpolator(new TileCoord(0, 0), HopMs);

        // Steady one-tile-per-cadence confirms, sampling just BEFORE the next confirm (i.e. at rest after the hop).
        const double cadence = 300d; // comfortably longer than the 160ms hop, so it settles between steps
        for (var step = 1; step <= 5; step++)
        {
            var arrival = TimeSpan.FromMilliseconds(step * cadence);
            hop.Confirm(new TileCoord(step, 0), arrival);

            // Right after the confirm a hop is in flight (not yet at rest).
            hop.Sample(arrival + TimeSpan.FromMilliseconds(1));
            Assert.True(hop.IsHopping, $"expected a hop in flight just after the step-{step} confirm");

            // Sample at the END of the step window (well past the hop duration): at rest, exactly on the new tile.
            var atRest = hop.Sample(arrival + TimeSpan.FromMilliseconds(cadence - 1));
            Assert.False(hop.IsHopping, $"expected rest (no hop) at the end of the step-{step} window");
            Assert.Equal(step, atRest.X, 6);
            Assert.Equal(0, atRest.Y, 6);
            Assert.Equal(0d, hop.VerticalOffset, 6); // grounded at rest
        }
    }

    // A backlog of several confirmed tiles arriving while a hop is mid-flight catches up to the NEWEST tile — no
    // accumulated lag, no crawling through the stale in-between tiles. The render ends on the latest confirmed tile.
    [Fact]
    public void BacklogCatchesUpToNewestTileNoAccumulatedLag()
    {
        var hop = new MonsterHopInterpolator(new TileCoord(0, 0), HopMs);

        // Five tiles all confirmed at (nearly) the same instant — a fast monster / a hitch / a tab-out dumping a burst.
        var burstAt = TimeSpan.FromMilliseconds(1000);
        for (var x = 1; x <= 5; x++)
        {
            hop.Confirm(new TileCoord(x, 0), burstAt + TimeSpan.FromMilliseconds(x));
        }

        // The hop is targeting the NEWEST tile (5), not crawling toward tile 1 — confirm by where it settles.
        var settled = hop.Sample(burstAt + TimeSpan.FromMilliseconds(HopMs + 50));
        Assert.False(hop.IsHopping);
        Assert.Equal(5, settled.X, 6); // newest tile, no lag pile-up
        Assert.Equal(0, settled.Y, 6);

        // And mid-hop it is already heading PAST tile 1 toward tile 5 (never lingers on the stale backlog).
        var hop2 = new MonsterHopInterpolator(new TileCoord(0, 0), HopMs);
        for (var x = 1; x <= 5; x++)
        {
            hop2.Confirm(new TileCoord(x, 0), burstAt);
        }

        var midHop = hop2.Sample(burstAt + TimeSpan.FromMilliseconds(HopMs / 2));
        Assert.True(midHop.X > 1.5, $"mid-hop X={midHop.X} should already be heading toward the newest tile (5), not tile 1");
    }

    // The vertical arc returns to GROUND (offset 0) at the end of every hop — no permanent vertical offset that would
    // accumulate the slime upward over many hops. The arc peaks mid-hop and is 0 at both ends.
    [Fact]
    public void VerticalArcReturnsToGroundAfterEachHop()
    {
        var hop = new MonsterHopInterpolator(new TileCoord(0, 0), HopMs) { };
        hop.SetHopHeight(0.5d);

        for (var step = 1; step <= 4; step++)
        {
            var arrival = TimeSpan.FromMilliseconds(step * 400);
            hop.Confirm(new TileCoord(step, 0), arrival);

            // Mid-hop the arc is OFF the ground (positive offset).
            hop.Sample(arrival + TimeSpan.FromMilliseconds(HopMs / 2));
            Assert.True(hop.VerticalOffset > 0.1d, $"arc should be lifted mid-hop on step {step}, was {hop.VerticalOffset}");

            // After the hop completes the arc is back exactly on the ground (no residual offset).
            hop.Sample(arrival + TimeSpan.FromMilliseconds(HopMs + 100));
            Assert.Equal(0d, hop.VerticalOffset, 6);
        }
    }

    // An IDLE monster (no new confirms) does not drift — it rests still on its tile with no perpetual bob.
    [Fact]
    public void IdleMonsterDoesNotDrift()
    {
        var hop = new MonsterHopInterpolator(new TileCoord(3, 7), HopMs);

        // Sample repeatedly over a long idle stretch with no confirms: position is pinned, no vertical bob.
        for (var ms = 0; ms <= 5000; ms += 100)
        {
            var pos = hop.Sample(TimeSpan.FromMilliseconds(ms));
            Assert.Equal(3, pos.X, 6);
            Assert.Equal(7, pos.Y, 6);
            Assert.Equal(0d, hop.VerticalOffset, 6);
            Assert.False(hop.IsHopping);
        }
    }

    // A repeated confirm of the SAME tile does not re-trigger a bounce (no jitter when the server re-sends the
    // resting tile in successive snapshots).
    [Fact]
    public void RepeatedSameTileConfirmDoesNotReHop()
    {
        var hop = new MonsterHopInterpolator(new TileCoord(2, 2), HopMs);

        hop.Confirm(new TileCoord(2, 2), TimeSpan.FromMilliseconds(100)); // same as rest tile
        Assert.False(hop.IsHopping);

        hop.Confirm(new TileCoord(3, 2), TimeSpan.FromMilliseconds(200)); // a real move -> hops
        hop.Sample(TimeSpan.FromMilliseconds(200 + HopMs + 50)); // settle
        Assert.False(hop.IsHopping);

        hop.Confirm(new TileCoord(3, 2), TimeSpan.FromMilliseconds(500)); // re-confirm the tile it rests on
        Assert.False(hop.IsHopping); // no new bounce
    }

    // A live hop-duration change applies to the next hop (mirrors the F1 knob being turned).
    [Fact]
    public void LiveHopDurationChangeAppliesToNextHop()
    {
        var hop = new MonsterHopInterpolator(new TileCoord(0, 0), HopMs);
        hop.SetHopDurationMs(80d);

        hop.Confirm(new TileCoord(1, 0), TimeSpan.Zero);
        // With an 80ms hop it is fully settled by 100ms (would still be mid-hop at the old 160ms).
        var pos = hop.Sample(TimeSpan.FromMilliseconds(100));
        Assert.False(hop.IsHopping);
        Assert.Equal(1, pos.X, 6);
    }
}
