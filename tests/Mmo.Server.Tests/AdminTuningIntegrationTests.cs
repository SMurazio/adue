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

// S60 end-to-end: the admin-gated AdminSetTuning message changes a server tuning param LIVE, and the game
// loop (routed through the mutable ServerTuning holder) honours the new value on the next pass. Interest
// radius is the observable knob here: a small startup radius keeps a separated entity out of AOI; an admin
// raising aoi.interestRadius live brings it into view (a spawn arrives). A NON-admin's identical request is
// ignored — the entity stays invisible. Also covers an unknown key being ignored (no change, no error).
public sealed class AdminTuningIntegrationTests
{
    [Fact]
    public async Task AdminInterestRadiusChangeBringsDistantEntityIntoAoiLive()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        // Small startup radius (3 tiles) + admin "Watcher".
        var options = CreateOptions(port, database.ConnectionString, interestRadius: 3f, admins: ["Watcher"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var watcher = new TuningClient("Watcher");
            using var mover = new TuningClient("Mover");
            watcher.Connect(port, options.ConnectionKey);
            mover.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => watcher.IsLoggedIn && watcher.OwnNetworkId != 0 && mover.IsLoggedIn && mover.OwnNetworkId != 0,
                watcher,
                mover);

            // Move the mover well outside the tiny 3-tile radius so the watcher cannot see it.
            await StepUntilAsync(mover, Direction8.E, () => mover.OwnTile.X >= watcher.OwnTile.X + 8, watcher);
            await PollForAsync(TimeSpan.FromMilliseconds(300), watcher, mover);
            watcher.ClearMessages();
            await PollForAsync(TimeSpan.FromMilliseconds(200), watcher, mover);
            Assert.DoesNotContain(
                watcher.Messages.OfType<EntitySpawnMessage>(),
                m => m.NetworkId == mover.OwnNetworkId);

            // Admin raises the live interest radius so the separated mover now falls inside AOI: a spawn arrives.
            watcher.SendAdminSetTuning("aoi.interestRadius", 40d);
            await WaitUntilAsync(
                () => watcher.Messages.OfType<EntitySpawnMessage>().Any(m => m.NetworkId == mover.OwnNetworkId),
                watcher,
                mover);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task NonAdminInterestRadiusChangeIsIgnored()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        // No admins: everyone is a Player, so AdminSetTuning must be ignored.
        var options = CreateOptions(port, database.ConnectionString, interestRadius: 3f, admins: []);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var watcher = new TuningClient("Watcher");
            using var mover = new TuningClient("Mover");
            watcher.Connect(port, options.ConnectionKey);
            mover.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => watcher.IsLoggedIn && watcher.OwnNetworkId != 0 && mover.IsLoggedIn && mover.OwnNetworkId != 0,
                watcher,
                mover);

            await StepUntilAsync(mover, Direction8.E, () => mover.OwnTile.X >= watcher.OwnTile.X + 8, watcher);
            await PollForAsync(TimeSpan.FromMilliseconds(300), watcher, mover);
            watcher.ClearMessages();

            // Non-admin attempt to widen the radius. The server must ignore it; the mover stays invisible.
            watcher.SendAdminSetTuning("aoi.interestRadius", 40d);
            await PollForAsync(TimeSpan.FromMilliseconds(600), watcher, mover);
            Assert.DoesNotContain(
                watcher.Messages.OfType<EntitySpawnMessage>(),
                m => m.NetworkId == mover.OwnNetworkId);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task UnknownTuningKeyIsIgnoredAndDoesNotDisconnect()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, interestRadius: 30f, admins: ["Watcher"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var watcher = new TuningClient("Watcher");
            watcher.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => watcher.IsLoggedIn && watcher.OwnNetworkId != 0, watcher);

            watcher.SendAdminSetTuning("does.not.exist", 1234d);
            // Give the server time to process; the session must stay alive (no bad-packet disconnect) and the
            // watcher keeps receiving snapshots — an unknown key is a no-op, not a protocol error.
            await PollForAsync(TimeSpan.FromMilliseconds(400), watcher);
            watcher.ClearMessages();
            await WaitUntilAsync(() => watcher.Messages.OfType<WorldSnapshotMessage>().Any(), watcher);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static ServerOptions CreateOptions(int port, string connectionString, float interestRadius, string[] admins)
    {
        return new ServerOptions(
            port,
            20,
            "tuning-integration-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            140,
            15,
            interestRadius,
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

    private static async Task WaitUntilAsync(Func<bool> condition, params TuningClient[] clients)
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

        throw new TimeoutException("Timed out waiting for tuning integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params TuningClient[] clients)
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

    private static async Task StepUntilAsync(TuningClient mover, Direction8 direction, Func<bool> condition, params TuningClient[] observers)
    {
        var clients = observers.Prepend(mover).Distinct().ToArray();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
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

    private sealed class TuningClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private uint _moveSequence;

        public TuningClient(string name)
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

        public List<IProtocolMessage> Messages { get; } = [];
        public bool IsLoggedIn { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public TileCoord OwnTile { get; private set; } = TileGrid.DefaultSpawnTile;

        public void Connect(int port, string key)
        {
            _client.Start();
            _client.Connect("127.0.0.1", port, key);
        }

        public void Poll() => _client.PollEvents();

        public void SendMove(Direction8 direction) =>
            Send(new MoveIntentMessage(++_moveSequence, true, direction), DeliveryMethod.ReliableOrdered);

        public void StopMove() =>
            Send(new MoveIntentMessage(++_moveSequence, false, Direction8.S), DeliveryMethod.ReliableOrdered);

        public void SendAdminSetTuning(string key, double value) =>
            Send(new AdminSetTuningMessage(key, value), DeliveryMethod.ReliableOrdered);

        public void ClearMessages() => Messages.Clear();

        public void Dispose() => _client.Stop();

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
                        if (spawn.DisplayName == _name)
                        {
                            OwnNetworkId = spawn.NetworkId;
                            OwnTile = spawn.Tile;
                        }

                        break;
                    case WorldSnapshotMessage snapshot:
                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                        foreach (var entity in snapshot.Entities)
                        {
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
