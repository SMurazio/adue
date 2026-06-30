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

// SLIME-FEEL-POLISH / CONTEXTUAL-KNOBS end-to-end: the slime hop PACING is dialed by the intuitive RANGE / HEIGHT /
// AIRBORNE / DELAY knobs, and "move speed" is now a CONTEXTUAL knob — exposed only on a GLIDER (where it IS the walk
// speed), hidden on a hopper. Two consequences are pinned here against a live server:
//   (1) the slime's REPLICATED interp cadence (EntitySpawn / MovementSpeedChanged, seeded from MoveSpeedMultiplier at
//       spawn) is DECOUPLED from its hop cadence (HopAirborneTicks + HopDelayTicks) — so editing a hop knob
//       (slime.hopDelayMs) re-paces the AI's hops but does NOT emit a MovementSpeedChanged for the spawned slime;
//   (2) editing a GLIDER's walk speed ("gnoll.moveSpeed") DOES re-pace the already-spawned gnoll LIVE: because
//       SpeedUnitsPerSecond is seeded ONCE at spawn, the per-type edit re-applies the new multiplier to the spawned
//       gnoll (PropagateMonsterTypeSpeedToSpawned) and re-broadcasts its effective cadence (MovementSpeedChanged) — so
//       the AI walk speed, the entity SpeedMultiplier, and the client interpolation stay in lockstep on a live edit.
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
    public async Task EditingGnollWalkSpeedRepacesTheSpawnedGliderLive()
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

            // Spawn a GNOLL (the glider) at the admin's own tile so its EntitySpawn lands in AOI immediately.
            admin.SendChat("/monster gnoll");
            await WaitUntilAsync(() => admin.TryGetMonsterSpawn(out _), admin);
            Assert.True(admin.TryGetMonsterSpawn(out var gnollSpawn));
            var gnollId = gnollSpawn.NetworkId;

            // The gnoll spawns at its TYPE's interp cadence seeded from MoveSpeedMultiplier (0.9× default).
            Assert.Equal(EffectiveMs(0.9), gnollSpawn.StepCooldownMs);

            // CONTEXTUAL-KNOBS: editing the glider's walk speed re-paces the already-spawned gnoll LIVE — the spawn-time
            // SpeedUnitsPerSecond state is re-applied + the new effective cadence re-broadcast as a MovementSpeedChanged
            // for THIS gnoll (the LIVESPEED re-apply). 0.9 → 1.5 changes the tick-quantised cadence, so it fires.
            admin.ClearSpeedChanges();
            admin.SendAdminSetTuning("gnoll.moveSpeed", 1.5d);
            await WaitUntilAsync(() => admin.SpeedChanges.Any(m => m.NetworkId == gnollId), admin);
            var change = admin.SpeedChanges.Last(m => m.NetworkId == gnollId);
            Assert.Equal(EffectiveMs(1.5), change.StepCooldownMs);
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
