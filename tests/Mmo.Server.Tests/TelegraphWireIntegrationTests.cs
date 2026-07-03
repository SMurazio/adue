using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Server.Tests;

// TELEGRAPH T2 (docs/ability-telegraph-sync-design.md): the wire event, pinned against a live server end-to-end
// (mirroring ClearSpawnersIntegrationTests' harness):
//   (1) SCHEDULE-TIME AOI SEND: a viewer already in AOI when /slam schedules a telegraph receives ONE
//       TelegraphMessage for it — reliable, with the locked shape (exact Q12.4 radius — HONEST TELEGRAPH: what is
//       drawn is what resolves) and a resolveTick strictly after startTick (the deadline form);
//   (2) LATE AOI JOIN: a client that logs in MID-WINDUP (after the cast) receives the SAME telegraph — same id,
//       same startTick, same resolveTick — because the per-recipient known-id diff has no "already announced"
//       memory for a fresh session (the SpawnerMarker pattern). Identical ticks are what let the late joiner
//       render the correct REMAINING fill and land on the shared deadline T;
//   (3) NO DUPLICATES: the diff pass never re-sends a known id to either viewer while the telegraph stays pending;
//   (4) RESOLVE WIRING (T1-review followup): a scheduled telegraph actually RESOLVES through the real tick loop —
//       the `_telegraphs.ResolveDue(_serverTick)` call in GameServer.TickCore — landing damage on a victim standing
//       at the locked origin. The scheduler suite drives ResolveDue directly, so this end-to-end pin is what fails
//       if that one TickCore line is deleted (feature dead: telegraphs schedule + announce but never resolve).
public sealed class TelegraphWireIntegrationTests
{
    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140;

    // M3-REVIEW-FOLLOWUPS item 1 (the death-respawn-anchor pin): the REAL genVersion 2 town map, parsed the same
    // way TownAndFloor1MapTests/AuthoredWorldTests do, so the expected respawn anchors are the SHIPPED `S` tiles —
    // not a hand-copied literal that could silently drift from the map.
    private static readonly AuthoredMap AuthoredWorldMap = AuthoredMap.Parse(AuthoredMaps.TownAndFloor1);

    [Fact]
    public async Task ScheduleTimeViewersAndLateJoinersReceiveTheSameTelegraphOnce()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: ["Admin"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var admin = new TelegraphClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn, admin);

            // A LONG windup (8 s) so the telegraph is still pending while the late joiner connects mid-windup.
            admin.SendChat("/slam 2 8000 15");

            // (1) the caster's own client is in AOI of its own cast — the announcement arrives on the next
            // broadcast tick after scheduling.
            await WaitUntilAsync(() => admin.Telegraphs.Count >= 1, admin);
            var cast = admin.Telegraphs[0];
            Assert.Equal(TelegraphShapeKind.Circle, cast.Shape.Kind);
            Assert.Equal(2d, cast.Shape.Radius, 6);          // exact: 2.0 is on the Q12.4 grid
            Assert.True(cast.ResolveTick > cast.StartTick);  // the deadline form: an absolute future tick
            // ~8 s @ 20 Hz = 160 ticks of windup (Ceiling-quantized server-side; allow the rounding tick).
            Assert.InRange(cast.ResolveTick - cast.StartTick, 159u, 161u);

            // (2) late AOI join: a fresh client logs in mid-windup and must receive the SAME telegraph. Clustered
            // spawns + a 64x64 map inside a 30-unit interest radius keep both players in mutual AOI of the origin.
            using var late = new TelegraphClient("Late");
            late.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => late.IsLoggedIn && late.Telegraphs.Count >= 1, admin, late);
            var joined = late.Telegraphs[0];
            Assert.Equal(cast.TelegraphId, joined.TelegraphId);
            Assert.Equal(cast.StartTick, joined.StartTick);      // NOT re-stamped at join time — the shared deadline
            Assert.Equal(cast.ResolveTick, joined.ResolveTick);
            Assert.Equal(cast.Shape, joined.Shape);

            // (3) no duplicates: the known-id diff must not re-announce a pending telegraph on later ticks.
            await PollForAsync(TimeSpan.FromMilliseconds(500), admin, late);
            Assert.Equal(1, admin.Telegraphs.Count(t => t.TelegraphId == cast.TelegraphId));
            Assert.Equal(1, late.Telegraphs.Count(t => t.TelegraphId == cast.TelegraphId));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task ScheduledTelegraphResolvesThroughTheLiveTickLoop_LandingDamageOnce()
    {
        // The ResolveDue WIRING pin (T1-review followup, todo item 2): /slam with a SHORT windup, caster standing
        // still at the locked origin. The 15 damage can ONLY arrive via a real GameServer tick running
        // _telegraphs.ResolveDue(_serverTick) (resolve → origin gather → PlayerDamageGate → OnPlayerDamageLanded →
        // DamageEventMessage to the victim's viewers, victim included — no client-side path fabricates the event
        // and nothing else in this world deals damage). Delete that TickCore call and the telegraph stays pending
        // forever: the announcement in step 1 still arrives, but the damage wait below times out.
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: ["Admin"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var admin = new TelegraphClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn, admin);

            // Short windup (~200 ms = 4 ticks @ 20 Hz) so the resolve happens well inside the wait budget.
            admin.SendChat("/slam 2 200 15");

            // Scheduled + announced (the T2 wire proves the schedule happened)…
            await WaitUntilAsync(() => admin.Telegraphs.Count >= 1, admin);

            // …then RESOLVED through a live tick: the standing caster eats its own slam. (No pre-resolve
            // "still undamaged" assert here — it would race the 4-tick windup on a stalled test thread, and the
            // regression under guard is "never resolves", which the wait below already catches as a timeout.)
            await WaitUntilAsync(() => admin.DamageEvents.Count >= 1, admin);
            Assert.Equal(15, admin.DamageEvents[0].Amount);

            // Resolved ONCE: a due telegraph leaves _pending, so continued polling shows no second hit.
            await PollForAsync(TimeSpan.FromMilliseconds(500), admin);
            Assert.Equal(1, admin.DamageEvents.Count);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task RealMonsterSlamCast_RootsThenLandsAtTheLockedOriginByTheWireResolveTick()
    {
        // SLAM-REVIEW-FOLLOWUPS item 1 (the important one): BasicRoamerBehaviorTests' SlamChannel_*/SlamLeap_*
        // tests drive a FAKE trySlam/beginSlamLeap pair (PlanSlam/CreateSlamLeap) that RE-IMPLEMENTS GameServer's
        // timing formula — a sign/min error in the REAL derivation (leapDurationTicks =
        // min(HopAirborneTicks, windupTicks); LeapStartTick = resolveTick − leapDurationTicks + 1, both inside
        // GameServer.TryBeginMonsterSlam/BeginMonsterSlamLeap) would ship green through that suite alone. This
        // test drives the REAL methods end-to-end: `/monster` spawns a live slime ON the admin's own tile — so it
        // is instantly within BOTH aggro range and the slam trigger range (min(AttackRangeUnits, HopDistanceUnits)
        // = 1.5 for the shipped slime, so this is the earliest/only way to reach the cast live) — and its brain's
        // REAL TrySlamDelegate call schedules the telegraph, then the REAL BeginSlamLeapDelegate executes the leap
        // through the REAL tick loop. Everything below is observed PURELY over the wire (TelegraphMessage +
        // WorldSnapshotMessage), with no test-side reimplementation of the timing math beyond the ONE fact the
        // manifest states directly (slime slamWindupMs=1500 @ 20 Hz Ceiling-quantizes to exactly 30 ticks).
        //
        // DECLINED sub-pins (documented per the todo's own "if impractical, don't force it" guidance — see the
        // review-request briefing for the full reasoning): (b) the reachability-decline case and (c) the
        // airborne-caster-decline case are UNREACHABLE live with the shipped slime numbers (AttackRangeUnits ==
        // HopDistanceUnits == 1.5, so the two gates can never diverge through the real AI trigger; the channel's
        // own root + GameServer's grounded gate mean the brain never even ATTEMPTS a cast while airborne) — this
        // batch is test-only (no manifest edits to author a divergent type, no reflection into the private method).
        // (d) "aims at the QUANTIZED origin, not the raw target" is unobservable over the wire: TelegraphScheduler's
        // quantization and the WorldSnapshot position codec use the IDENTICAL ~1/16-unit PositionEncoding grid, so
        // a raw-vs-quantized landing difference is smaller than the wire can even represent.
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: ["Admin"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var admin = new TelegraphClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn, admin);

            admin.SendChat("/monster");
            await WaitUntilAsync(() => admin.Spawns.Any(s => s.Kind == EntityKind.Monster), admin);
            var monsterId = admin.Spawns.First(s => s.Kind == EntityKind.Monster).NetworkId;

            // The real cast: a TelegraphMessage the slime's own AI scheduled through the real TryBeginMonsterSlam
            // (NextSlamTick is seeded at registration, so the very first eligible tick — once aggro acquires,
            // ~0.5s scan cadence — casts immediately; nothing else in this test schedules a telegraph).
            await WaitUntilAsync(() => admin.Telegraphs.Count >= 1, admin);
            var cast = admin.Telegraphs[0];

            // THE WINDUP-TICK MATH: 1500ms @ 20Hz Ceiling-quantizes to EXACTLY 30 ticks (CooldownMsToTicks) — the
            // first half of the real derivation (resolveTick = serverTick + windupTicks).
            Assert.Equal(30u, cast.ResolveTick - cast.StartTick);

            // NOT EARLY: some sample strictly before the resolve tick (but within the 6-tick airborne window —
            // HopAirborneMs=300 @ 20Hz=6 ticks — the leap is force-included every tick it is in flight) must show
            // the slime still AIRBORNE. A sign/min error that starts (or lands) the leap much too early would fail
            // this by having already grounded well before the resolve tick.
            await WaitUntilAsync(
                () => admin.SamplesFor(monsterId).Any(s => s.Tick < cast.ResolveTick && s.Tick >= cast.ResolveTick - 6 && s.VerticalOffset > 0d),
                admin);

            // LANDS AT THE LOCKED ORIGIN BY THE RESOLVE TICK, GROUNDED: poll until a grounded sample at/after the
            // resolve tick arrives, then confirm it put the slime AT the telegraph's own locked origin — the SAME
            // quantized center the client draws and the resolver tests — not off in the weeds from a derivation bug.
            await WaitUntilAsync(
                () => admin.SamplesFor(monsterId).Any(s => s.Tick >= cast.ResolveTick && s.Tick <= cast.ResolveTick + 3 && s.VerticalOffset == 0d),
                admin);
            var landed = admin.SamplesFor(monsterId)
                .First(s => s.Tick >= cast.ResolveTick && s.Tick <= cast.ResolveTick + 3 && s.VerticalOffset == 0d);
            Assert.True(
                (landed.Position - cast.Shape.Origin).Length <= 0.15d,
                $"landed at {landed.Position} (tick {landed.Tick}), expected the locked origin {cast.Shape.Origin} (cast at tick {cast.StartTick}, resolve {cast.ResolveTick}).");
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task DeathRespawnLandsOnAnAuthoredSpawnAnchor_NotLegacyWilderness()
    {
        // M3-REVIEW-FOLLOWUPS item 1 (MEDIUM-HIGH review finding, already FIXED by the orchestrator:
        // RespawnPlayers now calls Zone.NextSpawnTile instead of the legacy Zone.DefaultSpawnTile). This is the
        // regression pin the review required: on the REAL 384x384 authored town map (this file's other tests use
        // a small 64x64 procedural world, which has no authored anchors to test against), kill the admin's own
        // character with a lethal /slam, let the (shortened, for test speed) respawn delay elapse, and assert the
        // respawned tile is one of the map's authored `S` anchors — never TileGrid.DefaultSpawnTile (8,8), which
        // is walkable on the 384x384 world (the bug shipped SILENTLY: no crash, just a player waking up alone in
        // the far southwest instead of the plaza). Reverting RespawnPlayers' NextSpawnTile call back to
        // DefaultSpawnTile is exactly what this test catches.
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateAuthoredOptions(port, database.ConnectionString, admins: ["Admin"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var admin = new TelegraphClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn && admin.OwnNetworkId != 0, admin);

            // Shrink the respawn delay (live default 2000ms) so the test doesn't wait on it.
            admin.SendAdminSetTuning("player.respawnMs", 100d);
            await PollForAsync(TimeSpan.FromMilliseconds(150), admin); // let the tuning change land before the kill

            // Login already sent a "full HP" PlayerStatsMessage (the initial truth) — clear it so the wait below
            // for a refilled-HP message can only be satisfied by the POST-RESPAWN one, not this stale login one.
            admin.ClearStats();

            // A lethal self-slam: short windup, damage well past the 100 HP default so ONE hit kills.
            admin.SendChat("/slam 2 200 500");
            await WaitUntilAsync(() => admin.DamageEvents.Count >= 1, admin);

            // Respawn confirmed by the refilled-vitals broadcast: RespawnPlayers -> RestoreFullHealth ->
            // SendPlayerStats fires strictly AFTER the teleport in the same per-tick pass.
            await WaitUntilAsync(() => admin.Stats.Any(s => s.Stats.Health == s.Stats.MaxHealth && s.Stats.MaxHealth > 0), admin);
            await PollForAsync(TimeSpan.FromMilliseconds(200), admin); // let the post-teleport snapshot land too

            Assert.True(admin.SamplesFor(admin.OwnNetworkId).Count > 0, "never observed a WorldSnapshot sample for the admin's own entity.");
            var respawnedTile = admin.SamplesFor(admin.OwnNetworkId)[^1].Position.ToTileRounded();

            Assert.Contains(respawnedTile, AuthoredWorldMap.SpawnTiles);
            Assert.NotEqual(new TileCoord(8, 8), respawnedTile); // TileGrid.DefaultSpawnTile — the legacy fallback, named explicitly
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static ServerOptions CreateAuthoredOptions(int port, string connectionString, string[] admins)
    {
        return new ServerOptions(
            port,
            TickRate,
            "telegraph-wire-authored-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            AuthoredMaps.TownAndFloor1Width,
            AuthoredMaps.TownAndFloor1Height,
            BaseStepCooldownMs,
            15,
            30f,
            150,
            SpawnDistribution.Authored,
            new HashSet<string>(admins, StringComparer.OrdinalIgnoreCase))
        {
            GenVersion = TerrainGenerator.AuthoredGenVersion,
            ResourceNodeDensityTilesPerNode = 0,
        };
    }

    private static ServerOptions CreateOptions(int port, string connectionString, string[] admins)
    {
        return new ServerOptions(
            port,
            TickRate,
            "telegraph-wire-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            BaseStepCooldownMs,
            15,
            30f,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(admins, StringComparer.OrdinalIgnoreCase))
        {
            ResourceNodeDensityTilesPerNode = 0,
        };
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params TelegraphClient[] clients)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var client in clients)
            {
                client.Poll();
            }

            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for telegraph wire integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params TelegraphClient[] clients)
    {
        var stopAt = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            foreach (var client in clients)
            {
                client.Poll();
            }

            await Task.Delay(10);
        }
    }

    // A minimal client tracking exactly what T2 replicates: the TelegraphMessage stream (every arrival kept, so a
    // duplicate send is visible as a second list entry). Mirrors ClearSpawnersIntegrationTests.ClearSpawnersClient.
    private sealed class TelegraphClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private bool _disposed;

        public TelegraphClient(string name)
        {
            _name = name;
            _client = new NetManager(_listener) { AutoRecycle = false };
            _listener.PeerConnectedEvent += peer =>
            {
                _serverPeer = peer;
                Send(new ClientHelloMessage(_name), DeliveryMethod.ReliableOrdered);
                Send(new LoginRequestMessage(_name, _name), DeliveryMethod.ReliableOrdered);
            };
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public List<TelegraphMessage> Telegraphs { get; } = [];

        // T1-review followup (the ResolveDue wiring pin): the DamageEventMessage stream — a telegraph that RESOLVED
        // through the live tick loop lands damage, and the landed tail broadcasts this event to the victim's viewers
        // (including the victim itself). Every arrival kept, like Telegraphs, so a double resolve is visible.
        public List<DamageEventMessage> DamageEvents { get; } = [];
        public bool IsLoggedIn { get; private set; }

        // SLAM-REVIEW-FOLLOWUPS item 1 (the real-TryBeginMonsterSlam integration pin) + M3-REVIEW-FOLLOWUPS item 1
        // (the death-respawn-anchor pin): both need to identify a spawned entity (the /monster slime; the admin's
        // own player) by NetworkId and read its WorldSnapshot position/VerticalOffset OVER TIME (per-tick samples,
        // not just the latest) — the leap-landing pin specifically needs to see BOTH an airborne sample before the
        // resolve tick AND a grounded-at-the-origin sample at/after it, so only the newest sample is not enough.
        public List<EntitySpawnMessage> Spawns { get; } = [];
        public List<PlayerStatsMessage> Stats { get; } = [];
        public uint OwnNetworkId { get; private set; }

        private readonly Dictionary<uint, List<(uint Tick, WorldVector Position, double VerticalOffset)>> _samples = [];

        public IReadOnlyList<(uint Tick, WorldVector Position, double VerticalOffset)> SamplesFor(uint networkId) =>
            _samples.TryGetValue(networkId, out var list) ? list : [];

        public void Connect(int port, string key)
        {
            _client.Start();
            _client.Connect("127.0.0.1", port, key);
        }

        public void Poll()
        {
            if (!_disposed)
            {
                _client.PollEvents();
            }
        }

        public void SendChat(string text) =>
            Send(new ChatSendMessage(text), DeliveryMethod.ReliableOrdered);

        public void SendAdminSetTuning(string key, double value) =>
            Send(new AdminSetTuningMessage(key, value), DeliveryMethod.ReliableOrdered);

        // The initial login sends a PlayerStatsMessage too (full HP, the "initial truth" per COMBAT-S1) — a test
        // waiting on "HP refilled" must clear this first, or the wait is satisfied instantly by the LOGIN message
        // and never actually observes the post-respawn refill.
        public void ClearStats() => Stats.Clear();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _serverPeer?.Disconnect();
            _client.PollEvents();
            _client.Stop();
            _disposed = true;
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                var message = ProtocolCodec.Decode(reader.GetRemainingBytes());
                switch (message)
                {
                    case LoginResultMessage login:
                        IsLoggedIn = login.Accepted;
                        break;
                    case TelegraphMessage telegraph:
                        Telegraphs.Add(telegraph);
                        break;
                    case DamageEventMessage damage:
                        DamageEvents.Add(damage);
                        break;
                    case EntitySpawnMessage spawn:
                        Spawns.Add(spawn);
                        if (spawn.DisplayName == _name && spawn.Kind == EntityKind.Player)
                        {
                            OwnNetworkId = spawn.NetworkId;
                        }

                        break;
                    case PlayerStatsMessage stats:
                        Stats.Add(stats);
                        break;
                    case WorldSnapshotMessage snapshot:
                        foreach (var entity in snapshot.Entities)
                        {
                            if (!_samples.TryGetValue(entity.NetworkId, out var list))
                            {
                                list = [];
                                _samples[entity.NetworkId] = list;
                            }

                            list.Add((snapshot.ServerTick, entity.Position, entity.VerticalOffset));
                        }

                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        private void Send(IProtocolMessage message, DeliveryMethod deliveryMethod)
        {
            _serverPeer?.Send(ProtocolCodec.Encode(message), deliveryMethod);
        }
    }
}
