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

public sealed class DuplicateLoginIntegrationTests
{
    [Fact]
    public async Task SecondLoginKicksExistingSessionForSameCharacter()
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
            using var first = new IntegrationClient("Duplicate");
            first.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => first.IsLoggedIn && first.OwnNetworkId != 0, first);

            using var second = new IntegrationClient("Duplicate");
            second.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => second.IsLoggedIn
                    && second.OwnNetworkId != 0
                    && second.CharacterId == first.CharacterId,
                first,
                second);

            await WaitUntilAsync(() => first.IsDisconnected, first, second);
            await PollForAsync(TimeSpan.FromMilliseconds(300), second);

            var duplicateSpawns = second.Spawns
                .Where(message => message.DisplayName == "Duplicate")
                .Select(message => message.NetworkId)
                .Distinct()
                .ToArray();

            Assert.Single(duplicateSpawns);
            Assert.True(first.IsDisconnected);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static ServerOptions CreateOptions(int port, string connectionString)
    {
        return new ServerOptions(
            port,
            20,
            "duplicate-login-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            50,
            15,
            30,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
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

        throw new TimeoutException("Timed out waiting for duplicate-login integration condition.");
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

    private sealed class IntegrationClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
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

        public bool IsLoggedIn { get; private set; }
        public bool IsDisconnected { get; private set; }
        public Guid CharacterId { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public List<EntitySpawnMessage> Spawns { get; } = [];

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
                        CharacterId = login.CharacterId;
                        break;
                    case EntitySpawnMessage spawn:
                        Spawns.Add(spawn);
                        if (spawn.DisplayName == _name)
                        {
                            OwnNetworkId = spawn.NetworkId;
                        }

                        break;
                    case WorldSnapshotMessage snapshot:
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
