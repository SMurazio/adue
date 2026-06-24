using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// COMBAT-S1: WorldEntity carries server-authoritative vitals (HP/mana/stamina, current+max) and a clamping
// dev-set mutator (TrySetStatCurrent), mirroring the SpeedMultiplier pattern. These pin the defaults, the
// per-stat clamp into [0, max], and the change-detection (no re-replicate on a no-op set).
public sealed class WorldEntityStatsTests
{
    [Fact]
    public void NewEntityHasFullDefaultVitals()
    {
        var entity = CreateEntity();

        Assert.Equal(CharacterStats.Default, entity.Stats);
        Assert.Equal(100, entity.Stats.Health);
        Assert.Equal(100, entity.Stats.MaxMana);
        Assert.Equal(100, entity.Stats.MaxStamina);
    }

    [Fact]
    public void SetCurrentHealthClampsAndReportsChange()
    {
        var entity = CreateEntity();

        Assert.True(entity.TrySetStatCurrent(StatKind.Health, 30));
        Assert.Equal(30, entity.Stats.Health);

        // Below floor clamps to 0; above max clamps to max.
        Assert.True(entity.TrySetStatCurrent(StatKind.Health, -50));
        Assert.Equal(0, entity.Stats.Health);

        Assert.True(entity.TrySetStatCurrent(StatKind.Health, 500));
        Assert.Equal(100, entity.Stats.Health);
    }

    [Fact]
    public void EachStatIsIndependent()
    {
        var entity = CreateEntity();

        Assert.True(entity.TrySetStatCurrent(StatKind.Mana, 10));
        Assert.True(entity.TrySetStatCurrent(StatKind.Stamina, 25));

        Assert.Equal(100, entity.Stats.Health); // untouched
        Assert.Equal(10, entity.Stats.Mana);
        Assert.Equal(25, entity.Stats.Stamina);
    }

    [Fact]
    public void SettingTheSameClampedValueReportsNoChange()
    {
        var entity = CreateEntity();

        // Default health is 100 (== max); setting 100 is a no-op.
        Assert.False(entity.TrySetStatCurrent(StatKind.Health, 100));
        // And setting above max also lands on the existing 100 -> no change.
        Assert.False(entity.TrySetStatCurrent(StatKind.Health, 200));
    }

    // LIVING-ENEMIES P3: ApplyDamage can drive HP to exactly 0 (death trigger). The entity does not auto-die — the
    // caller (GameServer) detects HP<=0 and despawns it — but the stat path must reach 0 cleanly.
    [Fact]
    public void ApplyDamageReachesZeroForDeath()
    {
        var entity = CreateEntity();
        Assert.True(entity.ApplyDamage(60));
        Assert.Equal(40, entity.Stats.Health);
        Assert.True(entity.ApplyDamage(100)); // overkill clamps at 0.
        Assert.Equal(0, entity.Stats.Health);
        // A further hit on a 0-HP body is a no-op (no number, no spam).
        Assert.False(entity.ApplyDamage(10));
    }

    // LIVING-ENEMIES P3: RestoreFullHealth refills current HP to max (respawn at full) and reports a real change.
    [Fact]
    public void RestoreFullHealthRefillsAndReportsChange()
    {
        var entity = CreateEntity();
        entity.ApplyDamage(100);
        Assert.Equal(0, entity.Stats.Health);

        Assert.True(entity.RestoreFullHealth());
        Assert.Equal(entity.Stats.MaxHealth, entity.Stats.Health);

        // No-op at full.
        Assert.False(entity.RestoreFullHealth());
    }

    // LIVING-ENEMIES P3: TeleportTo moves the tile, faces S, resets the movement clocks, and bumps the revision so the
    // jump replicates (the player-respawn teleport).
    [Fact]
    public void TeleportMovesTileAndBumpsRevision()
    {
        var entity = CreateEntity();
        var revisionBefore = entity.StateRevision;
        var seqBefore = entity.StepSequence;

        entity.TeleportTo(new TileCoord(99, 88));

        Assert.Equal(new TileCoord(99, 88), entity.Tile);
        Assert.Equal(Direction8.S, entity.Facing);
        Assert.True(entity.StateRevision > revisionBefore);
        Assert.True(entity.StepSequence > seqBefore);
    }

    private static WorldEntity CreateEntity()
    {
        return new WorldEntity(
            id: 1,
            networkId: 1,
            EntityKind.Player,
            TileGrid.DefaultSpawnTile,
            Direction8.S,
            "Player1",
            Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
    }
}
