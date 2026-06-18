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
            40,
            5,
            150,
            new WorldBounds(-100, 100, -100, 100),
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

            outsideClient.SendMove(1, 0);
            await WaitUntilAsync(() => outsideClient.OwnPosition.X > 8, outsideClient);
            outsideClient.SendMove(0, 0);

            using var observer = new IntegrationClient("Observer");
            observer.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => observer.IsLoggedIn && observer.OwnNetworkId != 0, observer, outsideClient);

            var outsideNetworkId = outsideClient.OwnNetworkId;
            observer.ClearMessages();
            outsideClient.SendMove(-1, 0);
            await WaitUntilAsync(
                () => observer.Messages.OfType<EntitySpawnMessage>().Any(message => message.NetworkId == outsideNetworkId),
                observer,
                outsideClient);

            observer.ClearMessages();
            outsideClient.SendMove(1, 0);
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
        public WorldVector OwnPosition { get; private set; } = WorldVector.Zero;

        public void Connect(int port, string key)
        {
            _client.Start();
            _client.Connect("127.0.0.1", port, key);
        }

        public void Poll()
        {
            _client.PollEvents();
        }

        public void SendMove(float x, float y)
        {
            Send(new MoveInputMessage(++_moveSequence, new WorldVector(x, y)), DeliveryMethod.Unreliable);
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
                        OwnPosition = spawn.Position;
                        break;
                    case WorldSnapshotMessage snapshot:
                        var own = snapshot.Entities.FirstOrDefault(entity => entity.NetworkId == OwnNetworkId);
                        if (own is not null)
                        {
                            OwnPosition = own.Position;
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
