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

// LIVESPEED-DESYNC end-to-end: editing a monster TYPE's moveSpeed live (the F1 Monster tab → an AdminSetTuning on
// "slime.moveSpeed") must re-pace ALREADY-SPAWNED monsters of that type AND replicate the new cadence to viewers,
// exactly like the player /speed path. Before the fix the server AI re-paced the slime off the type's live
// MoveSpeedMultiplier while the entity's SpeedMultiplier (the EntitySpawn / MovementSpeedChanged cadence the client
// interpolates at) stayed at the spawn value, so no MovementSpeedChanged was sent → the slime visibly desynced from
// its 'Server positions' marker. These pin: (1) a live moveSpeed edit broadcasts a MovementSpeedChanged carrying the
// new EffectiveStepCooldownMs for the spawned monster, and that the entity now steps on the new cadence; (2) re-applying
// the SAME moveSpeed broadcasts nothing (TrySetSpeedMultiplier is a no-op when unchanged).
public sealed class MonsterLiveSpeedDesyncIntegrationTests
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
    public async Task EditingMonsterTypeMoveSpeedRepacesSpawnedMonsterAndReplicatesNewCadence()
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

            // The slime's spawn advertises its TYPE default cadence (0.6x ⇒ slower than base). This is already in
            // lockstep at spawn — the desync only appeared on a LIVE edit.
            Assert.Equal(EffectiveMs(0.6), slimeSpawn.StepCooldownMs);

            // Admin speeds the slime TYPE up to 1.0 on the F1 tab. The fix must (a) re-apply that to the spawned
            // slime entity and (b) replicate the new cadence — a MovementSpeedChanged for the slime carrying the
            // faster (shorter) effective cooldown.
            admin.SendAdminSetTuning("slime.moveSpeed", 1.0d);
            await WaitUntilAsync(
                () => admin.SpeedChanges.Any(m => m.NetworkId == slimeId),
                admin);

            var change = admin.SpeedChanges.Last(m => m.NetworkId == slimeId);
            Assert.Equal(EffectiveMs(1.0), change.StepCooldownMs);
            Assert.True(change.StepCooldownMs < slimeSpawn.StepCooldownMs, "speeding the type up must shorten the slime cadence live.");
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task ReapplyingTheSameMonsterTypeMoveSpeedBroadcastsNothing()
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

            // Set moveSpeed to the slime's CURRENT default (0.6) — an unchanged value. TrySetSpeedMultiplier returns
            // false, so no MovementSpeedChanged must be emitted for the slime (it is gated on an actual change).
            admin.ClearSpeedChanges();
            admin.SendAdminSetTuning("slime.moveSpeed", 0.6d);
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
            "monster-livespeed-test",
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

        throw new TimeoutException("Timed out waiting for monster live-speed integration condition.");
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
