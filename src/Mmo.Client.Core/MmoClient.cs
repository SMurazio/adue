using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Client.Core;

public sealed class MmoClient : IDisposable
{
    public const double RemoteInterpolationCadenceMultiplier = 1.3d;

    private readonly ClientConnectionOptions _options;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _netManager;
    private readonly Dictionary<uint, ClientEntity> _entities = [];
    private readonly List<ChatLine> _chatLog = [];
    private readonly List<ClientError> _errors = [];
    private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

    private NetPeer? _serverPeer;
    private PendingSnapshot? _pendingSnapshot;
    private uint? _lastAppliedSnapshotSequence;
    private uint _moveSequence;
    private Guid _localCharacterId;
    private TileCoord? _loginTile;
    private TimeSpan _currentTime;
    private bool _disposed;

    public MmoClient(ClientConnectionOptions options)
    {
        _options = options;
        _netManager = new NetManager(_listener)
        {
            AutoRecycle = false,
            DisconnectTimeout = 15000
        };

        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += (_, _) =>
        {
            _serverPeer = null;
            State = ClientConnectionState.Disconnected;
        };
        _listener.NetworkErrorEvent += (_, error) => _errors.Add(new ClientError("network", error.ToString()));
        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    public ClientConnectionState State { get; private set; } = ClientConnectionState.Disconnected;

    public ServerInfo? Server { get; private set; }

    public ZoneModel? Zone { get; private set; }

    public ClientRole Role { get; private set; } = ClientRole.Player;

    public bool IsLoggedIn => State == ClientConnectionState.LoggedIn;

    public Guid LocalCharacterId => _localCharacterId;

    public uint? LocalNetworkId { get; private set; }

    public TileCoord? LocalTile => LocalNetworkId.HasValue && _entities.TryGetValue(LocalNetworkId.Value, out var entity)
        ? entity.Tile
        : _loginTile;

    public IReadOnlyList<ChatLine> ChatLog => _chatLog;

    public IReadOnlyList<ClientError> Errors => _errors;

    public IReadOnlyList<ReplicatedEntity> Entities => _entities.Values.Select(static entity => entity.ToSnapshot()).ToArray();

    public IReadOnlyList<EntityRenderState> GetRenderStates()
    {
        return GetRenderStates(_currentTime);
    }

    public IReadOnlyList<EntityRenderState> GetRenderStates(TimeSpan now)
    {
        return _entities.Values.Select(entity => entity.ToRenderState(now)).ToArray();
    }

    public bool TryGetEntity(uint networkId, out ReplicatedEntity entity)
    {
        if (_entities.TryGetValue(networkId, out var stored))
        {
            entity = stored.ToSnapshot();
            return true;
        }

        entity = default!;
        return false;
    }

    public void Connect()
    {
        ThrowIfDisposed();
        if (State != ClientConnectionState.Disconnected)
        {
            return;
        }

        _netManager.Start();
        _netManager.Connect(_options.Host, _options.Port, _options.ConnectionKey);
        State = ClientConnectionState.Connecting;
    }

    public void Poll()
    {
        Poll(System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt));
    }

    public void Poll(TimeSpan now)
    {
        ThrowIfDisposed();
        _currentTime = now;
        _netManager.PollEvents();
    }

    public void Disconnect()
    {
        if (_disposed)
        {
            return;
        }

        if (_serverPeer is not null)
        {
            _netManager.DisconnectPeer(_serverPeer);
            _netManager.PollEvents();
        }

        _netManager.Stop();
        State = ClientConnectionState.Disconnected;
        _serverPeer = null;
    }

    public uint SendMoveStep(Direction8 direction)
    {
        var sequence = ++_moveSequence;
        Send(new MoveStepMessage(sequence, direction), DeliveryMethod.Sequenced);
        return sequence;
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

        Disconnect();
        _disposed = true;
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _serverPeer = peer;
        State = ClientConnectionState.Connected;
        Send(new ClientHelloMessage(_options.ClientName), DeliveryMethod.ReliableOrdered);
        Send(new LoginRequestMessage(_options.AccountName, _options.DisplayName), DeliveryMethod.ReliableOrdered);
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            HandleMessage(ProtocolCodec.Decode(reader.GetRemainingBytes()));
        }
        catch (Exception exception)
        {
            _errors.Add(new ClientError("bad-packet", exception.Message));
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleMessage(IProtocolMessage message)
    {
        switch (message)
        {
            case ServerHelloMessage hello:
                Server = new ServerInfo(hello.ServerName, hello.ProtocolVersion, hello.TickRate, hello.StepCooldownMs, hello.InterestRadiusTiles);
                break;
            case LoginResultMessage login:
                HandleLogin(login);
                break;
            case ZoneInfoMessage zone:
                Zone = new ZoneModel(zone.ZoneId, zone.Width, zone.Height, zone.BlockedTiles);
                break;
            case EntitySpawnMessage spawn:
                UpsertEntity(spawn.NetworkId, spawn.CharacterId, spawn.Kind, spawn.DisplayName, spawn.Tile, spawn.Facing);
                break;
            case EntityDespawnMessage despawn:
                _entities.Remove(despawn.NetworkId);
                if (LocalNetworkId == despawn.NetworkId)
                {
                    LocalNetworkId = null;
                }

                break;
            case WorldSnapshotMessage snapshot:
                HandleSnapshot(snapshot);
                break;
            case ChatBroadcastMessage chat:
                _chatLog.Add(new ChatLine(chat.Sender, chat.Text));
                break;
            case ServerErrorMessage error:
                _errors.Add(new ClientError(error.Code, error.Message));
                break;
        }
    }

    private void HandleLogin(LoginResultMessage login)
    {
        if (!login.Accepted)
        {
            _errors.Add(new ClientError("login-rejected", login.Reason));
            return;
        }

        _localCharacterId = login.CharacterId;
        _loginTile = login.Tile;
        Role = login.Role;
        State = ClientConnectionState.LoggedIn;
    }

    private void HandleSnapshot(WorldSnapshotMessage snapshot)
    {
        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);

        if (_lastAppliedSnapshotSequence.HasValue && snapshot.SnapshotSequence <= _lastAppliedSnapshotSequence.Value)
        {
            return;
        }

        if (snapshot.ChunkCount <= 1)
        {
            ApplySnapshot(snapshot.ServerTick, snapshot.SnapshotSequence, snapshot.IsComplete, snapshot.Entities);
            return;
        }

        if (snapshot.ChunkIndex < 0 || snapshot.ChunkIndex >= snapshot.ChunkCount)
        {
            return;
        }

        if (_pendingSnapshot is null || _pendingSnapshot.Sequence != snapshot.SnapshotSequence)
        {
            _pendingSnapshot = new PendingSnapshot(snapshot.ServerTick, snapshot.SnapshotSequence, snapshot.IsComplete, snapshot.TotalEntities, snapshot.ChunkCount);
        }

        _pendingSnapshot.AddChunk(snapshot.ChunkIndex, snapshot.Entities);
        if (!_pendingSnapshot.IsComplete)
        {
            return;
        }

        ApplySnapshot(
            _pendingSnapshot.ServerTick,
            _pendingSnapshot.Sequence,
            _pendingSnapshot.IsFullSnapshot,
            _pendingSnapshot.Entities);
        _pendingSnapshot = null;
    }

    private void ApplySnapshot(uint serverTick, uint sequence, bool isComplete, IReadOnlyCollection<EntityStateSnapshot> entities)
    {
        var visible = new HashSet<uint>();
        foreach (var state in entities)
        {
            visible.Add(state.NetworkId);
            if (!_entities.TryGetValue(state.NetworkId, out var entity))
            {
                entity = UpsertEntity(
                    state.NetworkId,
                    Guid.Empty,
                    EntityKind.Player,
                    $"#{state.NetworkId}",
                    state.Tile,
                    state.Facing);
            }

            entity.ApplySnapshot(state.Tile, state.Facing, _currentTime);
        }

        if (isComplete)
        {
            foreach (var networkId in _entities.Keys.Where(networkId => !visible.Contains(networkId)).ToArray())
            {
                _entities.Remove(networkId);
                if (LocalNetworkId == networkId)
                {
                    LocalNetworkId = null;
                }
            }
        }

        _lastAppliedSnapshotSequence = sequence;
    }

    private ClientEntity UpsertEntity(
        uint networkId,
        Guid characterId,
        EntityKind kind,
        string displayName,
        TileCoord tile,
        Direction8 facing)
    {
        var isLocal = characterId != Guid.Empty && characterId == _localCharacterId;
        if (_entities.TryGetValue(networkId, out var existing))
        {
            existing.UpdateMetadata(characterId, kind, displayName, isLocal);
            existing.ApplySnapshot(tile, facing, _currentTime);
            if (isLocal)
            {
                LocalNetworkId = networkId;
            }

            return existing;
        }

        var entity = new ClientEntity(
            networkId,
            characterId,
            kind,
            displayName,
            tile,
            facing,
            isLocal,
            CreateInterpolator(tile, isLocal));
        _entities[networkId] = entity;
        if (isLocal)
        {
            LocalNetworkId = networkId;
        }

        return entity;
    }

    private TileInterpolator CreateInterpolator(TileCoord initialTile, bool isLocal)
    {
        var cadence = Server?.EffectiveStepCadenceMs ?? MovementCadence.EffectiveStepCadenceMs(140, 20);
        var delay = isLocal ? 0d : cadence * RemoteInterpolationCadenceMultiplier;
        return new TileInterpolator(initialTile, cadence, delay);
    }

    private void Send(IProtocolMessage message, DeliveryMethod deliveryMethod)
    {
        if (_serverPeer is null)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        _serverPeer.Send(ProtocolCodec.Encode(message), deliveryMethod);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ClientEntity
    {
        private readonly TileInterpolator _interpolator;

        public ClientEntity(
            uint networkId,
            Guid characterId,
            EntityKind kind,
            string displayName,
            TileCoord tile,
            Direction8 facing,
            bool isLocal,
            TileInterpolator interpolator)
        {
            NetworkId = networkId;
            CharacterId = characterId;
            Kind = kind;
            DisplayName = displayName;
            Tile = tile;
            Facing = facing;
            IsLocal = isLocal;
            _interpolator = interpolator;
        }

        public uint NetworkId { get; }

        public Guid CharacterId { get; private set; }

        public EntityKind Kind { get; private set; }

        public string DisplayName { get; private set; }

        public TileCoord Tile { get; private set; }

        public Direction8 Facing { get; private set; }

        public bool IsLocal { get; private set; }

        public void UpdateMetadata(Guid characterId, EntityKind kind, string displayName, bool isLocal)
        {
            if (characterId != Guid.Empty)
            {
                CharacterId = characterId;
            }

            Kind = kind;
            DisplayName = displayName;
            IsLocal = isLocal || IsLocal;
        }

        public void ApplySnapshot(TileCoord tile, Direction8 facing, TimeSpan receivedAt)
        {
            Tile = tile;
            Facing = facing;
            _interpolator.Confirm(tile, receivedAt);
        }

        public ReplicatedEntity ToSnapshot()
        {
            return new ReplicatedEntity(NetworkId, CharacterId, Kind, DisplayName, Tile, Facing, IsLocal);
        }

        public EntityRenderState ToRenderState(TimeSpan now)
        {
            return new EntityRenderState(NetworkId, CharacterId, Kind, DisplayName, _interpolator.Sample(now), Tile, Facing, IsLocal);
        }
    }

    private sealed class PendingSnapshot
    {
        private readonly IReadOnlyList<EntityStateSnapshot>?[] _chunks;
        private readonly bool[] _received;

        public PendingSnapshot(uint serverTick, uint sequence, bool isFullSnapshot, int totalEntities, int chunkCount)
        {
            ServerTick = serverTick;
            Sequence = sequence;
            IsFullSnapshot = isFullSnapshot;
            TotalEntities = totalEntities;
            _chunks = new IReadOnlyList<EntityStateSnapshot>[chunkCount];
            _received = new bool[chunkCount];
        }

        public uint ServerTick { get; }

        public uint Sequence { get; }

        public bool IsFullSnapshot { get; }

        public int TotalEntities { get; }

        public bool IsComplete => _received.All(static received => received);

        public IReadOnlyList<EntityStateSnapshot> Entities => _chunks
            .Where(static chunk => chunk is not null)
            .SelectMany(static chunk => chunk!)
            .ToArray();

        public void AddChunk(int index, IReadOnlyList<EntityStateSnapshot> entities)
        {
            _chunks[index] = entities.ToArray();
            _received[index] = true;
        }
    }
}
