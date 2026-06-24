using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// LIVING-ENEMIES P3: unit coverage for the persistent monster SPAWNER object — the server object that OWNS a monster,
// schedules its respawn on death, and (the whole point) PERSISTS across the death/respawn so the red marker tile stays
// put. These pin the lifecycle the orchestrator/GameServer drives: attach a live monster, notify death (schedule the
// respawn off the type's delay), the respawn becomes due after the delay, and a stale death notification is ignored.
public sealed class MonsterSpawnerTests
{
    private static MonsterSpawner NewSpawner() =>
        new(spawnerId: 7, new TileCoord(10, 12), new MonsterType("slime", "Slime"));

    [Fact]
    public void NewSpawnerHasNoLiveMonsterOrPendingRespawn()
    {
        var spawner = NewSpawner();

        Assert.Null(spawner.LiveMonsterId);
        Assert.Null(spawner.RespawnAtTick);
        Assert.False(spawner.IsRespawnDue(0));
        Assert.Equal(7u, spawner.SpawnerId);
        Assert.Equal(new TileCoord(10, 12), spawner.Tile);
    }

    [Fact]
    public void AttachMonsterSetsLiveAndClearsRespawn()
    {
        var spawner = NewSpawner();

        spawner.AttachMonster(monsterId: 100);

        Assert.Equal(100ul, spawner.LiveMonsterId);
        Assert.Null(spawner.RespawnAtTick);
        // A live monster is never "respawn due".
        Assert.False(spawner.IsRespawnDue(99999));
    }

    [Fact]
    public void DeathSchedulesRespawnAfterDelayAndPersists()
    {
        var spawner = NewSpawner();
        spawner.AttachMonster(monsterId: 100);

        // Dies at tick 50 with a 100-tick respawn delay.
        Assert.True(spawner.NotifyMonsterDied(100, serverTick: 50, respawnDelayTicks: 100));

        // The live monster is cleared (the entity despawned) but the SPAWNER persists with a scheduled respawn.
        Assert.Null(spawner.LiveMonsterId);
        Assert.Equal(150u, spawner.RespawnAtTick);

        // Not due before the delay elapses; due at/after.
        Assert.False(spawner.IsRespawnDue(149));
        Assert.True(spawner.IsRespawnDue(150));
        Assert.True(spawner.IsRespawnDue(200));

        // Respawning (attach a fresh monster) clears the schedule and the spawner owns the new monster.
        spawner.AttachMonster(monsterId: 101);
        Assert.Equal(101ul, spawner.LiveMonsterId);
        Assert.Null(spawner.RespawnAtTick);
        Assert.False(spawner.IsRespawnDue(300));
    }

    [Fact]
    public void StaleDeathNotificationForOldMonsterIsIgnored()
    {
        var spawner = NewSpawner();
        spawner.AttachMonster(monsterId: 100);
        Assert.True(spawner.NotifyMonsterDied(100, serverTick: 10, respawnDelayTicks: 50));
        spawner.AttachMonster(monsterId: 200); // respawned with a new id.

        // A late death notification for the OLD monster id must not disturb the live one or re-schedule.
        Assert.False(spawner.NotifyMonsterDied(100, serverTick: 80, respawnDelayTicks: 50));
        Assert.Equal(200ul, spawner.LiveMonsterId);
        Assert.Null(spawner.RespawnAtTick);
    }

    [Fact]
    public void ZeroDelayRespawnIsDueImmediately()
    {
        var spawner = NewSpawner();
        spawner.AttachMonster(monsterId: 1);

        Assert.True(spawner.NotifyMonsterDied(1, serverTick: 500, respawnDelayTicks: 0));
        Assert.Equal(500u, spawner.RespawnAtTick);
        Assert.True(spawner.IsRespawnDue(500));
    }
}
