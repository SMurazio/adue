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

public sealed class AoiIntegrationTests
{
    [Fact]
    public async Task ClientReceivesSpawnAndDespawnWhenEntityEntersAndLeavesAoi()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = new ServerOptions(
            port,
            20,
            "integration-test",
            DatabaseProvider.Sqlite,
            database.ConnectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            50,
            15,
            5,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var outsideClient = new IntegrationClient("Outside");
            outsideClient.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => outsideClient.IsLoggedIn && outsideClient.OwnNetworkId != 0, outsideClient);

            var spawnTile = outsideClient.OwnTile;
            var outsideX = spawnTile.X + 7;
            await StepUntilAsync(outsideClient, Direction8.E, () => outsideClient.OwnTile.X >= outsideX);

            using var observer = new IntegrationClient("Observer");
            observer.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => observer.IsLoggedIn && observer.OwnNetworkId != 0, observer, outsideClient);
            Assert.Equal(spawnTile, observer.OwnTile);

            var outsideNetworkId = outsideClient.OwnNetworkId;
            observer.ClearMessages();
            await WaitUntilAsync(
                () => observer.Messages.OfType<WorldSnapshotMessage>().Any(),
                observer,
                outsideClient);
            await PollForAsync(TimeSpan.FromMilliseconds(250), observer, outsideClient);

            Assert.DoesNotContain(
                observer.Messages.OfType<EntitySpawnMessage>(),
                message => message.NetworkId == outsideNetworkId);
            Assert.DoesNotContain(
                observer.Messages.OfType<WorldSnapshotMessage>().SelectMany(message => message.Entities),
                entity => entity.NetworkId == outsideNetworkId);

            // First entry into the observer's AOI: spawn must carry the real display name.
            observer.ClearMessages();
            await StepUntilAsync(outsideClient, Direction8.W, () => outsideClient.OwnTile.X <= observer.OwnTile.X + 4, observer);
            await WaitUntilAsync(
                () => observer.Messages.OfType<EntitySpawnMessage>().Any(message => message.NetworkId == outsideNetworkId),
                observer,
                outsideClient);
            await WaitUntilAsync(
                () => observer.Messages
                    .OfType<WorldSnapshotMessage>()
                    .SelectMany(message => message.Entities)
                    .Any(entity => entity.NetworkId == outsideNetworkId),
                observer,
                outsideClient);

            var firstSpawn = observer.Messages
                .OfType<EntitySpawnMessage>()
                .First(message => message.NetworkId == outsideNetworkId);
            Assert.Equal(outsideClient.OwnDisplayName, firstSpawn.DisplayName);

            // Exit the observer's AOI: despawn must be sent.
            observer.ClearMessages();
            await StepUntilAsync(outsideClient, Direction8.E, () => outsideClient.OwnTile.X >= outsideX, observer);
            await WaitUntilAsync(
                () => observer.Messages.OfType<EntityDespawnMessage>().Any(message => message.NetworkId == outsideNetworkId),
                observer,
                outsideClient);

            // Re-entry: a fresh named EntitySpawn must arrive again (regression for S34).
            observer.ClearMessages();
            await StepUntilAsync(outsideClient, Direction8.W, () => outsideClient.OwnTile.X <= observer.OwnTile.X + 4, observer);
            await WaitUntilAsync(
                () => observer.Messages.OfType<EntitySpawnMessage>().Any(message => message.NetworkId == outsideNetworkId),
                observer,
                outsideClient);

            var reentrySpawn = observer.Messages
                .OfType<EntitySpawnMessage>()
                .First(message => message.NetworkId == outsideNetworkId);
            Assert.Equal(outsideClient.OwnDisplayName, reentrySpawn.DisplayName);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task StaticNonPlayerEntityReplicatesThroughAoi()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        // NODE-FIELD N2: scattered harvestables are no longer entities, so the static non-session-owned
        // subject is now an AUTHORED PROP (the town's House/Portal Resource-kind transients, genVersion 2
        // world) — the observer spawns on the plaza (Authored distribution) with the houses well inside the
        // 64u interest radius. Verifies such an entity both spawns (with kind + empty character id) and
        // appears in the periodic world snapshot. (Despawn-on-AOI-exit is covered by
        // ClientReceivesSpawnAndDespawnWhenEntityEntersAndLeavesAoi.)
        var options = new ServerOptions(
            port,
            20,
            "integration-test",
            DatabaseProvider.Sqlite,
            database.ConnectionString,
            TestSqliteDatabase.MigrationsPath,
            384,
            384,
            50,
            15,
            64,
            150,
            SpawnDistribution.Authored,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)) with
        {
            GenVersion = 2,
        };
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("Observer");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            await WaitUntilAsync(
                () => client.Messages.OfType<EntitySpawnMessage>().Any(message => message.Kind == EntityKind.Resource),
                client);

            var nodeSpawn = client.Messages
                .OfType<EntitySpawnMessage>()
                .First(message => message.Kind == EntityKind.Resource);
            Assert.Equal(EntityKind.Resource, nodeSpawn.Kind);
            Assert.Equal(Guid.Empty, nodeSpawn.CharacterId);

            await WaitUntilAsync(
                () => client.Messages
                    .OfType<WorldSnapshotMessage>()
                    .SelectMany(message => message.Entities)
                    .Any(entity => entity.NetworkId == nodeSpawn.NetworkId),
                client);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task ServerSendsZoneInfoAfterLogin()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = new ServerOptions(
            port,
            20,
            "integration-test",
            DatabaseProvider.Sqlite,
            database.ConnectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            50,
            15,
            30,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("Observer");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.Messages.OfType<ZoneInfoMessage>().Any(), client);

            var zone = client.Messages.OfType<ZoneInfoMessage>().Single();
            Assert.Equal(Zone.DefaultId, zone.ZoneId);
            Assert.Equal(64, zone.Width);
            Assert.Equal(64, zone.Height);
            // ZoneInfo no longer ships the tile payload — it ships the seed descriptor. Regenerate the
            // map locally via the shared generator and confirm both the content and the server's hash.
            // AUTHORED-MAP M3 (M1 review F3): compare the LAYOUT's ContentHash, never a re-hash of the
            // blocked list — on an authored genVersion the layout hash also covers categories/spawns/
            // markers, so a blocked-only re-hash would false-fail (and would hide category-only drift).
            var layout = TerrainGenerator.GenerateLayout(zone.Width, zone.Height, zone.Seed, zone.GenVersion);
            Assert.Contains(new TileCoord(16, 8), layout.BlockedTiles);
            Assert.DoesNotContain(TileGrid.DefaultSpawnTile, layout.BlockedTiles);
            Assert.Equal(layout.ContentHash, zone.ContentHash);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // S46 convergence-under-loss MUST-HAVE: with the acked baseline and NO periodic full heartbeat, an
    // observer that DROPS every snapshot (ignores it: neither applies nor acks) while a mover keeps
    // stepping must NOT desync permanently. Because the dropped snapshots are never acked, the server's
    // acked baseline for the mover never advances, so the mover stays in the snapshot payload every tick;
    // when the observer resumes, the next snapshot it accepts carries the mover's CURRENT absolute tile
    // and its reconstruction converges EXACTLY to the server's. A baseline bug that marked the mover
    // "sent" after the first (dropped) snapshot would drop it from the payload and the observer would stay
    // permanently stale — this test would then fail. Heartbeats are gone, so only the acked baseline heals.
    [Fact]
    public async Task ObserverConvergesAfterDroppingSnapshotsWhileMoverSteps()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = new ServerOptions(
            port,
            20,
            "integration-test",
            DatabaseProvider.Sqlite,
            database.ConnectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            50,
            15,
            30,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var mover = new IntegrationClient("Mover");
            using var observer = new IntegrationClient("Observer");
            mover.Connect(port, options.ConnectionKey);
            observer.Connect(port, options.ConnectionKey);

            await WaitUntilAsync(
                () => mover.IsLoggedIn && mover.OwnNetworkId != 0
                    && observer.IsLoggedIn && observer.OwnNetworkId != 0,
                mover,
                observer);

            // Both spawn at the clustered spawn tile, so the mover is already inside the observer's AOI.
            await WaitUntilAsync(
                () => observer.ReconstructedTileOf(mover.OwnNetworkId) is not null,
                mover,
                observer);

            var staleTile = observer.ReconstructedTileOf(mover.OwnNetworkId);

            // Drop ALL snapshots on the observer (no apply, no ack), then move the mover several tiles. The
            // server never gets an ack, so its acked baseline for the mover cannot advance.
            observer.DropSnapshots = true;
            var startX = mover.OwnTile.X;
            await StepUntilAsync(mover, Direction8.E, () => mover.OwnTile.X >= startX + 5, observer);

            // The observer's view is now stale (it ignored every snapshot during the move).
            Assert.Equal(staleTile, observer.ReconstructedTileOf(mover.OwnNetworkId));
            Assert.NotEqual(mover.OwnTile, observer.ReconstructedTileOf(mover.OwnNetworkId));

            // Resume processing. The next accepted snapshot must carry the mover (still unacked on the
            // server) at its current absolute tile, so the reconstruction converges exactly.
            observer.DropSnapshots = false;
            await WaitUntilAsync(
                () => observer.ReconstructedTileOf(mover.OwnNetworkId) == mover.OwnTile,
                mover,
                observer);

            Assert.Equal(mover.OwnTile, observer.ReconstructedTileOf(mover.OwnNetworkId));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    // CONTINUOUS remote-walk fluidity (per-tick continuous replication): a MOVING player must be force-included in a
    // viewer's snapshots ~EVERY tick (dense ~50ms samples the viewer interpolates smoothly), NOT only on a rounded-tile
    // crossing (~250ms at 4u/s) — the tile-stepped-era gate that left a remote viewer extrapolating across the gap and
    // stuttering (the live symptom). We drive a mover continuously inside an observer's AOI and assert the observer
    // receives the mover in the large majority of its snapshots during the move. The OLD tile-gated path would include
    // the mover in only ~1 snapshot in 5 (and suppress the rest as empty keep-alives); this test fails under that path.
    [Fact]
    public async Task MovingEntityIsForceIncludedForViewerEveryTick_NotJustOnTileCrossings()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = new ServerOptions(
            port,
            20,
            "integration-test",
            DatabaseProvider.Sqlite,
            database.ConnectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            // StepCooldownMs = 250 = the LIVE default ⇒ 4 u/s ⇒ a rounded-tile crossing only ~every 5 ticks. This is
            // the SPARSE-crossing regime the bug lives in: under the old tile-gated predicate the mover's StateRevision
            // bumps ~1/5 ticks so it was included in only ~1/5 of the observer's snapshots. (At a fast cooldown like 50
            // the mover crosses a tile EVERY tick, which would mask the bug — the entity would be included every tick
            // via !HasAckedCurrentRevision even under the old predicate, so the test would pass on the broken code.)
            250,
            15,
            30, // interest radius — wide enough that the few-tile walk stays inside the observer's AOI
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var mover = new IntegrationClient("Mover");
            using var observer = new IntegrationClient("Observer");
            mover.Connect(port, options.ConnectionKey);
            observer.Connect(port, options.ConnectionKey);

            await WaitUntilAsync(
                () => mover.IsLoggedIn && mover.OwnNetworkId != 0 && observer.IsLoggedIn && observer.OwnNetworkId != 0,
                mover,
                observer);

            // Both spawn at the clustered spawn tile, so the mover starts inside the observer's AOI.
            await WaitUntilAsync(() => observer.ReconstructedTileOf(mover.OwnNetworkId) is not null, mover, observer);

            var moverId = mover.OwnNetworkId;
            observer.ClearMessages();

            // Drive the mover continuously EAST at ~tick cadence (a MoveIntent every ~50ms) for ~1.2s. While moving the
            // mover's Velocity != 0, so the server must force-include it for the observer every tick.
            var stopAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1200);
            while (DateTimeOffset.UtcNow < stopAt)
            {
                mover.SendMove(Direction8.E);
                await PollForAsync(TimeSpan.FromMilliseconds(50), mover, observer);
            }

            mover.StopMove();
            await PollForAsync(TimeSpan.FromMilliseconds(100), mover, observer);

            var snapshots = observer.Messages.OfType<WorldSnapshotMessage>().ToList();
            var withMover = snapshots.Count(s => s.Entities.Any(e => e.NetworkId == moverId));

            // ~1.2s at 20Hz ≈ 24 ticks ⇒ many snapshots; the mover appears in the LARGE MAJORITY (per-tick continuous
            // replication). The old tile-gated path would yield ≈1/5 inclusion (a fresh sample only per tile crossing).
            Assert.True(snapshots.Count >= 12, $"expected many snapshots during the move, got {snapshots.Count}");
            Assert.True(
                withMover >= snapshots.Count * 0.7,
                $"mover replicated in only {withMover}/{snapshots.Count} snapshots — not per-tick (tile-gated regression?)");
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params IntegrationClient[] clients)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
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

        throw new TimeoutException("Timed out waiting for integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params IntegrationClient[] clients)
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

    private static async Task StepUntilAsync(IntegrationClient mover, Direction8 direction, Func<bool> condition, params IntegrationClient[] observers)
    {
        var clients = observers.Prepend(mover).Distinct().ToArray();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            mover.SendMove(direction);
            await PollForAsync(TimeSpan.FromMilliseconds(75), clients);
            if (condition())
            {
                mover.StopMove();
                await PollForAsync(TimeSpan.FromMilliseconds(75), clients);
                return;
            }
        }

        throw new TimeoutException("Timed out waiting for step movement condition.");
    }

    private sealed class IntegrationClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private uint _moveSequence;

        public IntegrationClient(string name)
        {
            _name = name;
            _client = new NetManager(_listener)
            {
                AutoRecycle = false
            };

            _listener.PeerConnectedEvent += peer =>
            {
                _serverPeer = peer;
                Send(new ClientHelloMessage(_name), DeliveryMethod.ReliableOrdered);
                Send(new LoginRequestMessage(_name, _name), DeliveryMethod.ReliableOrdered);
            };
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        // Client-side reconstructed view of every entity it currently believes is in its AOI, by network
        // id → last-known tile. Built exactly like the real client: EntitySpawn inserts, EntityDespawn
        // removes, snapshots apply absolute tiles, and an isComplete snapshot reconciles (prunes anything
        // not in the snapshot). The convergence test asserts this converges to the server's truth.
        private readonly Dictionary<uint, TileCoord> _reconstructed = [];

        public List<IProtocolMessage> Messages { get; } = [];
        public bool IsLoggedIn { get; private set; }
        public string OwnDisplayName => _name;
        public uint OwnNetworkId { get; private set; }
        public TileCoord OwnTile { get; private set; } = TileGrid.DefaultSpawnTile;

        // When true, the client ignores every snapshot entirely (no apply, no ack) — simulating snapshot
        // loss so the server's acked baseline cannot advance (the self-heal path under test).
        public bool DropSnapshots { get; set; }

        public TileCoord? ReconstructedTileOf(uint networkId)
        {
            return _reconstructed.TryGetValue(networkId, out var tile) ? tile : null;
        }

        public void Connect(int port, string key)
        {
            _client.Start();
            _client.Connect("127.0.0.1", port, key);
        }

        public void Poll()
        {
            _client.PollEvents();
        }

        // CONTINUOUS MIGRATION (Phase 3, v36): per-input continuous MoveIntent — the held direction's UNIT vector + a
        // nominal dt. The server integrates each fresh input by its dt; repeated SendMove calls (the StepUntil loop)
        // walk the avatar tile-by-tile as real time refills the anti-speedhack budget. StopMove sends a (0,0) input.
        public void SendMove(Direction8 direction)
        {
            var dir = direction.ToUnitVector();
            Send(new MoveIntentMessage(++_moveSequence, (float)dir.X, (float)dir.Y, 1f / 20f), DeliveryMethod.Unreliable);
        }

        public void StopMove()
        {
            Send(new MoveIntentMessage(++_moveSequence, 0f, 0f, 1f / 20f), DeliveryMethod.Unreliable);
        }

        public void ClearMessages()
        {
            Messages.Clear();
        }

        public void Dispose()
        {
            _client.Stop();
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                var message = ProtocolCodec.Decode(reader.GetRemainingBytes());
                Messages.Add(message);
                switch (message)
                {
                    case LoginResultMessage login:
                        IsLoggedIn = login.Accepted;
                        break;
                    case EntitySpawnMessage spawn:
                        _reconstructed[spawn.NetworkId] = spawn.Tile;
                        if (spawn.DisplayName == _name)
                        {
                            OwnNetworkId = spawn.NetworkId;
                            OwnTile = spawn.Tile;
                        }

                        break;
                    case EntityDespawnMessage despawn:
                        _reconstructed.Remove(despawn.NetworkId);
                        break;
                    case WorldSnapshotMessage snapshot:
                        ApplySnapshot(snapshot);
                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        private void ApplySnapshot(WorldSnapshotMessage snapshot)
        {
            if (DropSnapshots)
            {
                return;
            }

            Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);

            foreach (var entity in snapshot.Entities)
            {
                _reconstructed[entity.NetworkId] = entity.Position.ToTileRounded();
                if (entity.NetworkId == OwnNetworkId)
                {
                    OwnTile = entity.Position.ToTileRounded();
                }
            }

            // A complete snapshot (single-chunk in this test) carries the full visible set, so prune any
            // reconstructed entity it did not include — mirrors the real client's reconciliation.
            if (snapshot.IsComplete && snapshot.ChunkCount <= 1)
            {
                var present = snapshot.Entities.Select(static e => e.NetworkId).ToHashSet();
                foreach (var networkId in _reconstructed.Keys.Where(id => !present.Contains(id)).ToArray())
                {
                    _reconstructed.Remove(networkId);
                }
            }
        }

        private void Send(IProtocolMessage message, DeliveryMethod deliveryMethod)
        {
            _serverPeer?.Send(ProtocolCodec.Encode(message), deliveryMethod);
        }
    }
}
