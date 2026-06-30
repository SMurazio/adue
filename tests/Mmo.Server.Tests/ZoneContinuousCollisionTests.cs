using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// CONTINUOUS MIGRATION (Phase 2): the SERVER-LAYER swept-circle collision tests — the behavioural FLIP. Phase 1 let a
// player walk straight through blocked tiles (WorldEntityMovementTests.PlayerWalksThroughBlockedTiles_NoCollisionInPhase1);
// Phase 2 collides the PLAYER continuous integrator against walls derived from the tile map. These pin the INVERSE of
// the Phase-1 walk-through (stop at the surface), the slide on a glancing hit, the open-field regression (unchanged),
// and the server-layer determinism (same start + dir + map => byte-identical Position). Monsters take a SEPARATE
// continuous path (the HopLocomotion, Phase 8) — their collision-valid hops are covered by BasicRoamerBehaviorTests.
//
// Geometry: a blocked tile (tx,ty) is the 1x1 box [tx-0.5..tx+0.5]; the default body radius is 0.5, so a player
// driving into the -X face of a blocked tile stops with its CENTRE at (tx-0.5) - 0.5 = tx-1.0 (one tile-pitch shy of
// the blocked tile's centre — i.e. it cannot enter the blocked tile).
public sealed class ZoneContinuousCollisionTests
{
    private const double Eps = 1e-6;
    private const double Radius = CollisionDefaults.BodyRadius; // 0.5 — the default body radius

    // Build a zone with a single blocked tile and a player spawned at `spawn` with the given speed.
    private static (Zone zone, WorldEntity player) SpawnInto(TileCoord blocked, TileCoord spawn, double speed)
    {
        var grid = new TileGrid(32, 32, new[] { blocked });
        var zone = new Zone("test", grid, new[] { spawn });

        var session = new ClientSession(null!);
        var characterId = Guid.NewGuid();
        session.Authenticate(1, characterId, "Player", ClientRole.Player, Zone.DefaultId);
        var player = zone.SpawnPlayer(1, characterId, "Player", spawn, session, new Inventory(ItemRegistry.Default));
        session.AttachEntity(player);
        player.SetSpeedUnitsPerSecond(speed);
        return (zone, player);
    }

    [Fact]
    public void PlayerIntegratingIntoBlockedTile_StopsAtSurface_DoesNotEnter()
    {
        // The INVERSE of Phase 1's walk-through. Spawn at (8,8); block (10,8) directly east. Drive east many ticks.
        // The body must stop at the blocked tile's -X face minus radius => centre x = 9.5 - 0.5 = 9.0, never
        // entering the blocked tile (its rounded tile never reaches (10,8)).
        var (zone, player) = SpawnInto(blocked: new TileCoord(10, 8), spawn: new TileCoord(8, 8), speed: 5d);

        for (var i = 0; i < 200; i++)
        {
            zone.IntegrateMovement(player, Direction8.E.ToUnitVector(), dtSeconds: 0.05d, Radius);
        }

        Assert.True(player.Position.X <= 9.0d + Eps, $"entered/passed the wall: x={player.Position.X}");
        Assert.Equal(9.0d, player.Position.X, Eps); // pinned at the surface (face 9.5 minus radius 0.5)
        Assert.NotEqual(new TileCoord(10, 8), player.TileCoord);
        Assert.False(zone.BlockedTiles.Contains(player.TileCoord), "settled on a blocked tile");
    }

    [Fact]
    public void PlayerGlancingABlockedTile_SlidesAlongIt()
    {
        // Block (10,8). Spawn at (8,9) — one tile SOUTH of the wall's row. Drive NE (into the wall's row AND north).
        // The into-wall (X) component is blocked at the face; the tangential (Y/north) component is preserved, so the
        // body SLIDES north along the wall instead of stopping dead.
        var (zone, player) = SpawnInto(blocked: new TileCoord(10, 8), spawn: new TileCoord(8, 9), speed: 5d);
        var startY = player.Position.Y;

        for (var i = 0; i < 200; i++)
        {
            zone.IntegrateMovement(player, Direction8.NE.ToUnitVector(), dtSeconds: 0.05d, Radius);
        }

        Assert.False(zone.BlockedTiles.Contains(player.TileCoord), "penetrated the wall");
        // Slid north a meaningful distance (tangential motion preserved), not frozen at the start row.
        Assert.True(player.Position.Y < startY - 1d, $"did not slide along the wall (y={player.Position.Y} from {startY})");
    }

    [Fact]
    public void PlayerInOpenGround_MovesUnobstructed_RegressionWithPhase1()
    {
        // No wall anywhere near the path: continuous advance is unchanged from Phase 1 (the open-field regression).
        var (zone, player) = SpawnInto(blocked: new TileCoord(0, 0), spawn: new TileCoord(8, 8), speed: 10d);

        for (var i = 0; i < 5; i++)
        {
            zone.IntegrateMovement(player, Direction8.E.ToUnitVector(), dtSeconds: 0.1d, Radius); // 1 unit/tick east
        }

        Assert.Equal(13d, player.Position.X, Eps); // advanced ~5 tiles east unobstructed
        Assert.Equal(8d, player.Position.Y, Eps);
        Assert.Equal(new TileCoord(13, 8), player.TileCoord);
    }

    [Fact]
    public void ServerLayerDeterminism_SameStartDirMap_IdenticalPosition()
    {
        // Two independent zones with the SAME map + the same player start, fed the SAME (dir, dt, radius) stream into
        // and along a wall, must end byte-identical — the determinism contract at the SERVER layer (the Phase-4
        // client predictor reproduces this exact path).
        var (zoneA, a) = SpawnInto(blocked: new TileCoord(10, 8), spawn: new TileCoord(8, 8), speed: 5d);
        var (zoneB, b) = SpawnInto(blocked: new TileCoord(10, 8), spawn: new TileCoord(8, 8), speed: 5d);

        var script = new (Direction8 dir, int ticks)[]
        {
            (Direction8.E, 200),  // into the wall
            (Direction8.NE, 150), // slide along it
            (Direction8.N, 100),  // north
        };

        foreach (var (dir, ticks) in script)
        {
            for (var i = 0; i < ticks; i++)
            {
                zoneA.IntegrateMovement(a, dir.ToUnitVector(), dtSeconds: 0.05d, Radius);
                zoneB.IntegrateMovement(b, dir.ToUnitVector(), dtSeconds: 0.05d, Radius);
            }
        }

        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Position.X), BitConverter.DoubleToInt64Bits(b.Position.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Position.Y), BitConverter.DoubleToInt64Bits(b.Position.Y));
    }

    // ---- PLAYER↔MONSTER COLLISION --------------------------------------------------------------------------

    // Spawn a stationary monster body at `tile`'s centre into the zone's spatial index (so the player integrator's
    // nearby-monster gather finds it).
    private static WorldEntity SpawnMonster(Zone zone, TileCoord tile, uint networkId)
        => zone.SpawnTransient(networkId, EntityKind.Monster, "M", tile, Direction8.S);

    private static double Dist(WorldVector a, WorldVector b) => (a - b).Length;

    [Fact]
    public void PlayerIntegratingIntoMonster_IsBlocked_CentreDistanceStaysAtLeastTwoRadii()
    {
        // Open ground (block tile parked far away). Player at (8,8); a monster body at (11,8) due east. Drive the player
        // east many ticks: it must STOP at the radius-sum (centre distance >= 2×radius = 1.0), never overlapping, and
        // never pass to the monster's east side.
        var (zone, player) = SpawnInto(blocked: new TileCoord(0, 0), spawn: new TileCoord(8, 8), speed: 5d);
        var monster = SpawnMonster(zone, new TileCoord(11, 8), networkId: 2);

        for (var i = 0; i < 200; i++)
        {
            zone.IntegrateMovement(player, Direction8.E.ToUnitVector(), dtSeconds: 0.05d, Radius);
        }

        Assert.True(Dist(player.Position, monster.Position) >= (2d * Radius) - Eps,
            $"overlapped the monster: dist={Dist(player.Position, monster.Position)}");
        Assert.True(player.Position.X <= monster.Position.X + Eps, $"passed through the monster to x={player.Position.X}");
    }

    [Fact]
    public void PlayerMovingPastAnOffsetMonster_SlidesAround_ReachesTheFarSide()
    {
        // The monster sits slightly OFF the straight-east path (at (11, 8.45)); driving east, the player slides around
        // it (tangential motion preserved) and ends up EAST of it, never overlapping along the way.
        var (zone, player) = SpawnInto(blocked: new TileCoord(0, 0), spawn: new TileCoord(8, 8), speed: 5d);
        var monster = SpawnMonster(zone, new TileCoord(11, 8), networkId: 2);
        monster.ApplyResolvedMove(new WorldVector(11d, 8.45d)); // nudge off-axis so the player slides past, not stalls

        for (var i = 0; i < 300; i++)
        {
            zone.IntegrateMovement(player, Direction8.E.ToUnitVector(), dtSeconds: 0.05d, Radius);
            Assert.True(Dist(player.Position, monster.Position) >= (2d * Radius) - 1e-3,
                $"overlapped the monster mid-slide: dist={Dist(player.Position, monster.Position)}");
        }

        Assert.True(player.Position.X > monster.Position.X + Radius,
            $"did not get past the monster (x={player.Position.X}, monster x={monster.Position.X})");
    }

    [Fact]
    public void PlayerWithNoMonsterNearby_MovesByteIdentical_ToWallsOnlyPath()
    {
        // A monster parked FAR away (outside the obstacle-gather box) must leave the integrated path byte-identical to
        // the same zone with no monster at all — the empty-obstacle regression (walls-only path unchanged).
        var (zoneA, a) = SpawnInto(blocked: new TileCoord(0, 0), spawn: new TileCoord(8, 8), speed: 5d);
        SpawnMonster(zoneA, new TileCoord(28, 28), networkId: 2); // ~20 tiles away — far outside the gather box

        var (zoneB, b) = SpawnInto(blocked: new TileCoord(0, 0), spawn: new TileCoord(8, 8), speed: 5d);

        for (var i = 0; i < 50; i++)
        {
            zoneA.IntegrateMovement(a, Direction8.E.ToUnitVector(), dtSeconds: 0.05d, Radius);
            zoneB.IntegrateMovement(b, Direction8.E.ToUnitVector(), dtSeconds: 0.05d, Radius);
        }

        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Position.X), BitConverter.DoubleToInt64Bits(b.Position.X));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Position.Y), BitConverter.DoubleToInt64Bits(b.Position.Y));
    }

    // NOTE (Phase 11 coherence sweep): the former MonsterStillBlocksViaTileStep_NotTheContinuousCollision_Regression
    // test was DELETED here. It asserted monsters block via the tile-step path (Zone.TryStep / IsStepWalkable), but
    // after Phase 8 monsters move via the continuous HopLocomotion (no TryStep), and the hop's collision-valid landing
    // (a hop never lands inside a wall) is covered by BasicRoamerBehaviorTests.HopsLandCollisionValid_AndSomeLandSubTile.
    // The tile-step path it exercised has since been removed, so the test was testing deleted behaviour.
}
