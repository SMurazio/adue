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

// SLIME-FEEL-POLISH end-to-end: the monster hop PACING was redesigned around the intuitive RANGE / HEIGHT / AIRBORNE /
// DELAY knobs, and the opaque "move speed (x)" knob was RETIRED as a tunable. Two consequences are pinned here against a
// live server:
//   (1) the entity's REPLICATED interp cadence (EntitySpawn / MovementSpeedChanged, seeded from MoveSpeedMultiplier at
//       spawn) is intentionally DECOUPLED from the hop cadence (now HopAirborneTicks + HopDelayTicks) — so editing a hop
//       knob (slime.hopDelayMs) re-paces the AI's hops but does NOT emit a MovementSpeedChanged for the spawned slime;
//   (2) "slime.moveSpeed" is no longer a recognized tuning key — an AdminSetTuning on it is rejected (no re-pace, no
//       broadcast), confirming the retirement end-to-end.
// (The earlier LIVESPEED-DESYNC scenario — a live moveSpeed edit re-pacing + replicating the cadence — no longer exists
// because moveSpeed can no longer be edited live; this file replaces those tests.)
public sealed class MonsterHopPacingIntegrationTests
{
    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140;

    // Mirror the server's tick-quantised derivation (GameServer.EffectiveStepCooldownTicks / Ms): base 140ms @ 20Hz
    // ⇒ ceil(140/50)=3 ticks; effective ticks = round(baseTicks / multiplier) (>=1), wire ms = ticks * 50.
    private static int TickIntervalMs => 1000 / TickRate;
    private static int BaseTicks => (int)Math.Ceiling(BaseStepCooldownMs / (double)TickIntervalMs);
    private static ushort EffectiveMs(double multiplier)
    {
        var ticks = Math.Max(1, (int)Math.Round(BaseTicks / multiplier, MidpointRounding.AwayFromZero));
        return (ushort)(ticks * TickIntervalMs);
    }

    [Fact]
    public async Task EditingSlimeHopDelayDoesNotRepaceTheReplicatedInterpCadence()
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
            using var admin = new MonsterTuningClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn && admin.OwnNetworkId != 0, admin);

            // Spawn a slime at the admin's own tile (clustered ⇒ instantly in AOI ⇒ the EntitySpawn arrives).
            admin.SendChat("/monster");
            await WaitUntilAsync(() => admin.TryGetMonsterSpawn(out _), admin);
            Assert.True(admin.TryGetMonsterSpawn(out var slimeSpawn));
            var slimeId = slimeSpawn.NetworkId;

            // The slime's spawn advertises the TYPE's interp cadence seeded from MoveSpeedMultiplier (0.6x default).
            Assert.Equal(EffectiveMs(0.6), slimeSpawn.StepCooldownMs);

            // Admin lengthens the grounded REST between hops on the F1 tab. This re-paces the AI's HOP cadence, but the
            // replicated interp cadence is decoupled and left as-is — so NO MovementSpeedChanged is emitted for the slime.
            admin.ClearSpeedChanges();
            admin.SendAdminSetTuning("slime.hopDelayMs", 1000d);
            await PollForAsync(TimeSpan.FromMilliseconds(500), admin);
            Assert.DoesNotContain(admin.SpeedChanges, m => m.NetworkId == slimeId);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task MoveSpeedIsNoLongerATunableKeyAndBroadcastsNothing()
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
            using var admin = new MonsterTuningClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn && admin.OwnNetworkId != 0, admin);

            admin.SendChat("/monster");
            await WaitUntilAsync(() => admin.TryGetMonsterSpawn(out _), admin);
            Assert.True(admin.TryGetMonsterSpawn(out var slimeSpawn));
            var slimeId = slimeSpawn.NetworkId;

            // "slime.moveSpeed" is the RETIRED knob — no longer a recognized key. The server rejects + logs it, so it
            // re-paces nothing and broadcasts no MovementSpeedChanged for the slime.
            admin.ClearSpeedChanges();
            admin.SendAdminSetTuning("slime.moveSpeed", 1.0d);
            await PollForAsync(TimeSpan.FromMilliseconds(500), admin);
            Assert.DoesNotContain(admin.SpeedChanges, m => m.NetworkId == slimeId);
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
            "monster-hop-pacing-test",
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

    private static async Task WaitUntilAsync(Func<bool> condition, params MonsterTuningClient[] clients)
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

        throw new TimeoutException("Timed out waiting for monster hop-pacing integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params MonsterTuningClient[] clients)
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

    private sealed class MonsterTuningClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private bool _disposed;

        public MonsterTuningClient(string name)
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

        public void SendChat(string text) =>
            Send(new ChatSendMessage(text), DeliveryMethod.ReliableOrdered);

        public void SendAdminSetTuning(string key, double value) =>
            Send(new AdminSetTuningMessage(key, value), DeliveryMethod.ReliableOrdered);

        public void ClearSpeedChanges() => SpeedChanges.Clear();

        // The single spawned monster: a Monster-kind EntitySpawn that isn't the admin's own player.
        public bool TryGetMonsterSpawn(out EntitySpawnMessage spawn)
        {
            spawn = KnownSpawns.FirstOrDefault(s => s.Kind == EntityKind.Monster)!;
            return spawn is not null;
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
                        if (spawn.DisplayName == _name && spawn.Kind == EntityKind.Player)
                        {
                            OwnNetworkId = spawn.NetworkId;
                        }

                        break;
                    case MovementSpeedChangedMessage speed:
                        SpeedChanges.Add(speed);
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
