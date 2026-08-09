using System.Collections.Generic;
using System.Linq;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// ADUE P1 (todo/S-adue-p1-run-loop-chassis.md, docs/duo-standalone-plan.md P1): headless tests for the ROGUELITE RUN
// CHASSIS — the lobby/ready -> run -> clear-or-wipe -> end screen -> clean reset state machine.
//
// These deliberately drive the run engine ON TOP OF THE REAL BossEncounterEngine, wired exactly as GameServer wires
// it (the run's boss room IS BossEncounterEngine.TryBegin, and its outcome callback IS the run's end edge), and pump
// both per tick in TickCore's order (encounter first, then run). A fake boss room would let the tests assert that the
// run reacts to an outcome the tests themselves invented; going through the real encounter means CLEAR is produced by
// actually killing the boss and WIPE by actually killing the pair in the arena — the live symptoms.
public sealed class RunEngineTests
{
    private const int TickRate = 20;
    private static readonly TileCoord TownA = new(190, 40);
    private static readonly TileCoord TownB = new(196, 40);

    // Where the run's returnPlayer seam sends people (GameServer uses Zone.NextSpawnTile; the harness uses one fixed
    // town tile, which is all the assertions need — "out of the arena, alive, at the lobby anchor").
    private static readonly TileCoord LobbyTile = new(192, 42);

    private sealed class Harness
    {
        public readonly WorldState World = new();
        public readonly BossEncounterEngine Boss;
        public readonly RunEngine Run;

        public readonly List<(ulong Id, string Text)> Notifications = [];
        public readonly List<ulong> Returned = [];
        public readonly List<(ulong Id, RunEngine.RunSummary Summary)> Summaries = [];
        public int StatusPushes;

        public WorldEntity? BossEntity;

        private uint _nextNetworkId = 2000;

        public Harness()
        {
            var gate = new PlayerDamageGate(
                hasActiveIFrames: (_, _) => false,
                onDamageLanded: (_, _, _) => { });

            // The boss room: the REAL encounter engine, every seam wired the minimal way GameServer wires it. Only the
            // pieces this suite touches (spawn/despawn/resolve/teleport/notify) do real work; the P1-P3 mechanic seams
            // are inert recorders (their behaviour is pinned by BossEncounterEngineTests, not re-verified here).
            Boss = new BossEncounterEngine(
                TickRate,
                spawnBoss: (tile, maxHealth) =>
                {
                    var boss = World.AddTransient(_nextNetworkId++, EntityKind.Monster, "The Sunderer", tile, Direction8.S);
                    boss.SetMaxHealthFull(maxHealth);
                    BossEntity = boss;
                    return boss;
                },
                despawnBoss: id => World.Remove(id, out _),
                tryResolve: id => World.TryGet(id, out var e) ? e : null,
                teleport: (player, tile) => player.TeleportTo(tile),
                notify: (id, text) => Notifications.Add((id, text)),
                spawnDrone: tile => World.AddTransient(_nextNetworkId++, EntityKind.Monster, "Drone", tile, Direction8.S),
                despawnAdd: id => World.Remove(id, out _),
                broadcastPlating: (_, _) => { },
                damagePlayer: gate.TryDamagePlayer,
                displacePlayer: (_, _) => { },
                echoCue: _ => { },
                spawnSplinter: tile => World.AddTransient(_nextNetworkId++, EntityKind.Monster, "Splinter", tile, Direction8.S),
                scheduleFieldVisual: (_, _, _, _) => { },
                rootBoss: (boss, tile) => boss.TeleportTo(tile),
                scheduleBeam: (_, _, _, _, _, _, _) => { },
                // THE new seam under test: the encounter's end edge feeds the run chassis.
                encounterEnded: (result, tick) => Run.OnBossRoomEnded(result, tick));

            Run = new RunEngine(
                TickRate,
                tryResolve: id => World.TryGet(id, out var e) ? e : null,
                beginBossRoom: Boss.TryBegin,
                returnPlayer: player =>
                {
                    Returned.Add(player.Id);
                    player.TeleportTo(LobbyTile);
                    player.RestoreFullHealth();
                },
                notify: (id, text) => Notifications.Add((id, text)),
                sendSummary: (id, summary) => Summaries.Add((id, summary)),
                statusChanged: () => StatusPushes++);
        }

        public WorldEntity AddPlayer(string name, TileCoord tile) =>
            World.AddTransient(_nextNetworkId++, EntityKind.Player, name, tile, Direction8.S);

        // One server tick, in TickCore's exact order: the encounter pumps first (it may report an end edge), then the
        // run consumes it. Getting this order wrong is the single most load-bearing wiring detail in the chassis.
        public void Step(uint tick)
        {
            Boss.Step(tick);
            Run.Step(tick);
        }

        public void StepThrough(uint from, uint to)
        {
            for (var t = from; t <= to; t++)
            {
                Step(t);
            }
        }

        // Kill an entity outright (players: leaves the body where it stands, which is what the arena wipe check reads).
        // Mirrors GameServer's death wiring (ApplyPlayerDamage tail, GameServer.cs ~6176-6180): damage to zero,
        // then the run's death edge exactly once on the alive->dead transition. A bare ApplyDamage would bypass
        // the edge every real death goes through and silently undercount the summary's Deaths stat.
        public void Kill(WorldEntity entity)
        {
            var wasAlive = entity.Stats.Health > 0;
            entity.ApplyDamage(entity.Stats.Health);
            if (wasAlive)
            {
                Run.OnPlayerDied(entity.Id);
            }
        }
    }

    // ==== ready gate ====

    [Fact]
    public void SoloReady_StartsRunImmediately_AndEntersTheBossRoom()
    {
        var h = new Harness();
        var solo = h.AddPlayer("Solo", TownA);

        Assert.True(h.Run.TryReady(solo, partner: null, ready: true, serverTick: 1, out var message));

        Assert.Equal(RunPhase.Active, h.Run.Phase);
        Assert.Equal(1, h.Run.RosterCount);
        Assert.True(h.Run.IsRunParticipant(solo.Id));
        Assert.Contains("solo", message, System.StringComparison.OrdinalIgnoreCase);
        // The run's front door really did open the arena: the player was teleported inside its interior.
        Assert.True(BossArena.ContainsInterior(solo.TileCoord));
    }

    [Fact]
    public void DuoReady_WaitsForBothPartners()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);

        Assert.True(h.Run.TryReady(a, b, ready: true, serverTick: 1, out _));
        Assert.Equal(RunPhase.Lobby, h.Run.Phase); // one ready is not enough for a pair.
        Assert.True(h.Run.IsReady(a.Id));
        Assert.False(BossArena.ContainsInterior(a.TileCoord));
        Assert.Contains(h.Notifications, n => n.Id == b.Id && n.Text.Contains("Ready up"));

        Assert.True(h.Run.TryReady(b, a, ready: true, serverTick: 2, out _));
        Assert.Equal(RunPhase.Active, h.Run.Phase);
        Assert.Equal(2, h.Run.RosterCount);
        Assert.True(BossArena.ContainsInterior(a.TileCoord));
        Assert.True(BossArena.ContainsInterior(b.TileCoord));
        // The ready set is consumed by the start — it must not leak into the next lobby.
        Assert.Equal(0, h.Run.ReadyCount);
    }

    [Fact]
    public void Unready_ClearsTheReadyFlag_AndKeepsTheLobby()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);

        h.Run.TryReady(a, b, ready: true, serverTick: 1, out _);
        Assert.True(h.Run.IsReady(a.Id));

        h.Run.TryReady(a, b, ready: false, serverTick: 2, out _);
        Assert.False(h.Run.IsReady(a.Id));

        // The partner readying alone now must NOT start the run (the gate is both, and A is no longer ready).
        h.Run.TryReady(b, a, ready: true, serverTick: 3, out _);
        Assert.Equal(RunPhase.Lobby, h.Run.Phase);
    }

    [Fact]
    public void ReadyDuringALiveRun_IsRefused()
    {
        var h = new Harness();
        var solo = h.AddPlayer("Solo", TownA);
        var bystander = h.AddPlayer("Bystander", TownB);
        h.Run.TryReady(solo, null, ready: true, serverTick: 1, out _);

        Assert.False(h.Run.TryReady(bystander, null, ready: true, serverTick: 2, out var message));
        Assert.Contains("under way", message);
        Assert.Equal(RunPhase.Active, h.Run.Phase);
        Assert.False(h.Run.IsRunParticipant(bystander.Id));
    }

    // ==== the two acceptance transitions ====

    [Fact]
    public void ReadyRunWipeReset_DuoWipeEndsTheRunAndReturnsEveryone()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Run.TryReady(a, b, ready: true, serverTick: 1, out _);
        h.Run.TryReady(b, a, ready: true, serverTick: 1, out _);

        // Run the 3 s countdown so the boss is up and the fight is genuinely Active.
        h.StepThrough(2, 80);
        Assert.NotNull(h.BossEntity);
        Assert.Equal(RunPhase.Active, h.Run.Phase);

        // Take some damage off the boss through the ONE modifier hook, so the summary's damage stat is real.
        var landed = h.Boss.ModifyIncomingDamage(h.BossEntity!.Id, 200);
        h.BossEntity.ApplyDamage(landed);

        // WIPE: both bodies drop inside the arena. The encounter sees "no participant alive in the arena" and reports
        // its wipe edge; the run turns that into the end screen on the same tick pair.
        h.Kill(a);
        h.Kill(b);
        h.StepThrough(81, 84);

        Assert.Equal(RunPhase.Summary, h.Run.Phase);
        Assert.Equal(2, h.Summaries.Count);
        var summary = h.Summaries[0].Summary;
        Assert.Equal(RunOutcome.Wipe, summary.Outcome);
        Assert.Equal(2, summary.Deaths);
        Assert.True(summary.DamageDealt > 0, "the summary should carry the damage actually dealt to the boss.");
        Assert.True(summary.BossHealthPercent is > 0 and <= 100, "a wipe leaves the boss standing with HP left.");

        // EVERYONE was settled: revived, refilled, and returned out of the arena to the lobby anchor.
        Assert.Equal(2, h.Returned.Count);
        foreach (var player in new[] { a, b })
        {
            Assert.Equal(LobbyTile, player.TileCoord);
            Assert.Equal(player.Stats.MaxHealth, player.Stats.Health);
            Assert.False(BossArena.ContainsInterior(player.TileCoord));
        }

        // ... and the boss room was torn down with the run (no orphan boss left standing in the arena).
        Assert.False(h.World.TryGet(h.BossEntity.Id, out _));

        // RESET: the summary window elapses and the chassis returns to a clean lobby, ready for the next run.
        h.StepThrough(85, 85 + (uint)(30 * TickRate) + 5);
        Assert.Equal(RunPhase.Lobby, h.Run.Phase);
        Assert.Equal(0, h.Run.RosterCount);
        Assert.Equal(0, h.Run.ReadyCount);

        // ... and a SECOND run really can start with no restart — the whole point of the chassis.
        h.Run.TryReady(a, b, ready: true, serverTick: 1000, out _);
        h.Run.TryReady(b, a, ready: true, serverTick: 1000, out _);
        Assert.Equal(RunPhase.Active, h.Run.Phase);
    }

    [Fact]
    public void ReadyRunClearReset_KillingTheBossEndsTheRunAsAClear()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Run.TryReady(a, b, ready: true, serverTick: 1, out _);
        h.Run.TryReady(b, a, ready: true, serverTick: 1, out _);
        h.StepThrough(2, 80);
        Assert.NotNull(h.BossEntity);

        // CLEAR: the boss dies. (A live kill runs KillMonster first; either shape resolves to the same victory edge.)
        var landed = h.Boss.ModifyIncomingDamage(h.BossEntity!.Id, 5000);
        h.BossEntity.ApplyDamage(h.BossEntity.Stats.Health);
        Assert.True(landed > 0);

        h.StepThrough(81, 84);

        Assert.Equal(RunPhase.Summary, h.Run.Phase);
        Assert.Equal(2, h.Summaries.Count);
        var summary = h.Summaries[0].Summary;
        Assert.Equal(RunOutcome.Clear, summary.Outcome);
        Assert.Equal(0, summary.Deaths);
        Assert.Equal(0, summary.BossHealthPercent); // a clear leaves nothing standing.
        Assert.True(summary.DurationSeconds <= 5, "the run lasted ~4 s of ticks in this harness.");

        // Victors are pulled out of the arena too — the run, not the encounter's straggler timer, owns the exit.
        Assert.Equal(2, h.Returned.Count);
        Assert.Equal(LobbyTile, a.TileCoord);
        Assert.Equal(LobbyTile, b.TileCoord);

        h.StepThrough(85, 85 + (uint)(30 * TickRate) + 5);
        Assert.Equal(RunPhase.Lobby, h.Run.Phase);
    }

    [Fact]
    public void SoloDeathIsAWipe()
    {
        var h = new Harness();
        var solo = h.AddPlayer("Solo", TownA);
        h.Run.TryReady(solo, null, ready: true, serverTick: 1, out _);
        h.StepThrough(2, 80);

        h.Kill(solo);
        h.StepThrough(81, 84);

        Assert.Equal(RunPhase.Summary, h.Run.Phase);
        Assert.Equal(RunOutcome.Wipe, Assert.Single(h.Summaries).Summary.Outcome);
        Assert.Equal(1, Assert.Single(h.Summaries).Summary.Deaths);
    }

    // ==== death rules ====

    [Fact]
    public void DeadRunParticipant_IsFlaggedAgainstTownRespawn_UntilTheRunEnds()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);
        h.Run.TryReady(a, b, ready: true, serverTick: 1, out _);
        h.Run.TryReady(b, a, ready: true, serverTick: 1, out _);
        h.StepThrough(2, 80);

        // ONE partner down: the run continues (the survivor keeps fighting), and the dead body is still flagged as a
        // run participant — which is exactly what makes GameServer's RespawnPlayers skip its town teleport.
        h.Kill(a);
        h.StepThrough(81, 100);

        Assert.Equal(RunPhase.Active, h.Run.Phase);
        Assert.True(h.Run.IsRunParticipant(a.Id));
        Assert.True(BossArena.ContainsInterior(a.TileCoord), "a downed runner stays in the arena, not in town.");

        // The run ends (the survivor drops too) -> the flag clears and the body is settled.
        h.Kill(b);
        h.StepThrough(101, 104);
        Assert.False(h.Run.IsRunParticipant(a.Id));
        Assert.Equal(LobbyTile, a.TileCoord);
        Assert.Equal(a.Stats.MaxHealth, a.Stats.Health);
    }

    [Fact]
    public void EveryoneDisconnectingMidRun_AbandonsStraightBackToLobby()
    {
        var h = new Harness();
        var solo = h.AddPlayer("Solo", TownA);
        h.Run.TryReady(solo, null, ready: true, serverTick: 1, out _);
        h.StepThrough(2, 80);
        Assert.Equal(RunPhase.Active, h.Run.Phase);

        h.World.Remove(solo.Id, out _); // disconnect.
        h.StepThrough(81, 84);

        // No end screen for nobody: straight back to a clean lobby.
        Assert.Equal(RunPhase.Lobby, h.Run.Phase);
        Assert.Empty(h.Summaries);
        Assert.Equal(0, h.Run.RosterCount);
    }

    // ==== end screen -> next run ====

    [Fact]
    public void ReadyingOnTheEndScreen_DismissesItAndStartsTheNextRun()
    {
        var h = new Harness();
        var solo = h.AddPlayer("Solo", TownA);
        h.Run.TryReady(solo, null, ready: true, serverTick: 1, out _);
        h.StepThrough(2, 80);
        h.Kill(solo);
        h.StepThrough(81, 84);
        Assert.Equal(RunPhase.Summary, h.Run.Phase);

        // One press: the end screen is dismissed AND the next run begins, with no waiting on the summary timer.
        Assert.True(h.Run.TryReady(solo, null, ready: true, serverTick: 90, out _));
        Assert.Equal(RunPhase.Active, h.Run.Phase);
        Assert.True(h.Run.IsRunParticipant(solo.Id));
        Assert.Null(h.Run.LastSummary); // the previous run's summary does not survive into the new one.
    }

    // ==== status projection ====

    [Fact]
    public void StatusFor_ReportsTheReadyGateThenTheLiveRoster()
    {
        var h = new Harness();
        var a = h.AddPlayer("A", TownA);
        var b = h.AddPlayer("B", TownB);

        var lobby = h.Run.StatusFor(a.Id, b.Id);
        Assert.Equal(RunPhase.Lobby, lobby.Phase);
        Assert.Equal(2, lobby.RosterCount);
        Assert.Equal(0, lobby.ReadyCount);
        Assert.False(lobby.SelfReady);

        h.Run.TryReady(a, b, ready: true, serverTick: 1, out _);
        var halfReady = h.Run.StatusFor(a.Id, b.Id);
        Assert.Equal(1, halfReady.ReadyCount);
        Assert.True(halfReady.SelfReady);
        Assert.False(h.Run.StatusFor(b.Id, a.Id).SelfReady);

        // An unpaired player's gate is a roster of one.
        var soloView = h.Run.StatusFor(b.Id, partnerId: null);
        Assert.Equal(1, soloView.RosterCount);

        h.Run.TryReady(b, a, ready: true, serverTick: 2, out _);
        var active = h.Run.StatusFor(a.Id, b.Id);
        Assert.Equal(RunPhase.Active, active.Phase);
        Assert.Equal(2, active.RosterCount);
        Assert.True(active.SelfReady, "inside a run SelfReady doubles as 'you are on the roster'.");

        // A bystander sees the live run but is not on its roster.
        var bystander = h.AddPlayer("Bystander", TownA);
        Assert.False(h.Run.StatusFor(bystander.Id, partnerId: null).SelfReady);
    }

    [Fact]
    public void StatusChanged_FiresOnRealTransitionsOnly()
    {
        var h = new Harness();
        var solo = h.AddPlayer("Solo", TownA);

        var before = h.StatusPushes;
        h.StepThrough(1, 40); // idle lobby ticks push nothing.
        Assert.Equal(before, h.StatusPushes);

        h.Run.TryReady(solo, null, ready: true, serverTick: 41, out _);
        Assert.True(h.StatusPushes > before);

        var afterStart = h.StatusPushes;
        h.StepThrough(42, 80); // a live run pushes nothing per tick either.
        Assert.Equal(afterStart, h.StatusPushes);
    }

    // ==== the /boss dev shortcut must not corrupt run state ====

    [Fact]
    public void BareBossRun_IsInvisibleToTheChassis()
    {
        var h = new Harness();
        var dev = h.AddPlayer("Dev", TownA);

        // Straight into the arena via the encounter's own front door (what /boss does), never through the run engine.
        Assert.True(h.Boss.TryBegin(dev, partner: null, serverTick: 1, out _));
        h.StepThrough(2, 80);
        Assert.NotNull(h.BossEntity);
        Assert.Equal(RunPhase.Lobby, h.Run.Phase);

        // Its ending reports through the same seam — and is ignored, because no run is live.
        h.Kill(dev);
        h.StepThrough(81, 84);
        Assert.Equal(RunPhase.Lobby, h.Run.Phase);
        Assert.Empty(h.Summaries);

        // A real run can still start afterwards (the shortcut left nothing behind).
        dev.RestoreFullHealth();
        Assert.True(h.Run.TryReady(dev, null, ready: true, serverTick: 200, out _));
        Assert.Equal(RunPhase.Active, h.Run.Phase);
    }

    [Fact]
    public void RunRefused_WhenTheArenaIsBusyWithADevRun()
    {
        var h = new Harness();
        var dev = h.AddPlayer("Dev", TownA);
        var player = h.AddPlayer("Player", TownB);

        h.Boss.TryBegin(dev, partner: null, serverTick: 1, out _);
        h.StepThrough(2, 80);

        // The ready is accepted (nothing is wrong with the request) but the run never starts — the room refused, so
        // the chassis stays in the lobby and says why rather than half-starting.
        h.Run.TryReady(player, null, ready: true, serverTick: 81, out _);
        Assert.Equal(RunPhase.Lobby, h.Run.Phase);
        Assert.Equal(0, h.Run.RosterCount);
        Assert.Contains(h.Notifications, n => n.Id == player.Id && n.Text.Contains("cannot start"));
        Assert.False(BossArena.ContainsInterior(player.TileCoord));
    }
}
