using System.Collections.Generic;
using System.Linq;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;
using EncounterState = Mmo.Server.Runtime.BossEncounterEngine.EncounterState;

namespace Mmo.Server.Tests;

// BOSS-1 (docs/boss-encounter-sunderer-design.md): headless tests for the Sunderer encounter lifecycle engine —
// driven exactly as GameServer wires it (every world touch is an injected seam over a bare WorldState), in the SAME
// per-tick shape TickCore runs (TryBegin/TryLeave on the command path, Step once per tick). These pin the lifecycle
// contract: the 3 s countdown transitions to a spawned boss; HP scales 1200 duo / 700 solo at spawn; a full-party
// death resets immediately (wipe); an emptied arena resets only after the 10 s grace window; boss death is a victory
// that returns to Idle while retaining the victors; and the enter/leave teleport bookkeeping restores return tiles.
public sealed class BossEncounterEngineTests
{
    private const int TickRate = 20;
    private static readonly TileCoord TownA = new(190, 40);
    private static readonly TileCoord TownB = new(196, 40);

    // The headless harness: the engine wired over a real WorldState with recording lambdas for every seam (spawn /
    // despawn / resolve / teleport / notify) — the same seams GameServer supplies from SpawnMonsterCore / a leak-free
    // despawn / Zone.Teleport / SendSystem, but recorded so the tests can assert HP scaling, teleport targets, and
    // chat.
    private sealed class Harness
    {
        public readonly WorldState World = new();
        public readonly List<(ulong Id, string Text)> Announcements = [];
        public readonly List<(ulong Id, TileCoord Tile)> Teleports = [];
        public readonly BossEncounterEngine Engine;
        public WorldEntity? Boss;
        public int? BossSpawnHealth;
        public bool BossDespawned;
        private uint _nextNetworkId = 1000;

        public Harness()
        {
            Engine = new BossEncounterEngine(
                TickRate,
                spawnBoss: (tile, maxHealth) =>
                {
                    var boss = World.AddTransient(_nextNetworkId++, EntityKind.Monster, "The Sunderer", tile, Direction8.S);
                    boss.SetMaxHealthFull(maxHealth);
                    Boss = boss;
                    BossSpawnHealth = maxHealth;
                    BossDespawned = false;
                    return boss;
                },
                despawnBoss: id =>
                {
                    if (World.Remove(id, out _))
                    {
                        BossDespawned = true;
                    }
                },
                tryResolve: id => World.TryGet(id, out var e) ? e : null,
                teleport: (player, tile) =>
                {
                    Teleports.Add((player.Id, tile));
                    player.TeleportTo(tile);
                },
                notify: (id, text) => Announcements.Add((id, text)));
        }

        public WorldEntity AddPlayer(string name, TileCoord tile) =>
            World.AddTransient(_nextNetworkId++, EntityKind.Player, name, tile, Direction8.S);

        // Drive the engine from `from` to `to` inclusive (the per-tick Step pump).
        public void StepThrough(uint from, uint to)
        {
            for (var t = from; t <= to; t++)
            {
                Engine.Step(t);
            }
        }
    }

    [Fact]
    public void TryBegin_TeleportsToEntryTiles_AndEntersCountdown()
    {
        var h = new Harness();
        var issuer = h.AddPlayer("Issuer", TownA);
        var partner = h.AddPlayer("Partner", TownB);

        Assert.True(h.Engine.TryBegin(issuer, partner, serverTick: 100, out var msg));
        Assert.Equal(EncounterState.Countdown, h.Engine.State);
        Assert.Equal(2, h.Engine.ParticipantCount);
        Assert.Null(h.Boss); // no boss until the countdown ends.

        // Both were teleported to the fixed entry tiles; the issuer got a chat line.
        Assert.Equal(BossArena.IssuerEntryTile, issuer.TileCoord);
        Assert.Equal(BossArena.PartnerEntryTile, partner.TileCoord);
        Assert.Contains("arena", msg);
        // The partner got its own pulled-in line.
        Assert.Contains(h.Announcements, a => a.Id == partner.Id && a.Text.Contains("pulled you"));
    }

    [Fact]
    public void Countdown_SpawnsBossAtCentre_AfterThreeSeconds()
    {
        var h = new Harness();
        var issuer = h.AddPlayer("Solo", TownA);
        h.Engine.TryBegin(issuer, partner: null, serverTick: 100, out _);

        // The 3 s countdown = 60 ticks at 20 Hz → spawn at tick 160. Strictly before: still counting, no boss.
        h.StepThrough(101, 159);
        Assert.Equal(EncounterState.Countdown, h.Engine.State);
        Assert.Null(h.Boss);

        h.Engine.Step(160);
        Assert.Equal(EncounterState.Active, h.Engine.State);
        Assert.NotNull(h.Boss);
        Assert.Equal(BossArena.BossSpawnTile, h.Boss!.TileCoord);
        // The whole-second countdown was announced (3.. 2.. 1..) plus the awaken line.
        Assert.Contains(h.Announcements, a => a.Text.Contains("3"));
        Assert.Contains(h.Announcements, a => a.Text.Contains("2"));
        Assert.Contains(h.Announcements, a => a.Text.Contains("1"));
        Assert.Contains(h.Announcements, a => a.Text.Contains("awakens"));
    }

    [Fact]
    public void BossHealth_ScalesWithParticipantCount()
    {
        // Duo → 1200.
        var duo = new Harness();
        var a = duo.AddPlayer("A", TownA);
        var b = duo.AddPlayer("B", TownB);
        duo.Engine.TryBegin(a, b, serverTick: 0, out _);
        duo.StepThrough(1, 60);
        Assert.Equal(EncounterState.Active, duo.Engine.State);
        Assert.Equal(BossEncounterEngine.DuoBossHealth, duo.BossSpawnHealth);
        Assert.Equal(1200, duo.Boss!.Stats.MaxHealth);
        Assert.Equal(1200, duo.Boss!.Stats.Health);

        // Solo → 700.
        var solo = new Harness();
        var s = solo.AddPlayer("Solo", TownA);
        solo.Engine.TryBegin(s, partner: null, serverTick: 0, out _);
        solo.StepThrough(1, 60);
        Assert.Equal(BossEncounterEngine.SoloBossHealth, solo.BossSpawnHealth);
        Assert.Equal(700, solo.Boss!.Stats.MaxHealth);
    }

    [Fact]
    public void AllParticipantsDead_ResetsImmediately()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Engine.TryBegin(a, b, serverTick: 0, out _);
        h.StepThrough(1, 60);
        Assert.Equal(EncounterState.Active, h.Engine.State);
        var bossId = h.Boss!.Id;

        // Both die in the arena (Health → 0). This is the pre-RespawnPlayers window the engine's Step runs in.
        a.ApplyDamage(a.Stats.Health);
        b.ApplyDamage(b.Stats.Health);
        Assert.True(a.Stats.Health <= 0 && b.Stats.Health <= 0);

        h.Engine.Step(61);

        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.Equal(0, h.Engine.ParticipantCount);
        Assert.True(h.BossDespawned);
        Assert.False(h.World.TryGet(bossId, out _)); // boss removed on wipe.
    }

    [Fact]
    public void ArenaEmptied_ResetsOnlyAfterTheGraceWindow()
    {
        var h = new Harness();
        var a = h.AddPlayer("Solo", TownA);
        h.Engine.TryBegin(a, partner: null, serverTick: 0, out _);
        h.StepThrough(1, 60);
        Assert.Equal(EncounterState.Active, h.Engine.State);

        // Leave the arena → participants empty; the boss is abandoned (still alive, so NOT a victory).
        Assert.True(h.Engine.TryLeave(a, out _));
        Assert.Equal(0, h.Engine.ParticipantCount);

        // The 10 s grace = 200 ticks. Arm at 61; still Active right up to 260; reset exactly at 261 (200 elapsed).
        h.Engine.Step(61);
        h.StepThrough(62, 260);
        Assert.Equal(EncounterState.Active, h.Engine.State);

        h.Engine.Step(261);
        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.True(h.BossDespawned);
    }

    [Fact]
    public void Disconnect_DuringActive_EmptiesThenResetsAfterGrace()
    {
        var h = new Harness();
        var a = h.AddPlayer("Solo", TownA);
        h.Engine.TryBegin(a, partner: null, serverTick: 0, out _);
        h.StepThrough(1, 60);

        // Disconnect: the entity vanishes from the world (unresolvable). Prune drops it → empty → grace timer.
        Assert.True(h.World.Remove(a.Id, out _));
        h.Engine.Step(61);
        h.StepThrough(62, 260);
        Assert.Equal(EncounterState.Active, h.Engine.State);
        h.Engine.Step(261);
        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.True(h.BossDespawned);
    }

    [Fact]
    public void BossDeath_IsVictory_ReturnsToIdle_RetainingVictors()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Engine.TryBegin(a, b, serverTick: 0, out _);
        h.StepThrough(1, 60);
        var bossId = h.Boss!.Id;

        // Simulate a player kill: KillMonster despawns the boss, so the engine resolves it to null → victory.
        Assert.True(h.World.Remove(bossId, out _));
        h.Engine.Step(61);

        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.Contains(h.Announcements, m => m.Text.Contains("Victory"));
        // Victors are RETAINED so they can walk out via /boss (no auto-eject).
        Assert.Equal(2, h.Engine.ParticipantCount);

        // A fresh /boss is refused while the victors still occupy the arena...
        var c = h.AddPlayer("C", TownA);
        Assert.False(h.Engine.TryBegin(c, partner: null, serverTick: 62, out var deny));
        Assert.Contains("occupied", deny);

        // ...but each victor can leave (teleported home), and then a new encounter can begin.
        Assert.True(h.Engine.TryLeave(a, out _));
        Assert.True(h.Engine.TryLeave(b, out _));
        Assert.Equal(0, h.Engine.ParticipantCount);
        Assert.True(h.Engine.TryBegin(c, partner: null, serverTick: 63, out _));
        Assert.Equal(EncounterState.Countdown, h.Engine.State);
    }

    [Fact]
    public void TryLeave_ReturnsEachPlayerToItsStoredTile()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Engine.TryBegin(a, b, serverTick: 0, out _);

        // Entered → at the entry tiles.
        Assert.Equal(BossArena.IssuerEntryTile, a.TileCoord);
        Assert.Equal(BossArena.PartnerEntryTile, b.TileCoord);

        // Leaving returns each to the ORIGINAL tile it entered from.
        Assert.True(h.Engine.TryLeave(a, out _));
        Assert.Equal(TownA, a.TileCoord);
        Assert.True(h.Engine.TryLeave(b, out _));
        Assert.Equal(TownB, b.TileCoord);
    }

    [Fact]
    public void TryBegin_WhileAnEncounterIsInProgress_IsDenied()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);

        Assert.True(h.Engine.TryBegin(a, partner: null, serverTick: 0, out _));
        Assert.False(h.Engine.TryBegin(b, partner: null, serverTick: 1, out var msg));
        Assert.Contains("already engaged", msg);
    }

    [Fact]
    public void Countdown_CancelsToIdle_WhenEveryoneLeavesBeforeSpawn()
    {
        var h = new Harness();
        var a = h.AddPlayer("Solo", TownA);
        h.Engine.TryBegin(a, partner: null, serverTick: 0, out _);
        Assert.Equal(EncounterState.Countdown, h.Engine.State);

        // Leave mid-countdown → next Step finds the arena empty and cancels (no boss ever spawns).
        Assert.True(h.Engine.TryLeave(a, out _));
        h.Engine.Step(1);

        Assert.Equal(EncounterState.Idle, h.Engine.State);
        Assert.Null(h.Boss);
        Assert.False(h.BossDespawned);
    }
}
