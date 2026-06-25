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
        // Interest radius spans the whole 64² map so at least one of the scattered resource nodes (the
        // server's static, non-session-owned entities) lands in the observer's AOI regardless of where
        // the seeded scatter placed them. Verifies such an entity both spawns (with kind + empty
        // character id) and appears in the periodic world snapshot. (Despawn-on-AOI-exit is covered by
        // ClientReceivesSpawnAndDespawnWhenEntityEntersAndLeavesAoi.)
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
            64,
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
            var blocked = TerrainGenerator.Generate(zone.Width, zone.Height, zone.Seed, zone.GenVersion);
            Assert.Contains(new TileCoord(16, 8), blocked);
            Assert.DoesNotContain(TileGrid.DefaultSpawnTile, blocked);
            Assert.Equal(TerrainGenerator.ContentHash(blocked), zone.ContentHash);
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
