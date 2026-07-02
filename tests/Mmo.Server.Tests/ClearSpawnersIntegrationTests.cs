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

// SPAWNER-CLEANUP (todo/monster-types-followups #4): the admin dev command /clearspawners removes EVERY spawner and
// despawns each spawner's live monster — the fix for "/monster only ever ADDS; a long dev session accumulates
// spawners (and their markers)". Pinned against a live server end-to-end:
//   (1) each live monster despawns (EntityDespawn arrives for its network id via the normal AOI known-entity diff);
//   (2) each spawner's red marker deactivates (SpawnerMarker Active=false via SyncSpawnerMarkers' "no longer exists"
//       branch — no bespoke send);
//   (3) it is an admin CLEAR, not a kill: no corpse spawns for the cleared monster (no Corpse-kind EntitySpawn), and
//       with _spawners empty nothing respawns (RespawnMonsters iterates the now-empty spawner set).
public sealed class ClearSpawnersIntegrationTests
{
    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140;

    [Fact]
    public async Task ClearSpawnersDespawnsMonstersAndDeactivatesMarkers()
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
            using var admin = new ClearSpawnersClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn && admin.OwnNetworkId != 0, admin);

            // Two spawners of different types at the admin's tile (instantly in AOI ⇒ spawns + markers arrive).
            admin.SendChat("/monster");
            admin.SendChat("/monster gnoll");
            await WaitUntilAsync(() => admin.MonsterSpawns.Count >= 2 && admin.ActiveMarkerIds.Count >= 2, admin);
            var monsterIds = admin.MonsterSpawns.Select(s => s.NetworkId).ToArray();
            var spawnerIds = admin.ActiveMarkerIds.ToArray();

            admin.SendChat("/clearspawners");

            // (1) both monsters despawn and (2) both markers deactivate.
            await WaitUntilAsync(
                () => monsterIds.All(id => admin.DespawnedIds.Contains(id))
                    && spawnerIds.All(id => admin.DeactivatedMarkerIds.Contains(id)),
                admin);

            // (3) an admin clear is not a kill: no corpse spawned, and no monster respawned in the settle window.
            await PollForAsync(TimeSpan.FromMilliseconds(500), admin);
            Assert.DoesNotContain(admin.KnownSpawns, s => s.Kind == EntityKind.Corpse);
            Assert.Equal(2, admin.MonsterSpawns.Count);
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
            "clear-spawners-test",
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

    private static async Task WaitUntilAsync(Func<bool> condition, params ClearSpawnersClient[] clients)
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

        throw new TimeoutException("Timed out waiting for clear-spawners integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params ClearSpawnersClient[] clients)
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

    // A minimal admin client tracking exactly what /clearspawners must produce: entity spawns/despawns and the
    // spawner-marker place/drop stream. Mirrors MonsterHopPacingIntegrationTests.MonsterTuningClient.
    private sealed class ClearSpawnersClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private bool _disposed;

        public ClearSpawnersClient(string name)
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
        public HashSet<uint> DespawnedIds { get; } = [];
        public HashSet<uint> ActiveMarkerIds { get; } = [];
        public HashSet<uint> DeactivatedMarkerIds { get; } = [];
        public bool IsLoggedIn { get; private set; }
        public uint OwnNetworkId { get; private set; }

        public IReadOnlyList<EntitySpawnMessage> MonsterSpawns =>
            KnownSpawns.Where(s => s.Kind == EntityKind.Monster).ToArray();

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
                    case EntitySpawnMessage spawn:
                        KnownSpawns.Add(spawn);
                        if (spawn.DisplayName == _name && spawn.Kind == EntityKind.Player)
                        {
                            OwnNetworkId = spawn.NetworkId;
                        }

                        break;
                    case EntityDespawnMessage despawn:
                        DespawnedIds.Add(despawn.NetworkId);
                        break;
                    case SpawnerMarkerMessage marker:
                        if (marker.Active)
                        {
                            ActiveMarkerIds.Add(marker.SpawnerId);
                        }
                        else
                        {
                            DeactivatedMarkerIds.Add(marker.SpawnerId);
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
