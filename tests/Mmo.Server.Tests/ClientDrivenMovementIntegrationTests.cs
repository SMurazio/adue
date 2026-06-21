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

// UO1 commit 2: a client-driven session (declared via MovementModeMessage(ClientDriven: true)) must NOT have its
// entity advanced by the server's held-intent tick loop (StepHeldMovementIntents). It paces its OWN movement via
// the per-step StepCommitRequest stream; the held MoveIntent is recorded for facing/keepalive but ignored for
// pacing. The control case — the SAME held intent without the flag — must advance the entity, proving the test
// exercises the pacing path and the flag (not some other reason for stillness).
public sealed class ClientDrivenMovementIntegrationTests
{
    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140;

    [Fact]
    public async Task ClientDrivenSession_HeldIntent_DoesNotAdvanceViaTickLoop()
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
            using var player = new MovementClient("Driver");
            player.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => player.IsLoggedIn && player.OwnNetworkId != 0 && player.OwnTile.HasValue, player);

            var startTile = player.OwnTile!.Value;

            // Declare client-driven, then hold a MoveIntent east. The held-intent pacer MUST skip this session, so
            // over many ticks the confirmed tile stays put (no StepCommitRequest is sent here).
            player.Send(new MovementModeMessage(ClientDriven: true));
            player.Send(new MoveIntentMessage(1, true, Direction8.E));

            await PollForAsync(TimeSpan.FromMilliseconds(800), player); // ~16 ticks >> several step cooldowns

            Assert.Equal(startTile, player.OwnTile!.Value);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task ServerPacedSession_HeldIntent_DoesAdvanceViaTickLoop()
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
            using var player = new MovementClient("Paced");
            player.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => player.IsLoggedIn && player.OwnNetworkId != 0 && player.OwnTile.HasValue, player);

            var startTile = player.OwnTile!.Value;

            // No MovementMode message (default server-paced). The same held intent must advance the entity via the
            // tick loop — proving the still-ness above is the client-driven flag, not an unrelated cause.
            player.Send(new MoveIntentMessage(1, true, Direction8.E));

            await WaitUntilAsync(() => player.OwnTile!.Value != startTile, player);
            Assert.NotEqual(startTile, player.OwnTile!.Value);
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
            TickRate,
            "client-driven-test",
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
            new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            ResourceNodeDensityTilesPerNode = 0,
        };
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params MovementClient[] clients)
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

        throw new TimeoutException("Timed out waiting for client-driven movement integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params MovementClient[] clients)
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

    // A minimal integration client that tracks its own confirmed tile from the AOI snapshot stream so a test can
    // assert whether the server advanced it. Acks snapshots (so the server keeps streaming) and exposes Send for
    // raw protocol messages.
    private sealed class MovementClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private bool _disposed;

        public MovementClient(string name)
        {
            _name = name;
            _client = new NetManager(_listener) { AutoRecycle = false };
            _listener.PeerConnectedEvent += peer =>
            {
                _serverPeer = peer;
                Send(new ClientHelloMessage(_name));
                Send(new LoginRequestMessage(_name, _name));
            };
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public bool IsLoggedIn { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public TileCoord? OwnTile { get; private set; }

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

        public void Send(IProtocolMessage message)
        {
            _serverPeer?.Send(ProtocolCodec.Encode(message), DeliveryMethod.ReliableOrdered);
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
                        OwnTile = login.Tile;
                        break;
                    case EntitySpawnMessage spawn:
                        if (spawn.DisplayName == _name)
                        {
                            OwnNetworkId = spawn.NetworkId;
                            OwnTile = spawn.Tile;
                        }

                        break;
                    case WorldSnapshotMessage snapshot:
                        foreach (var state in snapshot.Entities)
                        {
                            if (state.NetworkId == OwnNetworkId)
                            {
                                OwnTile = state.Tile;
                            }
                        }

                        // Sequenced ack so the server keeps streaming deltas (and re-includes our entity on move).
                        _serverPeer?.Send(ProtocolCodec.Encode(new SnapshotAckMessage(snapshot.SnapshotSequence)), DeliveryMethod.Sequenced);
                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }
    }
}
