using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// Phase 1 (continuous migration): PLAYER movement is now CONTINUOUS — Position += Velocity x dt via
// WorldEntity.IntegrateMovement, NOT a tile-step off the step cooldown. (The tile-step path — TryStep — survives for
// MONSTERS and the attack-root; it is covered by WorldEntitySpeedTests / ZoneTests / MonsterRoamAiTests.) These tests
// assert the player movement SEMANTICS the flip introduces: continuous advance, instant stop, facing from direction,
// no walkability clamp (players walk through "walls" — Phase 2 adds real collision), and the attack-root freeze (R2)
// still rooting a player via IsMovementFrozen. The focused port of the proven exp:ContinuousMover lives in
// WorldEntityIntegratorTests; this file pins the player-path behaviour and its interaction with the root gate.
public sealed class WorldEntityMovementTests
{
    private const double Eps = 1e-9;

    [Fact]
    public void IntegratingAdvancesPositionContinuously_NotByWholeTiles()
    {
        var entity = CreateEntity(tile: new TileCoord(8, 8), speed: 4d);

        // 4 units/s * 0.05s = 0.2 units east — a sub-tile advance the tile-step model could never express.
        var crossed = entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.05d);

        Assert.False(crossed);                                  // no rounded-tile crossing yet
        Assert.Equal(8.2d, entity.Position.X, Eps);            // continuous, fractional position
        Assert.Equal(8d, entity.Position.Y, Eps);
        Assert.Equal(new TileCoord(8, 8), entity.TileCoord);   // still rounds to the origin tile
        Assert.Equal(Direction8.E, entity.Facing);             // faced from the direction
    }

    [Fact]
    public void IntegratingFacesFromTheHeldDirection()
    {
        var entity = CreateEntity(tile: new TileCoord(8, 8), facing: Direction8.S, speed: 4d);

        entity.IntegrateMovement(Direction8.NE.ToUnitVector(), dtSeconds: 0.05d);

        Assert.Equal(Direction8.NE, entity.Facing);
    }

    [Fact]
    public void CrossingARoundedTileBumpsStateRevisionAndStepSequence()
    {
        var entity = CreateEntity(tile: new TileCoord(8, 8), speed: 10d);
        var revisionBefore = entity.StateRevision;
        var seqBefore = entity.StepSequence;

        // 10 units/s * 0.06s = 0.6 east -> x = 8.6 rounds to tile (9,8): a crossing.
        var crossed = entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.06d);

        Assert.True(crossed);
        Assert.Equal(new TileCoord(9, 8), entity.TileCoord);
        Assert.Equal(revisionBefore + 1, entity.StateRevision);
        Assert.Equal(seqBefore + 1, entity.StepSequence);
    }

    [Fact]
    public void StopMovementHaltsInstantly_NoGlide()
    {
        var entity = CreateEntity(tile: new TileCoord(8, 8), speed: 5d);
        entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.1d); // moving
        var xAfterMove = entity.Position.X;

        entity.StopMovement();

        Assert.Equal(0d, entity.Velocity.Length, Eps);
        // A subsequent stopped tick does not drift the position.
        entity.IntegrateMovement(WorldVector.Zero, dtSeconds: 0.1d);
        Assert.Equal(xAfterMove, entity.Position.X, Eps);
    }

    [Fact]
    public void EntityIntegratorIsGridAgnostic_NoCollisionAtThisLayer()
    {
        // Phase 2: collision lives at the ZONE layer (Zone.IntegrateMovement queries walls + runs
        // ContinuousCollision.Resolve), NOT inside WorldEntity — the entity integrator stays grid-agnostic. So a bare
        // WorldEntity.IntegrateMovement (no grid in scope) still advances straight, unobstructed. The wall-block FLIP
        // (a player stopping at a blocked tile) is pinned at the server layer in ZoneContinuousCollisionTests; do not
        // "fix" this entity-level test to collide.
        var entity = CreateEntity(tile: new TileCoord(8, 8), speed: 10d);

        for (var i = 0; i < 5; i++)
        {
            entity.IntegrateMovement(Direction8.E.ToUnitVector(), dtSeconds: 0.1d); // 1 unit/tick east
        }

        // Advanced ~5 tiles east unobstructed — no clamp, no snag.
        Assert.Equal(13d, entity.Position.X, Eps);
        Assert.Equal(new TileCoord(13, 8), entity.TileCoord);
    }

    [Fact]
    public void AttackRootFreezesThePlayer_IsMovementFrozenGatesIntegration()
    {
        // R2: a committed swing roots the player's movement by pushing _nextEligibleTick forward
        // (ApplyAttackMovementRoot). While serverTick is inside that window IsMovementFrozen is true and the
        // integrator caller must skip the move (the combat invariant).
        var entity = CreateEntity(tile: new TileCoord(8, 8), speed: 5d);
        entity.ApplyAttackMovementRoot(serverTick: 10, rootTicks: 5); // rooted until tick 15

        Assert.True(entity.IsMovementFrozen(10));
        Assert.True(entity.IsMovementFrozen(14));
        Assert.False(entity.IsMovementFrozen(15)); // window elapsed
        Assert.False(entity.IsMovementFrozen(20));
    }

    [Fact]
    public void NeverRootedPlayerIsNeverFrozen()
    {
        var entity = CreateEntity(tile: new TileCoord(8, 8), speed: 5d);
        Assert.False(entity.IsMovementFrozen(0));
        Assert.False(entity.IsMovementFrozen(1000));
    }

    private static WorldEntity CreateEntity(
        TileCoord tile,
        Direction8 facing = Direction8.S,
        double speed = 4d)
    {
        var entity = new WorldEntity(
            id: 1,
            networkId: 1,
            EntityKind.Player,
            tile,
            facing,
            "Player1",
            Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
        entity.SetSpeedUnitsPerSecond(speed);
        return entity;
    }
}
