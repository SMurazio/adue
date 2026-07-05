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

        // BOSS-2 (P1): drone + plating recorders.
        public WorldEntity? Drone;
        public int DroneSpawnCount;
        public readonly List<ulong> AddDespawns = [];
        public readonly List<(ulong BossId, bool Active)> PlatingBroadcasts = [];

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
                notify: (id, text) => Announcements.Add((id, text)),
                spawnDrone: tile =>
                {
                    var drone = World.AddTransient(_nextNetworkId++, EntityKind.Monster, "Interposer Drone", tile, Direction8.S);
                    drone.SetMaxHealthFull(40);
                    Drone = drone;
                    DroneSpawnCount++;
                    return drone;
                },
                despawnAdd: id =>
                {
                    if (World.Remove(id, out _))
                    {
                        AddDespawns.Add(id);
                    }
                },
                broadcastPlating: (bossId, active) => PlatingBroadcasts.Add((bossId, active)));
        }

        // BOSS-2 (P1): begin a fight and run the 3 s countdown so the boss is spawned + Active. Participants enter at
        // the interior entry tiles (so the wipe check sees them alive-in-arena and the fight stays Active while stepped).
        public WorldEntity[] BeginAndSpawnBoss(bool duo)
        {
            var issuer = AddPlayer("Issuer", TownA);
            WorldEntity? partner = duo ? AddPlayer("Partner", TownB) : null;
            Engine.TryBegin(issuer, partner, serverTick: 0, out _);
            StepThrough(1, 60);
            Assert.Equal(EncounterState.Active, Engine.State);
            return partner is null ? [issuer] : [issuer, partner];
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
    public void VictorsWhoNeverLeave_AreEjectedHomeAfterTheGraceWindow_FreeingTheArena()
    {
        // Review BOSS-1 (MEDIUM): the arena is shared and non-instanced — a connected victor who idles inside must
        // NOT hold /boss hostage forever. 15 s after victory, stragglers are teleported to their stored return tiles
        // and the arena frees up.
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Engine.TryBegin(a, b, serverTick: 0, out _);
        h.StepThrough(1, 60);
        Assert.True(h.World.Remove(h.Boss!.Id, out _)); // player kill → victory on the next Step.
        h.Engine.Step(61);
        Assert.Equal(2, h.Engine.ParticipantCount);

        // Just before the 15 s deadline (armed at tick 61 → deadline 61 + 15*20 = 361): still retained.
        h.StepThrough(62, 360);
        Assert.Equal(2, h.Engine.ParticipantCount);
        Assert.Equal(BossArena.IssuerEntryTile, a.TileCoord);

        // Deadline reached: both stragglers are sent to their ORIGINAL return tiles and the arena clears.
        h.Engine.Step(361);
        Assert.Equal(0, h.Engine.ParticipantCount);
        Assert.Equal(TownA, a.TileCoord);
        Assert.Equal(TownB, b.TileCoord);

        // The arena is genuinely free again: a fresh /boss starts a new countdown.
        var c = h.AddPlayer("C", TownA);
        Assert.True(h.Engine.TryBegin(c, partner: null, serverTick: 362, out _));
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

    // ==== BOSS-2 (P1 HUSK): Sundered Plating + fusion shatter + interposer drone ====

    [Fact]
    public void Plating_ReducesDamage_DuoSeventyFive_SoloForty_AndTauntsOnce()
    {
        var duo = new Harness();
        var players = duo.BeginAndSpawnBoss(duo: true);
        var bossId = duo.Engine.BossId;
        Assert.True(duo.Engine.PlatingActive);
        // Spawn broadcast the shell is UP.
        Assert.Contains(duo.PlatingBroadcasts, p => p.BossId == bossId && p.Active);

        Assert.Equal(25, duo.Engine.ModifyIncomingDamage(bossId, 100)); // 75% reduction (duo).
        // The one-shot "your blows turn" taunt is a once-per-FIGHT event, announced to EVERY participant (AnnounceAll)
        // — so exactly one record per player, and a second plated hit adds none.
        foreach (var player in players)
        {
            Assert.Single(duo.Announcements, a => a.Id == player.Id && a.Text.Contains("turns your blows"));
        }

        Assert.Equal(25, duo.Engine.ModifyIncomingDamage(bossId, 100));
        foreach (var player in players)
        {
            Assert.Single(duo.Announcements, a => a.Id == player.Id && a.Text.Contains("turns your blows"));
        }

        // A non-boss monster is never modified.
        Assert.Equal(100, duo.Engine.ModifyIncomingDamage(bossId + 999, 100));

        var solo = new Harness();
        solo.BeginAndSpawnBoss(duo: false);
        Assert.Equal(60, solo.Engine.ModifyIncomingDamage(solo.Engine.BossId, 100)); // 40% reduction (solo).
    }

    [Fact]
    public void FusionShatter_MakesDamageFull_ThenReforms_ToReduced()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var bossId = h.Engine.BossId;

        h.Engine.OnFusion(ProjectileTier.Good, 61);
        Assert.True(h.Engine.WindowOpen);
        Assert.False(h.Engine.PlatingActive); // shell shattered.
        Assert.Contains(h.Announcements, a => a.Text.Contains("SHATTERS"));
        Assert.Contains(h.PlatingBroadcasts, p => p.BossId == bossId && !p.Active);
        Assert.Equal(100, h.Engine.ModifyIncomingDamage(bossId, 100)); // full damage during the window.

        // Good window = 6 s = 120 ticks: still open at 180, reforms exactly at 181 (61 + 120).
        h.StepThrough(62, 180);
        Assert.True(h.Engine.WindowOpen);
        h.Engine.Step(181);
        Assert.False(h.Engine.WindowOpen);
        Assert.True(h.Engine.PlatingActive);
        Assert.Contains(h.Announcements, a => a.Text.Contains("reforms"));
        Assert.Equal(25, h.Engine.ModifyIncomingDamage(bossId, 100)); // reduced again.
    }

    [Fact]
    public void FusionWindowLengths_Good6s_Perfect9s()
    {
        var good = new Harness();
        good.BeginAndSpawnBoss(duo: true);
        good.Engine.OnFusion(ProjectileTier.Good, 61);
        good.StepThrough(62, 180);
        Assert.True(good.Engine.WindowOpen); // 6 s = 120 ticks: open through 180.
        good.Engine.Step(181);
        Assert.False(good.Engine.WindowOpen);

        var perfect = new Harness();
        perfect.BeginAndSpawnBoss(duo: true);
        perfect.Engine.OnFusion(ProjectileTier.Perfect, 61);
        perfect.StepThrough(62, 240);
        Assert.True(perfect.Engine.WindowOpen); // 9 s = 180 ticks: open through 240.
        perfect.Engine.Step(241);
        Assert.False(perfect.Engine.WindowOpen);
    }

    [Fact]
    public void SoloShatter_ThreeHitsWithinSixSeconds_Shatters()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: false);
        var bossId = h.Engine.BossId;

        h.Engine.OnSkillshotMonsterHit(bossId, 61);
        h.Engine.OnSkillshotMonsterHit(bossId, 100);
        Assert.False(h.Engine.WindowOpen); // 2 hits — not yet.
        h.Engine.OnSkillshotMonsterHit(bossId, 120); // 3rd within 6 s of the first (120 - 61 = 59 ticks).
        Assert.True(h.Engine.WindowOpen);
    }

    [Fact]
    public void SoloShatter_HitsSpreadBeyondSixSeconds_DoNotShatter()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: false);
        var bossId = h.Engine.BossId;

        // Each successive hit is > 6 s (120 ticks) after the prior, so the window never holds 3 at once.
        h.Engine.OnSkillshotMonsterHit(bossId, 61);
        h.Engine.OnSkillshotMonsterHit(bossId, 190);
        h.Engine.OnSkillshotMonsterHit(bossId, 330);
        Assert.False(h.Engine.WindowOpen);
    }

    [Fact]
    public void SoloShatter_Counting_IsInertInDuo()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var bossId = h.Engine.BossId;
        h.Engine.OnSkillshotMonsterHit(bossId, 61);
        h.Engine.OnSkillshotMonsterHit(bossId, 62);
        h.Engine.OnSkillshotMonsterHit(bossId, 63);
        Assert.False(h.Engine.WindowOpen); // duo uses fusion, not hit-count.
    }

    [Fact]
    public void Fusion_DuringCountdownOrIdle_IsIgnored()
    {
        // Countdown: TryBegin but no boss yet.
        var countdown = new Harness();
        var issuer = countdown.AddPlayer("Solo", TownA);
        countdown.Engine.TryBegin(issuer, partner: null, serverTick: 0, out _);
        Assert.Equal(EncounterState.Countdown, countdown.Engine.State);
        countdown.Engine.OnFusion(ProjectileTier.Perfect, 5);
        Assert.False(countdown.Engine.WindowOpen);

        // Idle: never began.
        var idle = new Harness();
        idle.Engine.OnFusion(ProjectileTier.Good, 0);
        Assert.False(idle.Engine.WindowOpen);
    }

    [Fact]
    public void Plating_CrumblesPermanently_AtSeventyPercent_FullDamageAndDroneGone()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);
        var bossId = h.Engine.BossId;
        // Let the first drone spawn (5 s = 100 ticks after the boss spawned at tick 60 → tick 160).
        h.StepThrough(61, 160);
        Assert.True(h.Engine.DroneAlive);
        var droneId = h.Engine.DroneId;

        // Drop the boss to exactly 70% of 1200 (= 840): the plating crumbles for good on the next Step.
        h.Boss!.ApplyDamage(1200 - 840);
        h.Engine.Step(161);

        Assert.True(h.Engine.PlatingPermanentlyOff);
        Assert.False(h.Engine.PlatingActive);
        Assert.Equal(100, h.Engine.ModifyIncomingDamage(bossId, 100)); // full damage below 70%.
        Assert.False(h.Engine.DroneAlive);
        Assert.Contains(droneId, h.AddDespawns); // the drone is torn down at the boundary.
        Assert.Contains(h.PlatingBroadcasts, p => p.BossId == bossId && !p.Active);
        Assert.Contains(h.Announcements, a => a.Text.Contains("crumbles for good"));

        // No respawn below 70%.
        h.StepThrough(162, 400);
        Assert.False(h.Engine.DroneAlive);
    }

    [Fact]
    public void Drone_SpawnsAfterFiveSeconds_AndRespawnsSixAfterDeath()
    {
        var h = new Harness();
        h.BeginAndSpawnBoss(duo: true);

        // Boss spawned at tick 60; first drone scheduled 5 s (100 ticks) out → tick 160.
        h.StepThrough(61, 159);
        Assert.Equal(0, h.DroneSpawnCount);
        Assert.False(h.Engine.DroneAlive);
        h.Engine.Step(160);
        Assert.Equal(1, h.DroneSpawnCount);
        Assert.True(h.Engine.DroneAlive);

        // Kill the drone (player kill → it vanishes from the world); the engine detects it and arms a 6 s respawn.
        var droneId = h.Engine.DroneId;
        Assert.True(h.World.Remove(droneId, out _));
        h.Engine.Step(161);
        Assert.False(h.Engine.DroneAlive);

        // Respawn 6 s (120 ticks) after the death was detected → tick 281. Not before.
        h.StepThrough(162, 280);
        Assert.Equal(1, h.DroneSpawnCount);
        h.Engine.Step(281);
        Assert.Equal(2, h.DroneSpawnCount);
        Assert.True(h.Engine.DroneAlive);
    }

    [Fact]
    public void Drone_TornDown_OnWipe_AndOnVictory()
    {
        // Wipe: both participants die in the arena → immediate reset tears the drone down.
        var wipe = new Harness();
        var players = wipe.BeginAndSpawnBoss(duo: true);
        wipe.StepThrough(61, 160);
        var wipeDroneId = wipe.Engine.DroneId;
        Assert.True(wipe.Engine.DroneAlive);
        players[0].ApplyDamage(players[0].Stats.Health);
        players[1].ApplyDamage(players[1].Stats.Health);
        wipe.Engine.Step(161);
        Assert.Equal(EncounterState.Idle, wipe.Engine.State);
        Assert.False(wipe.Engine.DroneAlive);
        Assert.Contains(wipeDroneId, wipe.AddDespawns);

        // Victory: the boss dies (removed) → the drone is torn down while the victors are retained.
        var victory = new Harness();
        victory.BeginAndSpawnBoss(duo: true);
        victory.StepThrough(61, 160);
        var victoryDroneId = victory.Engine.DroneId;
        Assert.True(victory.Engine.DroneAlive);
        Assert.True(victory.World.Remove(victory.Engine.BossId, out _));
        victory.Engine.Step(161);
        Assert.Equal(EncounterState.Idle, victory.Engine.State);
        Assert.False(victory.Engine.DroneAlive);
        Assert.Contains(victoryDroneId, victory.AddDespawns);
    }
}
