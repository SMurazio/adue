using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// S51: per-entity movement speed. WorldEntity carries a SpeedMultiplier and derives a clamped effective
// step cooldown (in ticks) from the server's base cooldown. These tests pin the derivation + clamp, and
// prove that a faster multiplier actually steps more often over a fixed tick window than a default entity.
public sealed class WorldEntitySpeedTests
{
    private const uint BaseCooldownTicks = 4;   // e.g. 140ms @ 20Hz tick
    private const uint MinTicks = 1;
    private const uint MaxTicks = 100;

    [Fact]
    public void DefaultMultiplierKeepsBaseCadence()
    {
        var entity = CreateEntity();

        Assert.Equal(1.0, entity.SpeedMultiplier);
        Assert.Equal(BaseCooldownTicks, entity.EffectiveStepCooldownTicks(BaseCooldownTicks, MinTicks, MaxTicks));
    }

    [Fact]
    public void DoubleSpeedHalvesCooldown()
    {
        var entity = CreateEntity();
        Assert.True(entity.TrySetSpeedMultiplier(2.0));

        Assert.Equal(2u, entity.EffectiveStepCooldownTicks(BaseCooldownTicks, MinTicks, MaxTicks));
    }

    [Fact]
    public void SlowMultiplierLengthensCooldown()
    {
        var entity = CreateEntity();
        Assert.True(entity.TrySetSpeedMultiplier(0.5));

        Assert.Equal(8u, entity.EffectiveStepCooldownTicks(BaseCooldownTicks, MinTicks, MaxTicks));
    }

    [Fact]
    public void ExtremeFastMultiplierIsClampedToMin()
    {
        var entity = CreateEntity();
        Assert.True(entity.TrySetSpeedMultiplier(10_000));

        // Without the clamp this would round to 0 ticks (every-tick stepping); the floor keeps it >= MinTicks.
        Assert.Equal(MinTicks, entity.EffectiveStepCooldownTicks(BaseCooldownTicks, MinTicks, MaxTicks));
    }

    [Fact]
    public void ExtremeSlowMultiplierIsClampedToMax()
    {
        var entity = CreateEntity();
        Assert.True(entity.TrySetSpeedMultiplier(0.0001));

        Assert.Equal(MaxTicks, entity.EffectiveStepCooldownTicks(BaseCooldownTicks, MinTicks, MaxTicks));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidMultipliersAreRejected(double multiplier)
    {
        var entity = CreateEntity();
        Assert.False(entity.TrySetSpeedMultiplier(multiplier));
        Assert.Equal(1.0, entity.SpeedMultiplier);
    }

    [Fact]
    public void SettingSameMultiplierReportsNoChange()
    {
        var entity = CreateEntity();
        Assert.True(entity.TrySetSpeedMultiplier(1.5));
        Assert.False(entity.TrySetSpeedMultiplier(1.5));
    }

    // The behavioural contract: a 2x entity moves about twice as often as a default entity, and a 0.5x entity
    // about half as often, when both are paced every tick over a fixed window by the SAME cadence gate. The
    // effective cooldown derived from the multiplier drives the movement-cadence clock (_nextEligibleTick) via
    // TryBeginHop — the surviving consumer of EffectiveStepCooldownTicks (the monster hop; the player path is
    // now continuous and ungated). Counting accepted hops over the window pins the faster=more invariant.
    [Fact]
    public void FasterEntityMovesMoreOftenOverAFixedWindow()
    {
        const int ticks = 40;
        var defaultMoves = CountMoves(multiplier: 1.0, ticks);
        var fastMoves = CountMoves(multiplier: 2.0, ticks);
        var slowMoves = CountMoves(multiplier: 0.5, ticks);

        // base 4-tick cooldown over 40 ticks: default ~10, fast (2-tick) ~20, slow (8-tick) ~5.
        Assert.Equal(10, defaultMoves);
        Assert.Equal(20, fastMoves);
        Assert.Equal(5, slowMoves);
        Assert.True(fastMoves > defaultMoves && defaultMoves > slowMoves);
    }

    private static int CountMoves(double multiplier, int ticks)
    {
        var entity = CreateEntity(tile: new TileCoord(128, 128), facing: Direction8.E);
        // Note: setting 1.0 on an already-default entity is a no-op that returns false, so don't assert the
        // return here — we only need the entity to END at `multiplier` (it does, including the 1.0 case).
        entity.TrySetSpeedMultiplier(multiplier);
        Assert.Equal(multiplier, entity.SpeedMultiplier);
        var cooldown = entity.EffectiveStepCooldownTicks(BaseCooldownTicks, MinTicks, MaxTicks);

        var moves = 0;
        for (uint tick = 1; tick <= ticks; tick++)
        {
            // Pace the entity every tick via the movement-cadence gate (TryBeginHop arms _nextEligibleTick =
            // tick + cooldown, exactly the cadence the old tile-step path armed on an accepted step). An accepted
            // begin counts as one move; a begin inside the cooldown window is rejected and mutates nothing.
            if (entity.TryBeginHop(tick, cooldown))
            {
                moves++;
            }
        }

        return moves;
    }

    private static WorldEntity CreateEntity(TileCoord? tile = null, Direction8 facing = Direction8.S)
    {
        return new WorldEntity(
            id: 1,
            networkId: 1,
            EntityKind.Player,
            tile ?? TileGrid.DefaultSpawnTile,
            facing,
            "Player1",
            Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
    }
}
