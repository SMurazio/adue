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
    private const string PlaceholderEntityName = "Ancient Marker";

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

            observer.ClearMessages();
            await StepUntilAsync(outsideClient, Direction8.E, () => outsideClient.OwnTile.X >= outsideX, observer);
            await WaitUntilAsync(
                () => observer.Messages.OfType<EntityDespawnMessage>().Any(message => message.NetworkId == outsideNetworkId),
                observer,
                outsideClient);
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
            2,
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
            var spawnTile = client.OwnTile;
            await WaitUntilAsync(
                () => client.Messages.OfType<EntitySpawnMessage>().Any(message => message.DisplayName == PlaceholderEntityName),
                client);

            var markerSpawn = client.Messages
                .OfType<EntitySpawnMessage>()
                .First(message => message.DisplayName == PlaceholderEntityName);
            Assert.Equal(EntityKind.Resource, markerSpawn.Kind);
            Assert.Equal(Guid.Empty, markerSpawn.CharacterId);

            await WaitUntilAsync(
                () => client.Messages
                    .OfType<WorldSnapshotMessage>()
                    .SelectMany(message => message.Entities)
                    .Any(entity => entity.NetworkId == markerSpawn.NetworkId),
                client);

            client.ClearMessages();
            await StepUntilAsync(client, Direction8.S, () => client.OwnTile.Y >= spawnTile.Y + 4);
            await WaitUntilAsync(
                () => client.Messages.OfType<EntityDespawnMessage>().Any(message => message.NetworkId == markerSpawn.NetworkId),
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
            Assert.Contains(new TileCoord(16, 8), zone.BlockedTiles);
            Assert.DoesNotContain(TileGrid.DefaultSpawnTile, zone.BlockedTiles);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task FullSnapshotHeartbeatIsNotStarvedByPartialSnapshots()
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
            using var moverA = new IntegrationClient("MoverA");
            using var moverB = new IntegrationClient("MoverB");
            using var observer = new IntegrationClient("Observer");
            moverA.Connect(port, options.ConnectionKey);
            moverB.Connect(port, options.ConnectionKey);
            observer.Connect(port, options.ConnectionKey);

            await WaitUntilAsync(
                () => moverA.IsLoggedIn && moverA.OwnNetworkId != 0
                    && moverB.IsLoggedIn && moverB.OwnNetworkId != 0
                    && observer.IsLoggedIn && observer.OwnNetworkId != 0,
                moverA,
                moverB,
                observer);
            await WaitUntilAsync(
                () => observer.Messages.OfType<WorldSnapshotMessage>().Any(message => message.IsComplete),
                moverA,
                moverB,
                observer);

            observer.ClearMessages();

            var sawFullHeartbeat = await PumpMovementUntilFullSnapshotAsync(
                TimeSpan.FromMilliseconds(1600),
                moverA,
                moverB,
                observer);

            Assert.True(sawFullHeartbeat);
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
                return;
            }
        }

        throw new TimeoutException("Timed out waiting for step movement condition.");
    }

    private static async Task<bool> PumpMovementUntilFullSnapshotAsync(TimeSpan timeout, IntegrationClient firstMover, IntegrationClient secondMover, IntegrationClient observer)
    {
        var clients = new[] { firstMover, secondMover, observer };
        var deadline = DateTimeOffset.UtcNow + timeout;
        var nextMoveAt = DateTimeOffset.UtcNow;
        var direction = Direction8.E;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (DateTimeOffset.UtcNow >= nextMoveAt)
            {
                firstMover.SendMove(direction);
                secondMover.SendMove(direction);
                direction = direction == Direction8.E ? Direction8.W : Direction8.E;
                nextMoveAt += TimeSpan.FromMilliseconds(60);
            }

            foreach (var client in clients)
            {
                client.Poll();
            }

            if (observer.Messages.OfType<WorldSnapshotMessage>().Any(message => message.IsComplete))
            {
                return true;
            }

            await Task.Delay(5);
        }

        return false;
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

        public List<IProtocolMessage> Messages { get; } = [];
        public bool IsLoggedIn { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public TileCoord OwnTile { get; private set; } = TileGrid.DefaultSpawnTile;

        public void Connect(int port, string key)
        {
            _client.Start();
            _client.Connect("127.0.0.1", port, key);
        }

        public void Poll()
        {
            _client.PollEvents();
        }

        public void SendMove(Direction8 direction)
        {
            Send(new MoveStepMessage(++_moveSequence, direction), DeliveryMethod.Sequenced);
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
                    case EntitySpawnMessage spawn when spawn.DisplayName == _name:
                        OwnNetworkId = spawn.NetworkId;
                        OwnTile = spawn.Tile;
                        break;
                    case WorldSnapshotMessage snapshot:
                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                        var own = snapshot.Entities.FirstOrDefault(entity => entity.NetworkId == OwnNetworkId);
                        if (own is not null)
                        {
                            OwnTile = own.Tile;
                        }

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
