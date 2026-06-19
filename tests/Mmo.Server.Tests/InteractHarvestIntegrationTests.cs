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

// End-to-end coverage of the S38 gather loop against a live GameServer: a player adjacent to an
// Available resource node harvests it (item lands in the S37 inventory, node depletes and replicates as
// Depleted by AOI, then respawns), invalid interactions are rejected with a reason and no state change,
// the node-depleted bit never reaches a client that can't see the node, and a mid-session relogin
// (account takeover) keeps the harvested item.
public sealed class InteractHarvestIntegrationTests
{
    // "tree" is the first scattered node type (yields wood) and is placed at spawnTile + (0, 2).
    private const string TreeNodeName = "Tree";

    [Fact]
    public async Task AdjacentPlayerHarvestsNodeDepletesItAndRespawns()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("Harvester");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            var node = await WaitForNodeAsync(client);
            await StepAdjacentToAsync(client, node.Tile);

            client.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => client.InteractResults.Any(), client);

            var harvest = client.InteractResults.Last();
            Assert.True(harvest.Success);
            Assert.Equal("", harvest.Reason);

            await WaitUntilAsync(() => client.InventoryUpdates.Any(), client);
            var inventory = client.InventoryUpdates.Last();
            Assert.Contains(inventory.ChangedStacks, stack => stack.TemplateKey == "wood" && stack.Quantity == 1);

            // Node-depleted replicates by AOI: the observer (us) sees Depleted=true for this node.
            await WaitUntilAsync(() => client.LatestDepletedState(node.NetworkId) == true, client);

            // Respawn restores availability (tree respawns after 100 ticks; at 20Hz that's ~5s).
            await WaitUntilAsync(() => client.LatestDepletedState(node.NetworkId) == false, TimeSpan.FromSeconds(12), client);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task TooFarInteractIsRejectedWithoutDepletingNode()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("FarAway");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            var node = await WaitForNodeAsync(client);
            // Move away so we are NOT adjacent (spawn is +2 from the node; step further).
            await StepUntilAsync(client, Direction8.N, () => Math.Abs(client.OwnTile.Y - node.Tile.Y) > 1);

            client.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => client.InteractResults.Any(), client);

            var result = client.InteractResults.Last();
            Assert.False(result.Success);
            Assert.Equal("too_far", result.Reason);
            Assert.Empty(client.InventoryUpdates);
            Assert.NotEqual(true, client.LatestDepletedState(node.NetworkId));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task InteractingWithNonResourceIsRejected()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var actor = new IntegrationClient("Actor");
            using var target = new IntegrationClient("Target");
            actor.Connect(port, options.ConnectionKey);
            target.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => actor.IsLoggedIn && actor.OwnNetworkId != 0 && target.IsLoggedIn && target.OwnNetworkId != 0,
                actor,
                target);

            // Both spawn clustered at the same tile, so the actor sees the target player entity adjacent.
            await WaitUntilAsync(() => actor.KnownSpawns.Any(s => s.NetworkId == target.OwnNetworkId), actor, target);

            actor.SendInteract(target.OwnNetworkId);
            await WaitUntilAsync(() => actor.InteractResults.Any(), actor, target);

            var result = actor.InteractResults.Last();
            Assert.False(result.Success);
            Assert.Equal("not_resource", result.Reason);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task DepletedNodeRejectsSecondHarvest()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("DoubleHarvest");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            var node = await WaitForNodeAsync(client);
            await StepAdjacentToAsync(client, node.Tile);

            client.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => client.InteractResults.Any(r => r.Success), client);

            // Wait out the interact cooldown, then harvest again: the node is depleted.
            await PollForAsync(TimeSpan.FromMilliseconds(400), client);
            client.ClearInteractResults();
            client.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => client.InteractResults.Any(), client);

            var result = client.InteractResults.Last();
            Assert.False(result.Success);
            Assert.Equal("depleted", result.Reason);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task NodeDepletedStateIsNeverSerializedToAClientThatCannotSeeIt()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        // Small interest radius so a player who steps away genuinely loses sight of the node.
        var options = CreateOptions(port, database.ConnectionString, interestRadius: 4f);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var harvester = new IntegrationClient("Harvester");
            using var observer = new IntegrationClient("Observer");
            harvester.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => harvester.IsLoggedIn && harvester.OwnNetworkId != 0, harvester);

            var node = await WaitForNodeAsync(harvester);
            await StepAdjacentToAsync(harvester, node.Tile);

            // Observer logs in then walks far away so the node is outside its AOI.
            observer.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => observer.IsLoggedIn && observer.OwnNetworkId != 0, observer, harvester);
            await StepUntilAsync(observer, Direction8.E, () => observer.OwnTile.X - node.Tile.X > 8, harvester);

            observer.ClearMessages();
            harvester.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => harvester.InteractResults.Any(r => r.Success), harvester, observer);

            // Let snapshots flow; the observer must never receive an entity state for this node at all.
            await PollForAsync(TimeSpan.FromMilliseconds(600), observer, harvester);

            Assert.DoesNotContain(
                observer.Messages.OfType<WorldSnapshotMessage>().SelectMany(m => m.Entities),
                entity => entity.NetworkId == node.NetworkId);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task HarvestSurvivesSameAccountTakeoverRelogin()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var first = new IntegrationClient("Relogin");
            first.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => first.IsLoggedIn && first.OwnNetworkId != 0, first);

            var node = await WaitForNodeAsync(first);
            await StepAdjacentToAsync(first, node.Tile);

            first.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => first.InventoryUpdates.Any(), first);
            Assert.Contains(first.InventoryUpdates.Last().ChangedStacks, s => s.TemplateKey == "wood" && s.Quantity == 1);

            // Same account logs in again, taking over the session before any DB flush of the harvest.
            using var second = new IntegrationClient("Relogin");
            second.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => second.IsLoggedIn && second.OwnNetworkId != 0 && second.CharacterId == first.CharacterId,
                second,
                first);
            await WaitUntilAsync(() => first.IsDisconnected, second, first);

            // The taking-over session must carry the harvested wood. Wait for the same tree to respawn,
            // step adjacent, and harvest it again: the InventoryUpdate now reports a total of 2 wood,
            // proving the first session's not-yet-flushed harvest was handed off, not lost. (If the
            // takeover had reloaded the DB-stale inventory, this would report 1.)
            var node2 = await WaitForNodeAsync(second);
            await StepAdjacentToAsync(second, node2.Tile);
            await WaitUntilAsync(() => second.LatestDepletedState(node2.NetworkId) == false, TimeSpan.FromSeconds(12), second);
            second.SendInteract(node2.NetworkId);
            await WaitUntilAsync(() => second.InventoryUpdates.Any(u => u.ChangedStacks.Any(s => s.TemplateKey == "wood")), second);

            var woodTotal = second.InventoryUpdates
                .SelectMany(u => u.ChangedStacks)
                .Where(s => s.TemplateKey == "wood")
                .Select(s => s.Quantity)
                .Max();
            Assert.Equal(2, woodTotal);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static ServerOptions CreateOptions(int port, string connectionString, float interestRadius = 30f)
    {
        return new ServerOptions(
            port,
            20,
            "interact-harvest-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            50,
            15,
            interestRadius,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<(uint NetworkId, TileCoord Tile)> WaitForNodeAsync(IntegrationClient client)
    {
        await WaitUntilAsync(() => client.KnownSpawns.Any(s => s.DisplayName == TreeNodeName), client);
        var spawn = client.KnownSpawns.First(s => s.DisplayName == TreeNodeName);
        return (spawn.NetworkId, spawn.Tile);
    }

    // Steps the player to a tile that is Chebyshev-adjacent (<= 1) to the target.
    private static async Task StepAdjacentToAsync(IntegrationClient client, TileCoord target)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (IsAdjacent(client.OwnTile, target))
            {
                return;
            }

            var direction = DirectionToward(client.OwnTile, target);
            client.SendMove(direction);
            await PollForAsync(TimeSpan.FromMilliseconds(75), client);
        }

        throw new TimeoutException($"Timed out stepping adjacent to {target} (at {client.OwnTile}).");
    }

    private static bool IsAdjacent(TileCoord a, TileCoord b)
    {
        return Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;
    }

    private static Direction8 DirectionToward(TileCoord from, TileCoord to)
    {
        var dx = Math.Sign(to.X - from.X);
        var dy = Math.Sign(to.Y - from.Y);
        return (dx, dy) switch
        {
            (0, -1) => Direction8.N,
            (1, -1) => Direction8.NE,
            (1, 0) => Direction8.E,
            (1, 1) => Direction8.SE,
            (0, 1) => Direction8.S,
            (-1, 1) => Direction8.SW,
            (-1, 0) => Direction8.W,
            (-1, -1) => Direction8.NW,
            _ => Direction8.S
        };
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params IntegrationClient[] clients)
    {
        await WaitUntilAsync(condition, TimeSpan.FromSeconds(6), clients);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, params IntegrationClient[] clients)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
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

        throw new TimeoutException("Timed out waiting for interact-harvest integration condition.");
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
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
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

    private sealed class IntegrationClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private readonly Dictionary<uint, bool> _depletedByNetworkId = new();
        private NetPeer? _serverPeer;
        private uint _moveSequence;
        private bool _disposed;

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
            _listener.PeerDisconnectedEvent += (_, _) => IsDisconnected = true;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public List<IProtocolMessage> Messages { get; } = [];
        public List<EntitySpawnMessage> KnownSpawns { get; } = [];
        public List<InteractResultMessage> InteractResults { get; } = [];
        public List<InventoryUpdateMessage> InventoryUpdates { get; } = [];
        public bool IsLoggedIn { get; private set; }
        public bool IsDisconnected { get; private set; }
        public Guid CharacterId { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public TileCoord OwnTile { get; private set; } = TileGrid.DefaultSpawnTile;

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

        public void SendMove(Direction8 direction)
        {
            Send(new MoveStepMessage(++_moveSequence, direction), DeliveryMethod.Sequenced);
        }

        public void SendInteract(uint targetNetworkId)
        {
            Send(new InteractRequestMessage(targetNetworkId), DeliveryMethod.ReliableOrdered);
        }

        public void ClearMessages()
        {
            Messages.Clear();
        }

        public void ClearInteractResults()
        {
            InteractResults.Clear();
        }

        // null = no state seen yet for this node; true/false = last replicated depleted bit.
        public bool? LatestDepletedState(uint networkId)
        {
            return _depletedByNetworkId.TryGetValue(networkId, out var depleted) ? depleted : null;
        }

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
                Messages.Add(message);
                switch (message)
                {
                    case LoginResultMessage login:
                        IsLoggedIn = login.Accepted;
                        CharacterId = login.CharacterId;
                        break;
                    case EntitySpawnMessage spawn:
                        KnownSpawns.Add(spawn);
                        if (spawn.DisplayName == _name)
                        {
                            OwnNetworkId = spawn.NetworkId;
                            OwnTile = spawn.Tile;
                        }

                        break;
                    case InteractResultMessage result:
                        InteractResults.Add(result);
                        break;
                    case InventoryUpdateMessage update:
                        InventoryUpdates.Add(update);
                        break;
                    case WorldSnapshotMessage snapshot:
                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                        foreach (var entity in snapshot.Entities)
                        {
                            _depletedByNetworkId[entity.NetworkId] = entity.Depleted;
                            if (entity.NetworkId == OwnNetworkId)
                            {
                                OwnTile = entity.Tile;
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

        private void Send(IProtocolMessage message, DeliveryMethod deliveryMethod)
        {
            _serverPeer?.Send(ProtocolCodec.Encode(message), deliveryMethod);
        }
    }
}
