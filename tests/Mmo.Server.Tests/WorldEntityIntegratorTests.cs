using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// Phase 1 (continuous migration): pins the PLAYER continuous integrator (WorldEntity.IntegrateMovement /
// StopMovement) — the server-side port of the proven exp:ContinuousMover. The load-bearing properties:
//   1. Position += Velocity x dt   (a held direction advances position linearly with the FIXED server dt)
//   2. diagonals are NOT faster (the unit direction is normalized before scaling by SpeedUnitsPerSecond)
//   3. a 2x speed stat travels 2x the distance per tick (the multiplier is intrinsic to the speed stat)
//   4. StopMovement zeros velocity instantly (no glide — R6)
//   5. StateRevision / StepSequence bump ONLY when the rounded tile crosses, not every sub-tile tick (R1)
public sealed class WorldEntityIntegratorTests
{
    private const double Eps = 1e-9;

    [Fact]
    public void IntegrateEast_AdvancesPositionByVelocityTimesDt()
    {
        var entity = CreateEntity(tile: new TileCoord(0, 0), speed: 5d);

        entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 2d);

        Assert.Equal(10d, entity.Position.X, Eps);   // 5 units/s * 2 s
        Assert.Equal(0d, entity.Position.Y, Eps);
        Assert.Equal(5d, entity.Velocity.Length, Eps);
        Assert.Equal(Direction8.E, entity.Facing);
    }

    [Fact]
    public void IntegrateAccumulatesAcrossTicks()
    {
        var entity = CreateEntity(tile: new TileCoord(0, 0), speed: 4d);

        // Three 0.5s ticks east at speed 4 -> 6 units total.
        entity.IntegrateMovement(Direction8.E.ToUnitVector(), 0.5d);
        entity.IntegrateMovement(Direction8.E.ToUnitVector(), 0.5d);
        entity.IntegrateMovement(Direction8.E.ToUnitVector(), 0.5d);

        Assert.Equal(6d, entity.Position.X, Eps);
        Assert.Equal(0d, entity.Position.Y, Eps);
    }

    [Fact]
    public void DiagonalIntegration_IsSameSpeedAsCardinal()
    {
        var cardinal = CreateEntity(tile: new TileCoord(0, 0), speed: 6d);
        var diagonal = CreateEntity(tile: new TileCoord(0, 0), speed: 6d);

        cardinal.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 1d);
        diagonal.IntegrateMovement(Direction8.SE.ToUnitVector(), dtSeconds: 1d);

        // The unit direction normalizes the (1,1) diagonal, so both travel the SAME distance (6) in one second —
        // the diagonal just splits it across both axes.
        var cardinalDist = cardinal.Position.Length;
        var diagonalDist = diagonal.Position.Length;
        Assert.Equal(6d, cardinalDist, Eps);
        Assert.Equal(6d, diagonalDist, Eps);
        Assert.Equal(cardinalDist, diagonalDist, Eps);
        Assert.Equal(6d, diagonal.Velocity.Length, Eps);
    }

    [Fact]
    public void DoubleSpeedTravelsDoubleDistancePerTick()
    {
        var baseSpeed = CreateEntity(tile: new TileCoord(0, 0), speed: 5d);
        var fast = CreateEntity(tile: new TileCoord(0, 0), speed: 10d);

        baseSpeed.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.05d);
        fast.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.05d);

        // A 2x speed stat -> 2x distance the same tick (the multiplier is intrinsic to SpeedUnitsPerSecond, not a
        // cadence). 0.25 vs 0.5 units this tick.
        Assert.Equal(0.25d, baseSpeed.Position.X, Eps);
        Assert.Equal(0.5d, fast.Position.X, Eps);
        Assert.Equal(2d * baseSpeed.Position.X, fast.Position.X, Eps);
    }

    [Fact]
    public void StopMovement_ZeroesVelocity_InstantStop_NoGlide()
    {
        var entity = CreateEntity(tile: new TileCoord(0, 0), speed: 5d);

        entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 1d);   // move east 5 units
        Assert.Equal(5d, entity.Position.X, Eps);
        Assert.Equal(5d, entity.Velocity.Length, Eps);

        entity.StopMovement();   // release
        Assert.Equal(0d, entity.Velocity.Length, Eps);

        // A subsequent stopped tick (zero direction) must not drift — Position stays put.
        entity.IntegrateMovement(WorldVector.Zero, dtSeconds: 1d);
        Assert.Equal(5d, entity.Position.X, Eps);
        Assert.Equal(0d, entity.Position.Y, Eps);
        Assert.Equal(0d, entity.Velocity.Length, Eps);
    }

    [Fact]
    public void ZeroDirection_IsAnInstantStop()
    {
        var entity = CreateEntity(tile: new TileCoord(3, 7), speed: 5d);

        entity.IntegrateMovement(WorldVector.Zero, dtSeconds: 1d);

        Assert.Equal(3d, entity.Position.X, Eps);
        Assert.Equal(7d, entity.Position.Y, Eps);
        Assert.Equal(0d, entity.Velocity.Length, Eps);
    }

    [Fact]
    public void SubTileMove_DoesNotBumpStateRevisionOrStepSequence()
    {
        // R1: a sub-tile advance that does NOT cross a rounded-tile boundary must not bump replication state — the
        // tile-keyed snapshot would otherwise spam identical deltas every tick.
        var entity = CreateEntity(tile: new TileCoord(0, 0), speed: 1d);
        var revisionBefore = entity.StateRevision;
        var seqBefore = entity.StepSequence;

        // 1 unit/s * 0.1s = 0.1 units east — still rounds to tile (0,0).
        var crossed = entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.1d);

        Assert.False(crossed);
        Assert.Equal(new TileCoord(0, 0), entity.TileCoord);
        Assert.Equal(revisionBefore, entity.StateRevision);
        Assert.Equal(seqBefore, entity.StepSequence);
        Assert.Equal(0.1d, entity.Position.X, Eps);
    }

    [Fact]
    public void CrossingATile_BumpsStateRevisionAndStepSequenceExactlyOnce()
    {
        // R1 / R5: accumulate sub-tile steps until the rounded tile crosses; the bump happens exactly on the
        // crossing tick, not before. Round-away-from-zero means x>=0.5 rounds to tile 1.
        var entity = CreateEntity(tile: new TileCoord(0, 0), speed: 1d);
        var revisionStart = entity.StateRevision;
        var seqStart = entity.StepSequence;

        // Four 0.1s ticks -> x = 0.1, 0.2, 0.3, 0.4: all still round to tile 0, no bump.
        for (var i = 0; i < 4; i++)
        {
            Assert.False(entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.1d));
        }

        Assert.Equal(new TileCoord(0, 0), entity.TileCoord);
        Assert.Equal(revisionStart, entity.StateRevision);
        Assert.Equal(seqStart, entity.StepSequence);

        // The fifth tick -> x = 0.5, which rounds to tile 1: a single crossing, one bump each.
        Assert.True(entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.1d));
        Assert.Equal(new TileCoord(1, 0), entity.TileCoord);
        Assert.Equal(revisionStart + 1, entity.StateRevision);
        Assert.Equal(seqStart + 1, entity.StepSequence);
    }

    [Fact]
    public void NonPositiveDt_RefreshesVelocityAndFacingButDoesNotMove()
    {
        var entity = CreateEntity(tile: new TileCoord(1, 1), speed: 5d);

        var crossed = entity.IntegrateMovement(Direction8.N.ToUnitVector(), dtSeconds: 0d);

        Assert.False(crossed);
        Assert.Equal(1d, entity.Position.X, Eps);
        Assert.Equal(1d, entity.Position.Y, Eps);
        // Velocity + facing still reflect the held direction even on a zero-dt tick.
        Assert.Equal(5d, entity.Velocity.Length, Eps);
        Assert.Equal(Direction8.N, entity.Facing);
    }

    private static WorldEntity CreateEntity(TileCoord tile, double speed)
    {
        var entity = new WorldEntity(
            id: 1,
            networkId: 1,
            EntityKind.Player,
            tile,
            Direction8.S,
            "Player1",
            Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
        entity.SetSpeedUnitsPerSecond(speed);
        return entity;
    }
}
