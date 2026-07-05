using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// BOSS-4 prerequisite (BOSS-3 review MEDIUM-1): DIRECT tests for Zone.DisplaceResolved — the server-authoritative
// shove seam BOTH the P2 Repel/solo fields (BOSS-3) and the P3 knockback pulses (BOSS-4) ride, which until now had
// zero Zone-level coverage (only recorder fakes in the encounter suite). Built over a REAL Zone + TileGrid (the
// ZoneContinuousCollisionTests construction pattern) so the swept-circle wall resolve, the spatial-bucket migration,
// and the replication bookkeeping are the genuine articles.
//
// THE LOAD-BEARING CASE is the sub-tile wall-pinned shove: ApplyResolvedMove bumps StateRevision ONLY on a rounded-
// tile cross (R1), so a shove clamped short by a wall that crosses no tile would silently NOT re-publish the nudged
// position — the recipient already acked the unchanged revision and the shoved body would never move on any client.
// DisplaceResolved's else-branch (WorldEntity.MarkRepositioned) exists precisely to bump revision on that path — the
// stop-edge replication-miss class (StopMovement / SnapToGround / monster-separation precedents) that has bitten this
// project three times. These tests pin all three shove outcomes: open floor, wall-clamped-with-tile-cross, and
// wall-pinned-sub-tile.
//
// Geometry (the ZoneContinuousCollisionTests convention): a blocked tile (tx,ty) is the 1x1 box [tx-0.5..tx+0.5];
// the body radius is CollisionDefaults.BodyRadius (0.5) — the SAME value GameServer passes to DisplaceResolved — so a
// body shoved east into the -X face of blocked tile (10,8) clamps with its CENTRE at 9.5 - 0.5 = 9.0.
public sealed class ZoneDisplaceResolvedTests
{
    private const double Eps = 1e-6;
    private const double Radius = CollisionDefaults.BodyRadius; // 0.5 — what GameServer wires into DisplaceResolved

    // Build a zone with a single blocked tile and a player spawned at `spawn` — the ZoneContinuousCollisionTests
    // harness shape (a real Zone over a real TileGrid; the player registers in the spatial index via SpawnPlayer).
    private static (Zone zone, WorldEntity player) SpawnInto(TileCoord blocked, TileCoord spawn)
    {
        var grid = new TileGrid(32, 32, new[] { blocked });
        var zone = new Zone("test", grid, new[] { spawn });

        var session = new ClientSession(null!);
        var characterId = Guid.NewGuid();
        session.Authenticate(1, characterId, "Player", ClientRole.Player, Zone.DefaultId);
        var player = zone.SpawnPlayer(1, characterId, "Player", spawn, session, new Inventory(ItemRegistry.Default));
        session.AttachEntity(player);
        return (zone, player);
    }

    [Fact]
    public void OpenFloorShove_LandsAtTarget_AndBumpsRevision()
    {
        // No wall anywhere near the path (block parked at (0,0)). Shove the player 3u east — the BOSS-4 pulse
        // distance — from (8,8) to (11,8): it lands EXACTLY at the target, the rounded tile crossed (8→11), and
        // StateRevision bumped (ApplyResolvedMove's tile-cross branch) so the shove replicates.
        var (zone, player) = SpawnInto(blocked: new TileCoord(0, 0), spawn: new TileCoord(8, 8));
        var revisionBefore = player.StateRevision;

        zone.DisplaceResolved(player, new WorldVector(11d, 8d), Radius);

        Assert.Equal(11d, player.Position.X, Eps);
        Assert.Equal(8d, player.Position.Y, Eps);
        Assert.Equal(new TileCoord(11, 8), player.TileCoord);
        Assert.True(player.StateRevision > revisionBefore, "open-floor shove did not bump StateRevision");

        // The spatial bucket migrated with the shove: a gather around the LANDING tile finds the body (the index the
        // AOI/telegraph/aggro queries all share — a stale bucket would desync every spatial consumer).
        var gathered = new List<WorldEntity>();
        zone.World.GatherInterestCandidates(new TileCoord(11, 8), 1, gathered);
        Assert.Contains(player, gathered);
    }

    [Fact]
    public void ShoveIntoAWall_ClampsAtTheFaceMinusRadius_AndBumpsRevision()
    {
        // Block (10,8); player at (8,8). Shove 4u east to (12,8) — THROUGH the wall. The swept resolve must clamp the
        // centre at the wall face minus the body radius (9.5 - 0.5 = 9.0): never inside/through the blocked tile. The
        // clamped landing still crossed a tile (8→9), so revision bumps via ApplyResolvedMove's tile-cross branch.
        var (zone, player) = SpawnInto(blocked: new TileCoord(10, 8), spawn: new TileCoord(8, 8));
        var revisionBefore = player.StateRevision;

        zone.DisplaceResolved(player, new WorldVector(12d, 8d), Radius);

        Assert.Equal(9.0d, player.Position.X, Eps); // wall face 9.5 minus radius 0.5
        Assert.Equal(8d, player.Position.Y, Eps);
        Assert.NotEqual(new TileCoord(10, 8), player.TileCoord); // never entered the blocked tile
        Assert.False(zone.BlockedTiles.Contains(player.TileCoord), "shoved onto a blocked tile");
        Assert.True(player.StateRevision > revisionBefore, "wall-clamped shove did not bump StateRevision");
    }

    [Fact]
    public void SubTileWallPinnedShove_CrossesNoTile_ButStillBumpsRevision()
    {
        // THE FINDING'S CORE (BOSS-3 review MEDIUM-1 / the stop-edge replication-miss class). Block (10,8); start the
        // player at x=8.8 — a SUB-TILE position inside tile (9,8), 0.2u shy of the wall-clamp point (9.0, also tile
        // (9,8)). Shove 3u east: the resolve clamps at 9.0, so the entire resolved move happens WITHIN one rounded
        // tile — ApplyResolvedMove returns false (no tile cross) and the MarkRepositioned else-branch is the ONLY
        // thing that re-publishes the nudged position. Assert revision bumped even though the tile did not change.
        var (zone, player) = SpawnInto(blocked: new TileCoord(10, 8), spawn: new TileCoord(9, 8));
        player.ApplyResolvedMove(new WorldVector(8.8d, 8d)); // sub-tile nudge; 8.8 rounds to tile (9,8) — no cross.
        Assert.Equal(new TileCoord(9, 8), player.TileCoord);
        var revisionBefore = player.StateRevision;

        zone.DisplaceResolved(player, new WorldVector(11.8d, 8d), Radius);

        Assert.Equal(9.0d, player.Position.X, Eps); // clamped at the wall face minus radius
        Assert.Equal(new TileCoord(9, 8), player.TileCoord); // rounded tile UNCHANGED — the sub-tile case
        Assert.True(player.StateRevision > revisionBefore,
            "sub-tile wall-pinned shove did not bump StateRevision — the MarkRepositioned else-branch regressed "
            + "(the shoved body would never replicate: the recipient already acked this revision)");
    }

    [Fact]
    public void FullyPinnedShove_ZeroResolvedMovement_StillBumpsRevision()
    {
        // The degenerate extreme of the sub-tile case: the player ALREADY sits at the clamp point (9.0 — flush
        // against the wall). A further eastward shove resolves to ZERO movement; the else-branch must STILL bump
        // (idempotent re-publish, matching the monster-separation MarkRepositioned semantics) — cheap, and it keeps
        // the invariant simple: EVERY DisplaceResolved call re-publishes, moved or not.
        var (zone, player) = SpawnInto(blocked: new TileCoord(10, 8), spawn: new TileCoord(9, 8));
        player.ApplyResolvedMove(new WorldVector(9.0d, 8d)); // park exactly at the clamp point (tile (9,8), no cross).
        var revisionBefore = player.StateRevision;

        zone.DisplaceResolved(player, new WorldVector(12d, 8d), Radius);

        Assert.Equal(9.0d, player.Position.X, Eps); // did not move — fully pinned
        Assert.Equal(new TileCoord(9, 8), player.TileCoord);
        Assert.True(player.StateRevision > revisionBefore, "fully-pinned shove did not bump StateRevision");
    }
}
