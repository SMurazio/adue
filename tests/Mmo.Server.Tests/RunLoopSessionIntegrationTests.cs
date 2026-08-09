using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

// ADUE P1 RUN LOOP (todo/S-adue-run-symptom-tests.md — review M4 + the H1 gap): SESSION-LEVEL symptom tests. The
// headless RunEngineTests drive the engine on a bare WorldState and can only prove the PROXY flag (IsRunParticipant).
// The actual GameServer glue — RespawnPlayers consulting that flag AND the body's arena location to SKIP a dead run
// member's town respawn, returnPlayer's session.MarkAlive() un-stick at the run's end, and HandleRunReadyRequest's H1
// "can't ready while down" gate — only exists once a real ClientSession is in play. These drive a live GameServer over
// the loopback (the ClearSpawners/DuplicateLogin/AdminTuning integration tests are the precedent) and observe the
// symptoms through the wire: server system lines, RunStatus phase, and the player's own replicated tile.
//
// Determinism: death is inflicted with the admin /slam verb (a self-centred damage telegraph — no boss RNG), and the
// player-respawn delay is set LIVE via admin tuning so each test controls its own dead-window. /slam uses a radius of 1
// so it hits only the caster's tile, never the partner two tiles away on the other entry tile.
public sealed class RunLoopSessionIntegrationTests
{
    private const int TickRate = 20;

    // H1 gate (fixed post-review): readying UP while down is refused; un-readying while down is allowed. Deterministic
    // — the respawn delay is pinned high so the player stays dead across both presses (no respawn race).
    [Fact]
    public async Task ReadyingWhileDownIsRefused_ButUnreadyingWhileDownIsAllowed()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: ["Solo"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var solo = new RunClient("Solo");
            solo.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => solo.IsLoggedIn && solo.OwnNetworkId != 0, solo);

            // Pin the respawn delay high so the player stays DOWN for the whole test (no respawn mid-assertion).
            solo.SendAdminSetTuning("player.respawnMs", 60000d);
            await PollForAsync(TimeSpan.FromMilliseconds(150), solo);

            // Kill the player in town (not in a run) with a self-slam.
            solo.SendChat("/slam 1 100 9999");
            await WaitUntilAsync(() => solo.HasSystemLine("You died."), solo);

            // Readying UP while down is refused — and no run starts.
            solo.SendChat("/ready");
            await WaitUntilAsync(() => solo.HasSystemLine("You can't ready while down."), solo);
            Assert.NotEqual(RunPhase.Active, solo.LastRunPhase);
            Assert.False(solo.LastRunSelfReady);

            // Un-readying while down is allowed (it only clears a flag).
            solo.SendChat("/ready off");
            await WaitUntilAsync(() => solo.HasSystemLine("You are no longer ready."), solo);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // The core M4 symptom: a dead run member's body is NOT town-respawned mid-run (RespawnPlayers skips it while it is
    // down inside the arena), and at the run's end returnPlayer revives + returns it (session.MarkAlive un-stick), after
    // which the player can ready again. The respawn delay is pinned LOW so a BROKEN skip would teleport the body to town
    // within a fraction of a second — the test proves the body instead STAYS in the arena until the run ends.
    [Fact]
    public async Task DeadRunParticipantStaysDownInArenaUntilRunEnds_ThenIsRevivedAndCanReadyAgain()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: ["Alpha", "Bravo"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var a = new RunClient("Alpha");
            using var b = new RunClient("Bravo");
            a.Connect(port, options.ConnectionKey);
            b.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => a.IsLoggedIn && a.OwnNetworkId != 0 && b.IsLoggedIn && b.OwnNetworkId != 0, a, b);

            // A short respawn delay: if the mid-run skip were broken, a dead body would jump to town almost at once.
            a.SendAdminSetTuning("player.respawnMs", 300d);
            await PollForAsync(TimeSpan.FromMilliseconds(150), a, b);

            // Pair up, then both ready → the duo run starts and both are teleported into the arena.
            a.SendChat("/pair Bravo");
            await WaitUntilAsync(() => a.IsPaired && b.IsPaired, a, b);

            a.SendChat("/ready");
            b.SendChat("/ready");
            await WaitUntilAsync(
                () => a.LastRunPhase == RunPhase.Active && b.LastRunPhase == RunPhase.Active, a, b);
            await WaitUntilAsync(() => BossArena.ContainsInterior(a.OwnTile) && BossArena.ContainsInterior(b.OwnTile), a, b);

            // Wait for the boss to actually SPAWN (the ~3 s countdown elapses) before inflicting any death. This is the
            // only realistic wipe path: before the boss is up there is NO damage source in real play, and the encounter
            // reads a full-party death as a WIPE only in StepActive (StepCountdown handles just empty→abandon). Killing
            // the pair during the pre-boss countdown is an unreachable state — a test artifact.
            await WaitUntilAsync(() => a.HasSystemLine("THE SUNDERER awakens"), a, b);

            // Alpha dies INSIDE the arena, mid-fight. The in-arena death line proves IsRunParticipant + arena-location.
            a.SendChat("/slam 1 100 9999");
            await WaitUntilAsync(() => a.HasSystemLine("You are down. No respawn until the run ends."), a, b);

            // SKIP HELD: for well over the (300 ms) respawn delay, Alpha's body must STAY in the arena, never get the
            // town-respawn line, and the run must stay Active (Bravo is still up). Kept to ~1 s — long enough to be >3x
            // the respawn delay (a broken skip would fire within ~300 ms) but short enough that the freshly-spawned boss
            // (10 tiles away, chasing) can't close on Bravo and end the run before we do it deliberately below.
            var watchUntil = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1000);
            while (DateTimeOffset.UtcNow < watchUntil)
            {
                a.Poll();
                b.Poll();
                Assert.True(BossArena.ContainsInterior(a.OwnTile), "a downed run member must not be teleported to town mid-run.");
                Assert.False(a.HasSystemLine("You respawned."), "a downed run member must not town-respawn mid-run.");
                Assert.Equal(RunPhase.Active, a.LastRunPhase);
                await Task.Delay(20);
            }

            // End the run: Bravo dies too → the party has fallen in-arena → WIPE. Both get the run-over line.
            b.SendChat("/slam 1 100 9999");
            await WaitUntilAsync(() => a.HasSystemLine("RUN OVER — the Sunderer still stands."), a, b);

            // REVIVED + RETURNED: Alpha's body is settled by returnPlayer — teleported OUT of the arena (back to town).
            await WaitUntilAsync(() => !BossArena.ContainsInterior(a.OwnTile), a, b);

            // MarkAlive un-stick: Alpha can ready again — the ready is NOT refused as "down" (still paired → waits for
            // the partner). If MarkAlive had not run, this would come back "You can't ready while down."
            a.ClearSystemLines();
            a.SendChat("/ready");
            await WaitUntilAsync(() => a.HasSystemLine("Ready. Waiting for your partner."), a, b);
            Assert.False(a.HasSystemLine("You can't ready while down."));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // The run's boss room IS the authored Sunderer arena (BossArena tiles 356-379), which only exists on the AUTHORED
    // 384x384 town+floor-1 map — so this harness MUST generate that map (GenVersion = AuthoredGenVersion at the
    // authored dims), or the arena is never stamped and SpawnMonsterCore fails to place the boss at BossSpawnTile
    // (players still "teleport in" because ContainsInterior is a pure coordinate check, but the fight never starts).
    // Mirrors InteractHarvestIntegrationTests / TelegraphWireIntegrationTests, which stand up the same authored map.
    private static ServerOptions CreateOptions(int port, string connectionString, string[] admins)
    {
        return new ServerOptions(
            port,
            TickRate,
            "run-loop-session-test",
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

    private static async Task WaitUntilAsync(Func<bool> condition, params RunClient[] clients)
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

        throw new TimeoutException("Timed out waiting for run-loop session integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params RunClient[] clients)
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

    // A minimal loopback client for the run loop: sends chat verbs + admin tuning, and tracks exactly the observable
    // surface these symptoms need — server system lines, the owner-scoped RunStatus, the pair state, and its own tile
    // (decoded from snapshots, so "in the arena vs back in town" is directly assertable). Modelled on AdminTuningTests'
    // TuningClient.
    private sealed class RunClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private readonly List<string> _systemLines = [];
        private readonly object _gate = new();
        private NetPeer? _serverPeer;

        public RunClient(string name)
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
        public RunPhase LastRunPhase { get; private set; } = RunPhase.Lobby;
        public bool LastRunSelfReady { get; private set; }
        public bool IsPaired { get; private set; }

        public void Connect(int port, string key)
        {
            _client.Start();
            _client.Connect("127.0.0.1", port, key);
        }

        public void Poll() => _client.PollEvents();

        public void SendChat(string text) =>
            Send(new ChatSendMessage(text), DeliveryMethod.ReliableOrdered);

        public void SendAdminSetTuning(string key, double value) =>
            Send(new AdminSetTuningMessage(key, value), DeliveryMethod.ReliableOrdered);

        public bool HasSystemLine(string substring)
        {
            lock (_gate)
            {
                return _systemLines.Exists(line => line.Contains(substring));
            }
        }

        public void ClearSystemLines()
        {
            lock (_gate)
            {
                _systemLines.Clear();
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
                        if (spawn.DisplayName == _name)
                        {
                            OwnNetworkId = spawn.NetworkId;
                            OwnTile = spawn.Tile;
                        }

                        break;
                    case ChatBroadcastMessage chat when chat.Sender == "server":
                        lock (_gate)
                        {
                            _systemLines.Add(chat.Text);
                        }

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
                                OwnTile = entity.Position.ToTileRounded();
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
