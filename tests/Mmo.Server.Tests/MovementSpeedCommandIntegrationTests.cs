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

// S51 end-to-end: the admin-gated /speed dev command sets the caller's own entity SpeedMultiplier, the
// server recomputes the effective cadence, and a MovementSpeedChanged is replicated to every viewer whose
// AOI includes the caller. Also covers admin-gating (a non-admin /speed is denied and changes nothing).
public sealed class MovementSpeedCommandIntegrationTests
{
    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140;

    // Effective cadence is tick-quantised, so the wire ms is derived from the snapped tick count, not the
    // raw ms. Mirror the server's derivation here so the assertions track the real values:
    // base 140ms @ 20Hz ⇒ ceil(140/50)=3 ticks ⇒ 150ms; multiplier 2 ⇒ round(3/2)=2 ticks ⇒ 100ms.
    private static int TickIntervalMs => 1000 / TickRate;
    private static int BaseTicks => (int)Math.Ceiling(BaseStepCooldownMs / (double)TickIntervalMs);
    private static ushort EffectiveMs(double multiplier)
    {
        var ticks = Math.Max(1, (int)Math.Round(BaseTicks / multiplier, MidpointRounding.AwayFromZero));
        return (ushort)(ticks * TickIntervalMs);
    }

    [Fact]
    public async Task AdminSpeedCommandChangesCadenceAndReplicatesToAoiViewer()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: ["Mover"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var mover = new IntegrationClient("Mover");
            using var viewer = new IntegrationClient("Viewer");
            mover.Connect(port, options.ConnectionKey);
            viewer.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => mover.IsLoggedIn && mover.OwnNetworkId != 0 && viewer.IsLoggedIn && viewer.OwnNetworkId != 0,
                mover,
                viewer);

            // Both spawn clustered, so the viewer has the mover in its AOI and received the mover's spawn.
            await WaitUntilAsync(() => viewer.KnownSpawns.Any(s => s.NetworkId == mover.OwnNetworkId), mover, viewer);

            // Mover's spawn advertises the base effective cooldown (multiplier 1.0 ⇒ base, tick-quantised).
            var moverSpawn = viewer.KnownSpawns.First(s => s.NetworkId == mover.OwnNetworkId);
            Assert.Equal(EffectiveMs(1.0), moverSpawn.StepCooldownMs);

            // /speed 2 ⇒ effective cooldown roughly halves; the viewer receives a MovementSpeedChanged.
            mover.SendChat("/speed 2");
            await WaitUntilAsync(
                () => viewer.SpeedChanges.Any(m => m.NetworkId == mover.OwnNetworkId),
                mover,
                viewer);

            var change = viewer.SpeedChanges.Last(m => m.NetworkId == mover.OwnNetworkId);
            Assert.Equal(EffectiveMs(2.0), change.StepCooldownMs);
            Assert.True(change.StepCooldownMs < moverSpawn.StepCooldownMs);

            // /speed 1 resets back to the base cadence and replicates again.
            mover.SendChat("/speed 1");
            await WaitUntilAsync(
                () => viewer.SpeedChanges.Any(m => m.NetworkId == mover.OwnNetworkId && m.StepCooldownMs == EffectiveMs(1.0)),
                mover,
                viewer);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task NonAdminSpeedCommandIsDeniedAndDoesNotReplicate()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        // No admin names ⇒ everyone is a Player; /speed must be denied.
        var options = CreateOptions(port, database.ConnectionString, admins: []);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var player = new IntegrationClient("Player");
            using var viewer = new IntegrationClient("Viewer");
            player.Connect(port, options.ConnectionKey);
            viewer.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => player.IsLoggedIn && player.OwnNetworkId != 0 && viewer.IsLoggedIn && viewer.OwnNetworkId != 0,
                player,
                viewer);
            await WaitUntilAsync(() => viewer.KnownSpawns.Any(s => s.NetworkId == player.OwnNetworkId), player, viewer);

            player.SendChat("/speed 2");
            // The caller gets a denial system message; no MovementSpeedChanged is ever emitted to the AOI.
            await WaitUntilAsync(
                () => player.ChatLines.Any(c => c.Text.Contains("denied", StringComparison.OrdinalIgnoreCase)),
                player,
                viewer);

            await PollForAsync(TimeSpan.FromMilliseconds(400), player, viewer);
            Assert.Empty(viewer.SpeedChanges);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static ServerOptions CreateOptions(int port, string connectionString, string[] admins)
    {
        return new ServerOptions(
            port,
            20,
            "speed-command-test",
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
            new HashSet<string>(admins, StringComparer.OrdinalIgnoreCase));
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params IntegrationClient[] clients)
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

        throw new TimeoutException("Timed out waiting for /speed integration condition.");
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
            _client = new NetManager(_listener) { AutoRecycle = false };
            _listener.PeerConnectedEvent += peer =>
            {
                _serverPeer = peer;
                Send(new ClientHelloMessage(_name), DeliveryMethod.ReliableOrdered);
                Send(new LoginRequestMessage(_name, _name), DeliveryMethod.ReliableOrdered);
            };
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public List<EntitySpawnMessage> KnownSpawns { get; } = [];
        public List<MovementSpeedChangedMessage> SpeedChanges { get; } = [];
        public List<ChatBroadcastMessage> ChatLines { get; } = [];
        public bool IsLoggedIn { get; private set; }
        public uint OwnNetworkId { get; private set; }

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

        public void SendChat(string text)
        {
            Send(new ChatSendMessage(text), DeliveryMethod.ReliableOrdered);
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
                        break;
                    case EntitySpawnMessage spawn:
                        KnownSpawns.Add(spawn);
                        if (spawn.DisplayName == _name)
                        {
                            OwnNetworkId = spawn.NetworkId;
                        }

                        break;
                    case MovementSpeedChangedMessage speed:
                        SpeedChanges.Add(speed);
                        break;
                    case ChatBroadcastMessage chat:
                        ChatLines.Add(chat);
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
