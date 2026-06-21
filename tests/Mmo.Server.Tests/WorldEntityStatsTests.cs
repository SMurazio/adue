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
