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

// ADUE P2-A (todo/S-p2-practice-room-and-dummy.md): SESSION-LEVEL symptom tests for the practice room + dummy server
// infrastructure. Drives a live GameServer over the loopback (the ClearSpawners / RunLoopSession integration tests are
// the precedent) and observes the symptoms through the wire: the caller's own replicated tile (in-room membership),
// server system lines, and the dummy's entity presence (EntitySpawn / EntityDespawn of the "Practice Dummy" monster).
//
// The room is a SEALED pocket on the AUTHORED 384x384 map (PracticeRoom tiles 8-31 x, 352-375 y) — so this harness MUST
// generate that map (GenVersion = AuthoredGenVersion at the authored dims), exactly like RunLoopSessionIntegrationTests,
// or the room is never stamped and the dummy is never placed.
public sealed class PracticeRoomIntegrationTests
{
    private const int TickRate = 20;

    // Acceptance: /practice teleports a solo caller into the room + spawns a dummy; /practice off returns them to town
    // and despawns it.
    [Fact]
    public async Task SoloPractice_EntersRoomAndSpawnsDummy_OffReturnsToTownAndDespawns()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: []);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var solo = new PracticeClient("Solo");
            solo.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => solo.IsLoggedIn && solo.OwnNetworkId != 0, solo);
            Assert.False(PracticeRoom.ContainsInterior(solo.OwnTile), "should start in town, not the practice room.");

            // ENTER: teleported into the room + a dummy spawns.
            solo.SendChat("/practice");
            await WaitUntilAsync(() => PracticeRoom.ContainsInterior(solo.OwnTile) && solo.DummyNetworkId != 0, solo);
            Assert.True(solo.HasSystemLine("Entered the practice room"));
            Assert.False(solo.DummyDespawned);

            // LEAVE: back to town + the dummy despawns (its EntityDespawn arrives via the normal AOI known-entity diff).
            var dummyId = solo.DummyNetworkId;
            solo.SendChat("/practice off");
            await WaitUntilAsync(() => !PracticeRoom.ContainsInterior(solo.OwnTile) && solo.SawDespawn(dummyId), solo);
            Assert.True(solo.HasSystemLine("You leave the practice room"));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // Acceptance: a paired caller brings the online partner; the dummy despawns only when BOTH have left. Bravo stays in
    // the room (a continuous observer) while Alpha leaves first, proving the dummy survives the first departure.
    [Fact]
    public async Task PairedPractice_BringsPartner_AndDummyDespawnsOnlyWhenBothHaveLeft()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: []);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var a = new PracticeClient("Alpha");
            using var b = new PracticeClient("Bravo");
            a.Connect(port, options.ConnectionKey);
            b.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => a.IsLoggedIn && a.OwnNetworkId != 0 && b.IsLoggedIn && b.OwnNetworkId != 0, a, b);

            a.SendChat("/pair Bravo");
            await WaitUntilAsync(() => a.IsPaired && b.IsPaired, a, b);

            // Alpha enters: BOTH partners are teleported in and one shared dummy spawns.
            a.SendChat("/practice");
            await WaitUntilAsync(
                () => PracticeRoom.ContainsInterior(a.OwnTile) && PracticeRoom.ContainsInterior(b.OwnTile)
                    && a.DummyNetworkId != 0 && b.DummyNetworkId != 0, a, b);
            var dummyId = a.DummyNetworkId;
            Assert.True(b.HasSystemLine("Pulled into the practice room"));

            // Alpha leaves first. The dummy must SURVIVE (Bravo is still practicing): for a solid window Bravo — who
            // stays in AOI of the room — must NOT see the dummy despawn, and stays in the room.
            a.SendChat("/practice off");
            await WaitUntilAsync(() => !PracticeRoom.ContainsInterior(a.OwnTile), a, b);
            var watchUntil = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(700);
            while (DateTimeOffset.UtcNow < watchUntil)
            {
                a.Poll();
                b.Poll();
                Assert.False(b.SawDespawn(dummyId), "the dummy must not despawn while a partner is still practicing.");
                Assert.True(PracticeRoom.ContainsInterior(b.OwnTile), "the remaining partner must still be in the room.");
                await Task.Delay(20);
            }

            // Bravo leaves too — now the room is empty, so the dummy finally despawns.
            b.SendChat("/practice off");
            await WaitUntilAsync(() => !PracticeRoom.ContainsInterior(b.OwnTile) && b.SawDespawn(dummyId), a, b);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // H1 review regression: a PARTNER-initiated /ready must NOT drag a still-practicing partner into a run (nor orphan
    // the dummy). The exact repro: A readies, then A enters practice (pulling B in), then B leaves and readies. Before
    // the fix, A's stale ready flag + a self-only guard let StartRun(B, A) teleport A out of the room and onto the
    // roster. After the fix A stays put, no run starts, and the dummy is still owned by the room (despawns cleanly when
    // A finally leaves). Distinct from APracticeOccupantIsNotPulledIntoAnUnrelatedActiveRun, whose runner is UNRELATED.
    [Fact]
    public async Task PairedPractice_APartnersReadyCannotDragAPracticingPlayerIntoARun()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: []);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var a = new PracticeClient("Alpha");
            using var b = new PracticeClient("Bravo");
            a.Connect(port, options.ConnectionKey);
            b.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => a.IsLoggedIn && a.OwnNetworkId != 0 && b.IsLoggedIn && b.OwnNetworkId != 0, a, b);

            a.SendChat("/pair Bravo");
            await WaitUntilAsync(() => a.IsPaired && b.IsPaired, a, b);

            // (1) A readies — paired, so it waits for B (no run yet). (2) A enters practice, pulling B in; A's stale
            // ready flag is cleared by entering.
            a.SendChat("/ready");
            await WaitUntilAsync(() => a.HasSystemLine("Waiting for your partner"), a, b);

            a.SendChat("/practice");
            await WaitUntilAsync(
                () => PracticeRoom.ContainsInterior(a.OwnTile) && PracticeRoom.ContainsInterior(b.OwnTile)
                    && a.DummyNetworkId != 0, a, b);
            var dummyId = a.DummyNetworkId;

            // (3) B leaves. (4) B readies — refused, because partner A is still practicing. NO run starts.
            b.SendChat("/practice off");
            await WaitUntilAsync(() => !PracticeRoom.ContainsInterior(b.OwnTile), a, b);
            b.SendChat("/ready");
            await WaitUntilAsync(() => b.HasSystemLine("partner is in the practice room"), a, b);

            // For a solid window: A must STAY in the room (never yanked into the arena), and neither client ever sees the
            // run go Active — the partner-ready could not start a run that would drag A in.
            var watchUntil = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(700);
            while (DateTimeOffset.UtcNow < watchUntil)
            {
                a.Poll();
                b.Poll();
                Assert.True(PracticeRoom.ContainsInterior(a.OwnTile), "a practicing partner must not be dragged into a run.");
                Assert.False(BossArena.ContainsInterior(a.OwnTile), "a practicing partner must not be teleported to the arena.");
                Assert.NotEqual(RunPhase.Active, a.LastRunPhase);
                Assert.NotEqual(RunPhase.Active, b.LastRunPhase);
                Assert.False(a.LastRunSelfReady, "A is not on any run roster.");
                Assert.False(b.LastRunSelfReady, "B's ready was refused (partner practicing).");
                Assert.False(a.SawDespawn(dummyId), "the dummy must not be orphaned/despawned while A still practices.");
                await Task.Delay(20);
            }

            // The dummy is still OWNED by the room, not leaked: when A finally leaves, it despawns cleanly.
            a.SendChat("/practice off");
            await WaitUntilAsync(() => !PracticeRoom.ContainsInterior(a.OwnTile) && a.SawDespawn(dummyId), a, b);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // Acceptance: the dummy is non-aggressive — it never targets/attacks a player standing next to it. The solo player
    // walks straight at the dummy; for a solid window the dummy must NEVER leave its spawn tile (a chasing brain would
    // close the gap) and the player must take NO damage (never dies, stays full HP). Proves both the stationary brain
    // (ignores aggro) and attackDamage 0.
    [Fact]
    public async Task DummyIsNonAggressive_NeverChasesOrDamagesAnApproachingPlayer()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: []);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var solo = new PracticeClient("Solo");
            solo.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => solo.IsLoggedIn && solo.OwnNetworkId != 0, solo);

            solo.SendChat("/practice");
            await WaitUntilAsync(
                () => PracticeRoom.ContainsInterior(solo.OwnTile) && solo.DummyNetworkId != 0 && solo.DummySeenAlive, solo);
            var dummyId = solo.DummyNetworkId;

            // The dummy sits ~10 tiles from the issuer entry tile — walk straight at it. Direction is computed toward
            // the dummy's spawn point each tick (no hard-coded heading), so the player genuinely closes to point-blank
            // (monster-body collision stops it right beside the dummy) regardless of axis convention.
            var dummyPoint = WorldVector.FromTile(PracticeRoom.DummySpawnTile);
            var watchUntil = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1500);
            uint seq = 1;
            while (DateTimeOffset.UtcNow < watchUntil)
            {
                var toDummy = dummyPoint - solo.OwnPos;
                var len = toDummy.Length;
                var dir = len > 0.001d ? toDummy * (1d / len) : toDummy;
                solo.SendMoveIntent(seq++, (float)dir.X, (float)dir.Y, 0.05f);
                solo.Poll();

                Assert.False(solo.HasSystemLine("You died."), "the dummy must never kill the player.");
                Assert.False(solo.SawDespawn(dummyId), "the dummy must stay present through the approach.");
                Assert.Equal(PracticeRoom.DummySpawnTile, solo.DummyTile); // never chases → never leaves its spawn tile.
                await Task.Delay(20);
            }

            // The player really did get next to it (its aggro/attack window, had it any) — the approach was real, not
            // a no-op that left the player far away.
            var gap = (solo.DummyPos - solo.OwnPos).Length;
            Assert.True(gap < 3.0d, $"expected the player to have closed on the dummy, but the gap was {gap:F2}u.");
            Assert.False(solo.TookDamage, "the player must take no damage standing next to the dummy.");
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // Acceptance: /practice is refused during an active run. A solo player readies → the run goes Active (in the boss
    // arena); trying to /practice from there is refused and leaves them in the arena.
    [Fact]
    public async Task PracticeEnterIsRefusedDuringAnActiveRun()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: []);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var solo = new PracticeClient("Solo");
            solo.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => solo.IsLoggedIn && solo.OwnNetworkId != 0, solo);

            // Solo-ready → the run starts and teleports the player into the Sunderer arena.
            solo.SendChat("/ready");
            await WaitUntilAsync(() => solo.LastRunPhase == RunPhase.Active && BossArena.ContainsInterior(solo.OwnTile), solo);

            // /practice from within an active run is refused; the player stays in the arena (never in the practice room).
            solo.SendChat("/practice");
            await WaitUntilAsync(() => solo.HasSystemLine("You can't enter the practice room during a run."), solo);
            Assert.True(BossArena.ContainsInterior(solo.OwnTile), "a runner must not be teleported to the practice room.");
            Assert.False(PracticeRoom.ContainsInterior(solo.OwnTile));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // Acceptance: a practice occupant is NOT a run participant. While Practicer rehearses in the room, an UNRELATED solo
    // Runner readies → their run goes Active. Practicer must be untouched: still in the room (never pulled into the
    // arena) and their owner-scoped RunStatus never reports them as ready/in-it (SelfReady false).
    [Fact]
    public async Task APracticeOccupantIsNotPulledIntoAnUnrelatedActiveRun()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: []);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var practicer = new PracticeClient("Practicer");
            using var runner = new PracticeClient("Runner");
            practicer.Connect(port, options.ConnectionKey);
            runner.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => practicer.IsLoggedIn && practicer.OwnNetworkId != 0 && runner.IsLoggedIn && runner.OwnNetworkId != 0,
                practicer, runner);

            // Practicer enters the room (Lobby).
            practicer.SendChat("/practice");
            await WaitUntilAsync(() => PracticeRoom.ContainsInterior(practicer.OwnTile) && practicer.DummyNetworkId != 0, practicer, runner);

            // The unpaired Runner readies → a solo run starts (Active) and teleports the RUNNER into the arena.
            runner.SendChat("/ready");
            await WaitUntilAsync(
                () => runner.LastRunPhase == RunPhase.Active && BossArena.ContainsInterior(runner.OwnTile), practicer, runner);

            // Practicer is untouched by that run: still in the practice room, never on its roster (SelfReady false).
            var watchUntil = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(500);
            while (DateTimeOffset.UtcNow < watchUntil)
            {
                practicer.Poll();
                runner.Poll();
                Assert.True(PracticeRoom.ContainsInterior(practicer.OwnTile), "a practice occupant must not be pulled into a run.");
                Assert.False(BossArena.ContainsInterior(practicer.OwnTile));
                Assert.False(practicer.LastRunSelfReady, "a practice occupant is not a run participant.");
                await Task.Delay(20);
            }
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // Mirrors RunLoopSessionIntegrationTests.CreateOptions — the AUTHORED 384x384 map (arena + practice room stamped).
    private static ServerOptions CreateOptions(int port, string connectionString, string[] admins)
    {
        return new ServerOptions(
            port,
            TickRate,
            "practice-room-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            AuthoredMaps.TownAndFloor1Width,
            AuthoredMaps.TownAndFloor1Height,
            140,
            15,
            30f,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(admins, StringComparer.OrdinalIgnoreCase))
        {
            GenVersion = TerrainGenerator.AuthoredGenVersion,
        };
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params PracticeClient[] clients)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
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

        throw new TimeoutException("Timed out waiting for practice-room integration condition.");
    }

    // A minimal loopback client for the practice room: sends chat verbs + move intents, and tracks exactly the
    // observable surface these symptoms need — server system lines, the owner-scoped RunStatus, pair state, its own
    // tile/position/HP, and the practice dummy's spawn/position/despawn. Modelled on RunLoopSessionIntegrationTests'
    // RunClient (its own tile decode) + ClearSpawnersIntegrationTests' entity tracking.
    private sealed class PracticeClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private readonly List<string> _systemLines = [];
        private readonly HashSet<uint> _despawned = [];
        private readonly object _gate = new();
        private NetPeer? _serverPeer;

        public PracticeClient(string name)
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

        public bool IsLoggedIn { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public TileCoord OwnTile { get; private set; } = TileGrid.DefaultSpawnTile;
        public WorldVector OwnPos { get; private set; }
        public RunPhase LastRunPhase { get; private set; } = RunPhase.Lobby;
        public bool LastRunSelfReady { get; private set; }
        public bool IsPaired { get; private set; }
        public bool TookDamage { get; private set; }

        // The practice dummy (the "Practice Dummy" monster). NetworkId 0 until its spawn is seen.
        public uint DummyNetworkId { get; private set; }
        public TileCoord DummyTile { get; private set; }
        public WorldVector DummyPos { get; private set; }
        public bool DummySeenAlive { get; private set; }
        public bool DummyDespawned => DummyNetworkId != 0 && SawDespawn(DummyNetworkId);

        public bool SawDespawn(uint id)
        {
            lock (_gate)
            {
                return _despawned.Contains(id);
            }
        }

        public void Connect(int port, string key)
        {
            _client.Start();
            _client.Connect("127.0.0.1", port, key);
        }

        public void Poll() => _client.PollEvents();

        public void SendChat(string text) =>
            Send(new ChatSendMessage(text), DeliveryMethod.ReliableOrdered);

        public void SendMoveIntent(uint seq, float dirX, float dirY, float dt) =>
            Send(new MoveIntentMessage(seq, dirX, dirY, dt), DeliveryMethod.Sequenced);

        public bool HasSystemLine(string substring)
        {
            lock (_gate)
            {
                return _systemLines.Exists(line => line.Contains(substring));
            }
        }

        public void Dispose() => _client.Stop();

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
                    case EntitySpawnMessage spawn:
                        if (spawn.DisplayName == _name && spawn.Kind == EntityKind.Player)
                        {
                            OwnNetworkId = spawn.NetworkId;
                            OwnTile = spawn.Tile;
                        }
                        else if (spawn.Kind == EntityKind.Monster && spawn.DisplayName == "Practice Dummy")
                        {
                            DummyNetworkId = spawn.NetworkId;
                            DummyTile = spawn.Tile;
                            DummySeenAlive = true;
                        }

                        break;
                    case EntityDespawnMessage despawn:
                        lock (_gate)
                        {
                            _despawned.Add(despawn.NetworkId);
                        }

                        break;
                    case ChatBroadcastMessage chat when chat.Sender == "server":
                        lock (_gate)
                        {
                            _systemLines.Add(chat.Text);
                        }

                        break;
                    case DamageEventMessage dmg when dmg.NetworkId == OwnNetworkId && dmg.Amount > 0:
                        TookDamage = true;
                        break;
                    case RunStatusMessage status:
                        LastRunPhase = status.Phase;
                        LastRunSelfReady = status.SelfReady;
                        break;
                    case PairStatusMessage pair:
                        IsPaired = pair.Paired;
                        break;
                    case WorldSnapshotMessage snapshot:
                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                        foreach (var entity in snapshot.Entities)
                        {
                            if (entity.NetworkId == OwnNetworkId)
                            {
                                OwnPos = entity.Position;
                                OwnTile = entity.Position.ToTileRounded();
                            }
                            else if (entity.NetworkId == DummyNetworkId && DummyNetworkId != 0)
                            {
                                DummyPos = entity.Position;
                                DummyTile = entity.Position.ToTileRounded();
                            }
                        }

                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        private void Send(IProtocolMessage message, DeliveryMethod deliveryMethod) =>
            _serverPeer?.Send(ProtocolCodec.Encode(message), deliveryMethod);
    }
}
