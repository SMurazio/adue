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

// TELEGRAPH T2 (docs/ability-telegraph-sync-design.md): the wire event, pinned against a live server end-to-end
// (mirroring ClearSpawnersIntegrationTests' harness):
//   (1) SCHEDULE-TIME AOI SEND: a viewer already in AOI when /slam schedules a telegraph receives ONE
//       TelegraphMessage for it — reliable, with the locked shape (exact Q12.4 radius — HONEST TELEGRAPH: what is
//       drawn is what resolves) and a resolveTick strictly after startTick (the deadline form);
//   (2) LATE AOI JOIN: a client that logs in MID-WINDUP (after the cast) receives the SAME telegraph — same id,
//       same startTick, same resolveTick — because the per-recipient known-id diff has no "already announced"
//       memory for a fresh session (the SpawnerMarker pattern). Identical ticks are what let the late joiner
//       render the correct REMAINING fill and land on the shared deadline T;
//   (3) NO DUPLICATES: the diff pass never re-sends a known id to either viewer while the telegraph stays pending.
public sealed class TelegraphWireIntegrationTests
{
    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140;

    [Fact]
    public async Task ScheduleTimeViewersAndLateJoinersReceiveTheSameTelegraphOnce()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: ["Admin"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var admin = new TelegraphClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn, admin);

            // A LONG windup (8 s) so the telegraph is still pending while the late joiner connects mid-windup.
            admin.SendChat("/slam 2 8000 15");

            // (1) the caster's own client is in AOI of its own cast — the announcement arrives on the next
            // broadcast tick after scheduling.
            await WaitUntilAsync(() => admin.Telegraphs.Count >= 1, admin);
            var cast = admin.Telegraphs[0];
            Assert.Equal(TelegraphShapeKind.Circle, cast.Shape.Kind);
            Assert.Equal(2d, cast.Shape.Radius, 6);          // exact: 2.0 is on the Q12.4 grid
            Assert.True(cast.ResolveTick > cast.StartTick);  // the deadline form: an absolute future tick
            // ~8 s @ 20 Hz = 160 ticks of windup (Ceiling-quantized server-side; allow the rounding tick).
            Assert.InRange(cast.ResolveTick - cast.StartTick, 159u, 161u);

            // (2) late AOI join: a fresh client logs in mid-windup and must receive the SAME telegraph. Clustered
            // spawns + a 64x64 map inside a 30-unit interest radius keep both players in mutual AOI of the origin.
            using var late = new TelegraphClient("Late");
            late.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => late.IsLoggedIn && late.Telegraphs.Count >= 1, admin, late);
            var joined = late.Telegraphs[0];
            Assert.Equal(cast.TelegraphId, joined.TelegraphId);
            Assert.Equal(cast.StartTick, joined.StartTick);      // NOT re-stamped at join time — the shared deadline
            Assert.Equal(cast.ResolveTick, joined.ResolveTick);
            Assert.Equal(cast.Shape, joined.Shape);

            // (3) no duplicates: the known-id diff must not re-announce a pending telegraph on later ticks.
            await PollForAsync(TimeSpan.FromMilliseconds(500), admin, late);
            Assert.Equal(1, admin.Telegraphs.Count(t => t.TelegraphId == cast.TelegraphId));
            Assert.Equal(1, late.Telegraphs.Count(t => t.TelegraphId == cast.TelegraphId));
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
            TickRate,
            "telegraph-wire-test",
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

    private static async Task WaitUntilAsync(Func<bool> condition, params TelegraphClient[] clients)
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

        throw new TimeoutException("Timed out waiting for telegraph wire integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params TelegraphClient[] clients)
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

    // A minimal client tracking exactly what T2 replicates: the TelegraphMessage stream (every arrival kept, so a
    // duplicate send is visible as a second list entry). Mirrors ClearSpawnersIntegrationTests.ClearSpawnersClient.
    private sealed class TelegraphClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private bool _disposed;

        public TelegraphClient(string name)
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

        public List<TelegraphMessage> Telegraphs { get; } = [];
        public bool IsLoggedIn { get; private set; }

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

        public void SendChat(string text) =>
            Send(new ChatSendMessage(text), DeliveryMethod.ReliableOrdered);

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
                    case TelegraphMessage telegraph:
                        Telegraphs.Add(telegraph);
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
