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

public sealed class PersistenceWriteBehindIntegrationTests
{
    [Fact]
    public async Task PeriodicCheckpointPersistsDirtyPlayerTile()
    {
        var repository = new RecordingCharacterRepository();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, persistenceCheckpointSeconds: 1);
        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("CheckpointPlayer");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            var start = client.OwnTile;
            await StepUntilAsync(client, Direction8.E, () => client.OwnTile.X > start.X);
            var characterId = client.CharacterId;

            var save = await repository.WaitForSaveAsync(
                item => item.CharacterId == characterId && item.Tile.X > start.X,
                TimeSpan.FromSeconds(10));

            Assert.True(save.Tile.X > start.X);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task DisconnectFlushPersistsDirtyPlayerTileBeforeLongCheckpoint()
    {
        var repository = new RecordingCharacterRepository();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, persistenceCheckpointSeconds: 60);
        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("DisconnectPlayer");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            var start = client.OwnTile;
            await StepUntilAsync(client, Direction8.E, () => client.OwnTile.X > start.X);
            var characterId = client.CharacterId;

            await client.DisconnectAsync();

            var save = await repository.WaitForSaveAsync(
                item => item.CharacterId == characterId && item.Tile.X > start.X,
                TimeSpan.FromSeconds(10));

            Assert.True(save.Tile.X > start.X);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static ServerOptions CreateOptions(int port, int persistenceCheckpointSeconds)
    {
        return new ServerOptions(
            port,
            20,
            "persistence-test",
            DatabaseProvider.Sqlite,
            "Data Source=:memory:",
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            50,
            persistenceCheckpointSeconds,
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

        throw new TimeoutException("Timed out waiting for persistence integration condition.");
    }

    private static async Task StepUntilAsync(IntegrationClient mover, Direction8 direction, Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            mover.SendMove(direction);
            await WaitForPollAsync(TimeSpan.FromMilliseconds(75), mover);
            if (condition())
            {
                return;
            }
        }

        throw new TimeoutException("Timed out waiting for step movement condition.");
    }

    private static async Task WaitForPollAsync(TimeSpan duration, params IntegrationClient[] clients)
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
        private uint _moveSequence;
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
        public TileCoord OwnTile { get; private set; } = TileGrid.DefaultSpawnTile;

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

        public void SendMove(Direction8 direction)
        {
            Send(new MoveStepMessage(++_moveSequence, direction), DeliveryMethod.Sequenced);
        }

        public async Task DisconnectAsync()
        {
            if (_disposed)
            {
                return;
            }

            _serverPeer?.Disconnect();
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
            while (!IsDisconnected && DateTimeOffset.UtcNow < deadline)
            {
                _client.PollEvents();
                await Task.Delay(10);
            }

            _client.Stop();
            _disposed = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _serverPeer?.Disconnect();
            for (var i = 0; i < 5 && !IsDisconnected; i++)
            {
                _client.PollEvents();
                Thread.Sleep(10);
            }

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
                    case EntitySpawnMessage spawn when spawn.DisplayName == _name:
                        OwnNetworkId = spawn.NetworkId;
                        OwnTile = spawn.Tile;
                        break;
                    case WorldSnapshotMessage snapshot:
                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                        foreach (var entity in snapshot.Entities)
                        {
                            if (entity.NetworkId == OwnNetworkId)
                            {
                                OwnTile = entity.Tile;
                                break;
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

    private sealed class RecordingCharacterRepository : ICharacterRepository
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, CharacterRecord> _characters = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SaveRecord> _saves = [];
        private TaskCompletionSource _saveSignal = NewSignal();

        public Task<CharacterRecord> LoadOrCreateAsync(
            string accountName,
            string displayName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{accountName.Trim()}:{displayName.Trim()}";
            lock (_lock)
            {
                if (!_characters.TryGetValue(key, out var character))
                {
                    character = new CharacterRecord(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        displayName.Trim(),
                        Zone.DefaultId,
                        TileGrid.DefaultSpawnTile);
                    _characters.Add(key, character);
                }

                return Task.FromResult(character);
            }
        }

        public Task SaveTileAsync(Guid characterId, TileCoord tile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource signal;
            lock (_lock)
            {
                _saves.Add(new SaveRecord(characterId, tile));
                signal = _saveSignal;
                _saveSignal = NewSignal();
            }

            signal.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task<SaveRecord> WaitForSaveAsync(Predicate<SaveRecord> predicate, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                Task waitTask;
                lock (_lock)
                {
                    foreach (var save in _saves)
                    {
                        if (predicate(save))
                        {
                            return save;
                        }
                    }

                    waitTask = _saveSignal.Task;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var completed = await Task.WhenAny(waitTask, Task.Delay(remaining));
                if (completed != waitTask)
                {
                    break;
                }
            }

            throw new TimeoutException("Timed out waiting for repository save.");
        }

        private static TaskCompletionSource NewSignal()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private readonly record struct SaveRecord(Guid CharacterId, TileCoord Tile);
}
