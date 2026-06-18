using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LiteNetLib;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Server.Runtime;

public sealed class GameServer
{
    private const string ServerName = "mmo-learning-server";
    private const int MaxSequencedSnapshotBytes = 1000;
    private const int ProtocolHeaderBytes = 7;
    private const int SnapshotHeaderBytes = 17;
    private const int EntityStateFixedBytes = 7;
    private const int MaxBadPacketsBeforeDisconnect = 5;
    private const int DefaultStressClientCount = 120;
    private static readonly TimeSpan DefaultStressDuration = TimeSpan.FromSeconds(60);
    private const string PlaceholderEntityName = "Ancient Marker";
    private const float InterestExitHysteresisTiles = 1f;
    private const float SnapshotRetentionBonusDistanceSquared = 144f;

    private readonly ServerOptions _options;
    private readonly ICharacterRepository _characters;
    private readonly PersistenceWriteBehindWorker _persistence;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _netManager;
    private readonly Dictionary<NetPeer, ClientSession> _sessions = new();
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly SyntheticClientLoad _syntheticLoad = new();
    private readonly ServerMetrics _metrics = new();
    private readonly ServerRuntimeGuard _runtimeGuard;
    private readonly ServerMovementTrace _movementTrace;
    private readonly NetworkIdPool _networkIds = new();
    private readonly Zone _zone;
    private readonly List<ClientSession> _authenticatedScratch = [];
    private readonly List<WorldEntity> _entityScratch = [];
    private readonly List<VisibleEntity> _visibleCandidateScratch = [];
    private readonly List<WorldEntity> _visibleEntityScratch = [];
    private readonly HashSet<uint> _visibleNetworkIdScratch = [];
    private readonly List<uint> _despawnScratch = [];
    private readonly List<WorldEntity> _payloadEntityScratch = [];
    private readonly List<EntityStateSnapshot> _snapshotChunkScratch = [];
    private readonly VisibleEntityComparer _visibleEntityComparer = new();
    private readonly ProtocolEncodeBuffer _messageEncodeBuffer = new();
    private readonly ProtocolEncodeBuffer _snapshotEncodeBuffer = new();
    private readonly Dictionary<Guid, PendingTileSave> _dirtyDurableTiles = [];

    private uint _serverTick;
    private uint _nextPersistenceCheckpointTick;
    private long _pendingMovementElapsedTicks;

    public GameServer(ServerOptions options, ICharacterRepository characters)
    {
        _options = options;
        _characters = characters;
        _persistence = new PersistenceWriteBehindWorker(characters);
        _nextPersistenceCheckpointTick = options.PersistenceCheckpointTicks;
        _runtimeGuard = new ServerRuntimeGuard(_metrics);
        _movementTrace = new ServerMovementTrace(options);
        _zone = Zone.CreateDefault(options.WorldWidthTiles, options.WorldHeightTiles, options.SpawnDistribution);
        _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Resource, PlaceholderEntityName, ResolvePlaceholderEntityTile(), Direction8.S);
        _netManager = new NetManager(_listener)
        {
            AutoRecycle = false,
            DisconnectTimeout = 15000
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
        _listener.NetworkLatencyUpdateEvent += OnNetworkLatencyUpdate;
        _listener.NetworkErrorEvent += (endpoint, error) =>
        {
            _metrics.RecordNetworkError();
            Log.Warn($"Network error from {endpoint}: {error}.");
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _netManager.Start(_options.Port);
        Log.Info($"Server listening on UDP {_options.Port}.");

        var tickInterval = TimeSpan.FromSeconds(1d / _options.TickRate);
        var nextTickAt = DateTimeOffset.UtcNow;
        var lastTickStartedAt = 0L;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _netManager.PollEvents();
                _syntheticLoad.Poll();
                DrainMainThreadActions();

                var now = DateTimeOffset.UtcNow;
                var catchUpTicksThisIteration = CountDueTicks(now, nextTickAt, tickInterval);
                while (now >= nextTickAt)
                {
                    var tickStartedAt = Stopwatch.GetTimestamp();
                    var interTickGap = lastTickStartedAt == 0
                        ? TimeSpan.Zero
                        : Stopwatch.GetElapsedTime(lastTickStartedAt, tickStartedAt);
                    lastTickStartedAt = tickStartedAt;
                    var gen0Before = GC.CollectionCount(0);
                    var gen1Before = GC.CollectionCount(1);
                    var gen2Before = GC.CollectionCount(2);
                    var tickBudget = new TickBudgetRecorder();
                    var scheduleDrift = now - nextTickAt;
                    Tick(tickBudget);
                    var tickDuration = Stopwatch.GetElapsedTime(tickStartedAt);
                    var budgetSample = tickBudget.ToSample();
                    _metrics.RecordTick(tickDuration, scheduleDrift, budgetSample);
                    _movementTrace.TickHitch(
                        _serverTick,
                        interTickGap,
                        tickDuration,
                        scheduleDrift,
                        budgetSample,
                        catchUpTicksThisIteration,
                        GC.CollectionCount(0) - gen0Before,
                        GC.CollectionCount(1) - gen1Before,
                        GC.CollectionCount(2) - gen2Before,
                        tickInterval);
                    nextTickAt += tickInterval;
                }

                await Task.Delay(1, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            QueueConnectedPlayersForPersistence();
            await _persistence.FlushAsync(CancellationToken.None);
            await _persistence.DisposeAsync();
            _syntheticLoad.Stop();
            _netManager.Stop();
            Log.Info("Server stopped.");
        }
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        request.AcceptIfKey(_options.ConnectionKey);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _sessions[peer] = new ClientSession(peer);
        _metrics.RecordPeerConnected();
        TrySend(peer, new ServerHelloMessage(ServerName, ProtocolCodec.Version, _options.TickRate, _options.StepCooldownMs, _options.InterestRadius), DeliveryMethod.ReliableOrdered);
        Log.Info($"Peer connected: {FormatPeer(peer)}.");
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (!_sessions.Remove(peer, out var session))
        {
            return;
        }

        if (session.IsAuthenticated)
        {
            if (_zone.Despawn(session.EntityId!.Value, out var entity))
            {
                _networkIds.Return(entity.NetworkId);
                QueueTileSave(session, entity.Tile);
            }
            else
            {
                _networkIds.Return(session.NetworkId);
                QueueTileSave(session);
            }
        }

        _metrics.RecordPeerDisconnected();
        Log.Info($"Peer disconnected: {FormatPeer(peer)}; reason={disconnectInfo.Reason}.");
    }

    private void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        if (_sessions.TryGetValue(peer, out var session))
        {
            session.LastLatencyMs = latency;
        }
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            var bytes = reader.GetRemainingBytes();
            var message = ProtocolCodec.Decode(bytes);
            _metrics.RecordReceived(message, bytes.Length);
            HandleMessage(peer, message);
        }
        catch (Exception exception)
        {
            _metrics.RecordBadPacket();
            var count = _sessions.TryGetValue(peer, out var session)
                ? session.RecordBadPacket()
                : MaxBadPacketsBeforeDisconnect;

            Log.Warn($"Failed to process packet from {FormatPeer(peer)}: {exception.Message}");
            if (count >= MaxBadPacketsBeforeDisconnect)
            {
                Log.Warn($"Disconnecting {FormatPeer(peer)} after {count} bad packets.");
                _netManager.DisconnectPeer(peer);
            }
            else
            {
                TrySend(peer, new ServerErrorMessage("bad_packet", "Bad packet."), DeliveryMethod.ReliableOrdered);
            }
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleMessage(NetPeer peer, IProtocolMessage message)
    {
        if (!_sessions.TryGetValue(peer, out var session))
        {
            return;
        }

        switch (message)
        {
            case ClientHelloMessage hello:
                Log.Info($"Client hello from {FormatPeer(peer)}: {hello.ClientName}.");
                break;
            case LoginRequestMessage login:
                BeginLogin(peer, session, login);
                break;
            case MoveStepMessage move:
                if (session.IsAuthenticated)
                {
                    var startedAt = Stopwatch.GetTimestamp();
                    try
                    {
                        if (TryGetSessionEntity(session, out var entity))
                        {
                            if (_zone.TryStep(entity, move.Direction, _serverTick, _options.StepCooldownTicks, out var result))
                            {
                                MarkDirtyDurableTile(entity);
                            }

                            _movementTrace.MoveStep(session, move.Sequence, result, _serverTick);
                        }
                    }
                    finally
                    {
                        _pendingMovementElapsedTicks += Stopwatch.GetTimestamp() - startedAt;
                    }
                }
                break;
            case ChatSendMessage chat:
                if (session.IsAuthenticated)
                {
                    HandleChat(session, chat.Text);
                }
                break;
            case SnapshotAckMessage ack:
                if (session.IsAuthenticated)
                {
                    session.AcknowledgeSnapshot(ack.LastSnapshotSequence);
                }
                break;
            default:
                TrySend(peer, new ServerErrorMessage("unsupported_message", $"Unsupported {message.Type}."), DeliveryMethod.ReliableOrdered);
                break;
        }
    }

    private void BeginLogin(NetPeer peer, ClientSession session, LoginRequestMessage login)
    {
        if (session.IsAuthenticated || session.LoginInProgress)
        {
            return;
        }

        session.LoginInProgress = true;
        var loginStartedAt = Stopwatch.GetTimestamp();
        _ = Task.Run(async () =>
        {
            try
            {
                var character = await _characters.LoadOrCreateAsync(login.AccountName, login.DisplayName, CancellationToken.None);
                _mainThreadActions.Enqueue(() =>
                {
                    if (!_sessions.TryGetValue(peer, out var current))
                    {
                        return;
                    }

                    var networkId = 0u;
                    try
                    {
                        var role = ResolveRole(login.AccountName, character.DisplayName);
                        var takeoverTile = KickExistingSessionForCharacter(current, character.CharacterId);
                        var loginTile = ResolveLoginTile(takeoverTile ?? character.Tile);
                        networkId = _networkIds.Rent();
                        var entity = _zone.SpawnPlayer(networkId, character.CharacterId, character.DisplayName, loginTile, current);
                        current.Authenticate(entity.NetworkId, character.CharacterId, character.DisplayName, role, character.ZoneId);
                        current.AttachEntity(entity);
                        _metrics.RecordLogin(true, Stopwatch.GetElapsedTime(loginStartedAt));
                        TrySend(peer, new LoginResultMessage(true, character.CharacterId, character.DisplayName, role, entity.Tile, ""), DeliveryMethod.ReliableOrdered);
                        TrySend(peer, CreateZoneInfoMessage(), DeliveryMethod.ReliableOrdered);
                        Log.Info($"Authenticated {character.DisplayName} ({character.CharacterId}) as {role}.");
                    }
                    catch (Exception exception)
                    {
                        _networkIds.Return(networkId);
                        current.LoginInProgress = false;
                        _metrics.RecordLogin(false, Stopwatch.GetElapsedTime(loginStartedAt));
                        TrySend(peer, new LoginResultMessage(false, Guid.Empty, login.DisplayName, ClientRole.Player, _zone.ResolveSpawnTile(Zone.DefaultSpawnTile), "No network id available."), DeliveryMethod.ReliableOrdered);
                        Log.Error("Login failed", exception);
                    }
                });
            }
            catch (Exception exception)
            {
                _mainThreadActions.Enqueue(() =>
                {
                    if (_sessions.TryGetValue(peer, out var current))
                    {
                        current.LoginInProgress = false;
                    }

                    _metrics.RecordLogin(false, Stopwatch.GetElapsedTime(loginStartedAt));
                    TrySend(peer, new LoginResultMessage(false, Guid.Empty, login.DisplayName, ClientRole.Player, _zone.ResolveSpawnTile(Zone.DefaultSpawnTile), exception.Message), DeliveryMethod.ReliableOrdered);
                    Log.Error("Login failed", exception);
                });
            }
        });
    }

    private TileCoord? KickExistingSessionForCharacter(ClientSession current, Guid characterId)
    {
        ClientSession? existing = null;
        foreach (var session in _sessions.Values)
        {
            if (ReferenceEquals(session, current) || !session.IsAuthenticated || session.CharacterId != characterId)
            {
                continue;
            }

            existing = session;
            break;
        }

        return existing is null
            ? null
            : KickAuthenticatedSession(existing, "logged_in_elsewhere", "Logged in elsewhere.");
    }

    private TileCoord? KickAuthenticatedSession(ClientSession session, string code, string message)
    {
        TrySend(session.Peer, new ServerErrorMessage(code, message), DeliveryMethod.ReliableOrdered);
        _sessions.Remove(session.Peer);
        _metrics.RecordPeerDisconnected();

        TileCoord? tile = null;
        if (session.EntityId.HasValue && _zone.Despawn(session.EntityId.Value, out var entity))
        {
            tile = entity.Tile;
            _networkIds.Return(entity.NetworkId);
            QueueTileSave(session, entity.Tile);
        }
        else
        {
            _networkIds.Return(session.NetworkId);
            QueueTileSave(session);
        }

        _netManager.DisconnectPeer(session.Peer);
        Log.Info($"Kicked {session.DisplayName}: {message}");
        return tile;
    }

    private void Tick(TickBudgetRecorder tickBudget)
    {
        _runtimeGuard.TryRun("tick", () => TickCore(tickBudget));
    }

    private void TickCore(TickBudgetRecorder tickBudget)
    {
        _serverTick++;
        tickBudget.RecordElapsed(TickBudgetCategory.Movement, DrainPendingMovementElapsedTicks());

        using (tickBudget.Measure(TickBudgetCategory.Persistence))
        {
            CheckpointDirtyDurableTiles();
        }

        BroadcastSnapshot(tickBudget);

        if (_serverTick % (uint)(_options.TickRate * 10) == 0)
        {
            using (tickBudget.Measure(TickBudgetCategory.Other))
            {
                Log.Info($"tick={_serverTick} peers={_sessions.Count} players={_sessions.Values.Count(x => x.IsAuthenticated)}");
            }
        }
    }

    private void BroadcastSnapshot(TickBudgetRecorder tickBudget)
    {
        _authenticatedScratch.Clear();
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated)
            {
                _authenticatedScratch.Add(session);
            }
        }

        _entityScratch.Clear();
        _zone.World.CopyEntitiesTo(_entityScratch);

        foreach (var session in _authenticatedScratch)
        {
            _runtimeGuard.TryRun($"snapshot for {session.DisplayName} #{session.NetworkId}", () => BroadcastSnapshotToSession(session, _entityScratch, tickBudget));
        }
    }

    private void BroadcastSnapshotToSession(ClientSession session, IReadOnlyCollection<WorldEntity> entities, TickBudgetRecorder tickBudget)
    {
        using (tickBudget.Measure(TickBudgetCategory.Aoi))
        {
            SelectVisibleEntities(session, entities, _visibleEntityScratch, _visibleNetworkIdScratch);
        }

        using (tickBudget.Measure(TickBudgetCategory.Network))
        {
            SendEntityDespawns(session, _visibleNetworkIdScratch);
            EnsureEntitySpawns(session, _visibleEntityScratch);
        }

        SendSnapshotPackets(session, _visibleEntityScratch, tickBudget, out var visibleCount, out var chunkCount, out var sentBytes, out var sentPackets);

        if (sentPackets > 0)
        {
            _metrics.RecordSnapshotSent(sentBytes, visibleCount, entities.Count);
        }

        if ((visibleCount < entities.Count || sentPackets > 1) && _serverTick % (uint)(_options.TickRate * 5) == 0)
        {
            Log.Info($"snapshot for {session.DisplayName}: visible={visibleCount}/{entities.Count}, radius={_options.InterestRadius:0.#}, chunks={sentPackets}/{chunkCount}, bytes={sentBytes}");
        }
    }

    private void SendEntityDespawns(ClientSession recipient, IReadOnlySet<uint> visibleIds)
    {
        _despawnScratch.Clear();
        recipient.CollectSnapshotEntitiesMissingFrom(visibleIds, _despawnScratch);
        foreach (var networkId in _despawnScratch)
        {
            TrySend(recipient.Peer, new EntityDespawnMessage(_serverTick, networkId), DeliveryMethod.ReliableOrdered);
            recipient.ForgetSentRevision(networkId);
        }
    }

    private void SelectVisibleEntities(
        ClientSession recipient,
        IReadOnlyCollection<WorldEntity> entities,
        List<WorldEntity> destination,
        HashSet<uint> visibleIds)
    {
        destination.Clear();
        visibleIds.Clear();
        _visibleCandidateScratch.Clear();

        if (!TryGetSessionEntity(recipient, out var recipientEntity))
        {
            return;
        }

        foreach (var candidate in entities)
        {
            var distanceSquared = DistanceSquared(recipientEntity, candidate);
            if (IsEntityInInterest(recipientEntity, candidate, recipient, _options.InterestRadius))
            {
                _visibleCandidateScratch.Add(new VisibleEntity(candidate, SnapshotSortKey(recipient, candidate, distanceSquared)));
            }
        }

        _visibleCandidateScratch.Sort(_visibleEntityComparer);
        var visibleCount = Math.Min(_visibleCandidateScratch.Count, _options.MaxVisibleEntities);
        for (var i = 0; i < visibleCount; i++)
        {
            var entity = _visibleCandidateScratch[i].Entity;
            destination.Add(entity);
            visibleIds.Add(entity.NetworkId);
        }
    }

    private void SendSnapshotPackets(
        ClientSession recipient,
        IReadOnlyList<WorldEntity> visible,
        TickBudgetRecorder tickBudget,
        out int visibleCount,
        out int chunkCount,
        out int sentBytes,
        out int sentPackets)
    {
        sentBytes = 0;
        sentPackets = 0;
        chunkCount = 0;
        if (!TryGetSessionEntity(recipient, out var recipientEntity))
        {
            visibleCount = 0;
            return;
        }

        var isComplete = recipient.ShouldSendFullSnapshot(_serverTick, SnapshotHeartbeatTicks());
        _payloadEntityScratch.Clear();
        foreach (var entity in visible)
        {
            if (isComplete || !recipient.HasSentRevision(entity))
            {
                _payloadEntityScratch.Add(entity);
            }
        }

        recipient.RememberSnapshotEntities(visible);

        if (_payloadEntityScratch.Count == 0)
        {
            visibleCount = visible.Count;
            return;
        }

        var maxEntitiesPerPacket = Math.Max(1, (MaxSequencedSnapshotBytes - ProtocolHeaderBytes - SnapshotHeaderBytes) / EstimateEntityStateBytes());
        chunkCount = Math.Max(1, (int)Math.Ceiling(_payloadEntityScratch.Count / (double)maxEntitiesPerPacket));
        var snapshotSequence = recipient.NextSnapshotSequence();

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            _snapshotChunkScratch.Clear();
            var start = chunkIndex * maxEntitiesPerPacket;
            var end = Math.Min(start + maxEntitiesPerPacket, _payloadEntityScratch.Count);
            for (var i = start; i < end; i++)
            {
                var entity = _payloadEntityScratch[i];
                _snapshotChunkScratch.Add(ToEntityStateSnapshot(entity));
                if (_movementTrace.Enabled)
                {
                    _movementTrace.SnapshotCarried(recipient, entity, snapshotSequence, _serverTick, chunkIndex, chunkCount);
                }
            }

            EncodedPacket packet;
            using (tickBudget.Measure(TickBudgetCategory.Serialize))
            {
                packet = _snapshotEncodeBuffer.Encode(new WorldSnapshotMessage(
                    _serverTick,
                    snapshotSequence,
                    visible.Count,
                    isComplete,
                    chunkIndex,
                    chunkCount,
                    _snapshotChunkScratch));
            }

            if (packet.Length > MaxSequencedSnapshotBytes)
            {
                Log.Warn($"Snapshot chunk exceeded budget for {recipient.DisplayName}: chunk={chunkIndex + 1}/{chunkCount}, bytes={packet.Length}.");
            }

            using (tickBudget.Measure(TickBudgetCategory.Network))
            {
                if (TrySend(recipient.Peer, packet.Buffer, packet.Length, DeliveryMethod.Unreliable))
                {
                    sentBytes += packet.Length;
                    sentPackets++;
                }
            }
        }

        foreach (var entity in _payloadEntityScratch)
        {
            recipient.RememberSentRevision(entity);
        }

        recipient.RememberSnapshotSent(_serverTick, isComplete);
        visibleCount = visible.Count;
    }

    private void EnsureEntitySpawns(ClientSession recipient, IReadOnlyCollection<WorldEntity> authenticated)
    {
        foreach (var entity in authenticated)
        {
            if (recipient.KnowsEntity(entity.NetworkId))
            {
                continue;
            }

            TrySend(recipient.Peer, new EntitySpawnMessage(
                entity.NetworkId,
                entity.CharacterId ?? Guid.Empty,
                entity.Kind,
                entity.DisplayName,
                entity.Tile,
                entity.Facing), DeliveryMethod.ReliableOrdered);
            recipient.RememberKnownEntity(entity.NetworkId);
        }
    }

    private static float SnapshotSortKey(ClientSession recipient, WorldEntity candidate, float distanceSquared)
    {
        if (recipient.EntityId.HasValue && candidate.Id == recipient.EntityId.Value)
        {
            return -1;
        }

        return recipient.WasInLastSnapshot(candidate.NetworkId)
            ? distanceSquared - SnapshotRetentionBonusDistanceSquared
            : distanceSquared;
    }

    private readonly record struct VisibleEntity(WorldEntity Entity, float SortKey);

    private sealed class VisibleEntityComparer : IComparer<VisibleEntity>
    {
        public int Compare(VisibleEntity x, VisibleEntity y)
        {
            var keyComparison = x.SortKey.CompareTo(y.SortKey);
            return keyComparison != 0
                ? keyComparison
                : x.Entity.Id.CompareTo(y.Entity.Id);
        }
    }

    private static float DistanceSquared(WorldEntity a, WorldEntity b)
    {
        var dx = b.Tile.X - a.Tile.X;
        var dy = b.Tile.Y - a.Tile.Y;
        return (dx * dx) + (dy * dy);
    }

    internal static bool IsEntityInInterest(
        WorldEntity recipientEntity,
        WorldEntity candidate,
        ClientSession recipient,
        float interestRadius)
    {
        if (candidate.Id == recipientEntity.Id)
        {
            return true;
        }

        var distanceSquared = DistanceSquared(recipientEntity, candidate);
        var radiusSquared = interestRadius * interestRadius;
        if (distanceSquared <= radiusSquared)
        {
            return true;
        }

        var exitRadius = interestRadius + InterestExitHysteresisTiles;
        var exitRadiusSquared = exitRadius * exitRadius;
        return recipient.WasInLastSnapshot(candidate.NetworkId) && distanceSquared <= exitRadiusSquared;
    }

    private static EntityStateSnapshot ToEntityStateSnapshot(WorldEntity entity)
    {
        return new EntityStateSnapshot(entity.NetworkId, entity.Tile, entity.Facing);
    }

    private static int EstimateEntityStateBytes()
    {
        return EntityStateFixedBytes;
    }

    private uint SnapshotHeartbeatTicks()
    {
        return (uint)Math.Max(1, _options.TickRate);
    }

    private bool TryGetSessionEntity(ClientSession session, out WorldEntity entity)
    {
        if (session.EntityId.HasValue)
        {
            return _zone.World.TryGet(session.EntityId.Value, out entity);
        }

        entity = null!;
        return false;
    }

    private TileCoord ResolveLoginTile(TileCoord tile)
    {
        return _zone.ResolvePlayerSpawnTile(tile);
    }

    private TileCoord ResolvePlaceholderEntityTile()
    {
        var preferred = _zone.SpawnTiles[0].Offset(2, 0);
        return _zone.ResolveSpawnTile(preferred);
    }

    private ZoneInfoMessage CreateZoneInfoMessage()
    {
        var blockedTiles = _zone.BlockedTiles
            .OrderBy(tile => tile.Y)
            .ThenBy(tile => tile.X)
            .ToArray();
        return new ZoneInfoMessage(_zone.Id, _zone.Width, _zone.Height, blockedTiles);
    }

    private void HandleChat(ClientSession sender, string text)
    {
        var safeText = text.Trim();
        if (safeText.StartsWith("/", StringComparison.Ordinal))
        {
            HandleCommand(sender, safeText);
            return;
        }

        BroadcastChat(sender, safeText);
    }

    private void BroadcastChat(ClientSession sender, string text)
    {
        var safeText = text.Trim();
        if (safeText.Length == 0)
        {
            return;
        }

        if (safeText.Length > 240)
        {
            safeText = safeText[..240];
        }

        var broadcast = new ChatBroadcastMessage(sender.DisplayName, safeText);
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated && session.ZoneId == sender.ZoneId)
            {
                TrySend(session.Peer, broadcast, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void HandleCommand(ClientSession sender, string commandLine)
    {
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts.Length == 0 ? "" : parts[0].TrimStart('/').ToLowerInvariant();

        if (command is "help" or "?")
        {
            SendSystem(sender, sender.Role == ClientRole.Admin
                ? "commands: /help, /role, /who, /metrics, /stress, /stress status, /stress start [clients] [duration], /stress stop"
                : "commands: /help, /role. Admin commands require role Admin.");
            return;
        }

        if (command == "role")
        {
            SendSystem(sender, $"role: {sender.Role}");
            return;
        }

        if (sender.Role != ClientRole.Admin)
        {
            SendSystem(sender, "command denied: role Admin required.");
            Log.Warn($"Denied command from {sender.DisplayName}: {commandLine}");
            return;
        }

        switch (command)
        {
            case "who":
                SendSystem(sender, FormatWho());
                break;
            case "metrics":
                SendSystem(sender, _metrics.FormatStateSummary(
                    _sessions.Count,
                    _sessions.Values.Count(session => session.IsAuthenticated),
                    _serverTick,
                    _syntheticLoad.Status()));
                SendSystem(sender, _metrics.FormatWindowSummary(TimeSpan.FromSeconds(5)));
                SendSystem(sender, _metrics.FormatWindowSummary(TimeSpan.FromSeconds(60)));
                SendSystem(sender, _metrics.FormatTotalSummary());
                SendSystem(sender, _metrics.FormatMessageSummary());
                break;
            case "stress":
                HandleStressCommand(sender, parts);
                break;
            default:
                SendSystem(sender, $"unknown command: /{command}. Try /help.");
                break;
        }
    }

    private void HandleStressCommand(ClientSession sender, string[] parts)
    {
        var subcommand = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "start";
        switch (subcommand)
        {
            case "status":
                SendSystem(sender, _syntheticLoad.Status());
                break;
            case "stop":
                SendSystem(sender, _syntheticLoad.Stop());
                Log.Info($"{sender.DisplayName} stopped synthetic load.");
                break;
            case "start":
                StartSyntheticLoad(sender, parts);
                break;
            default:
                SendSystem(sender, $"usage: /stress | /stress status | /stress start [clients] [duration] | /stress stop. Default: /stress start {DefaultStressClientCount} {FormatDuration(DefaultStressDuration)}.");
                break;
        }
    }

    private void StartSyntheticLoad(ClientSession sender, string[] parts)
    {
        const int maxClients = 200;
        var clientCount = parts.Length >= 3 && int.TryParse(parts[2], out var parsedCount)
            ? parsedCount
            : DefaultStressClientCount;
        clientCount = Math.Clamp(clientCount, 1, maxClients);

        var duration = parts.Length >= 4 && TryParseDuration(parts[3], out var parsedDuration)
            ? parsedDuration
            : DefaultStressDuration;
        duration = ClampDuration(duration, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));

        _syntheticLoad.Start(clientCount, duration, _options.Port, _options.ConnectionKey);
        SendSystem(sender, $"stress started: clients={clientCount}, duration={FormatDuration(duration)}.");
        Log.Info($"{sender.DisplayName} started synthetic load: clients={clientCount}, duration={FormatDuration(duration)}.");
    }

    private string FormatWho()
    {
        var players = _sessions.Values
            .Where(session => session.IsAuthenticated)
            .OrderByDescending(session => session.Role)
            .ThenBy(session => session.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(session => $"{session.DisplayName}({session.Role}, {session.LastLatencyMs}ms)")
            .ToArray();

        return players.Length == 0
                ? "who: no authenticated players."
                : $"who: {string.Join(", ", players)}";
    }

    private void SendSystem(ClientSession session, string text)
    {
        TrySend(session.Peer, new ChatBroadcastMessage("server", text), DeliveryMethod.ReliableOrdered);
    }

    private ClientRole ResolveRole(string accountName, string displayName)
    {
        return _options.AdminNames.Contains(accountName) || _options.AdminNames.Contains(displayName)
            ? ClientRole.Admin
            : ClientRole.Player;
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        value = value.Trim();
        var lower = value.ToLowerInvariant();

        if (lower.EndsWith("ms", StringComparison.Ordinal)
            && double.TryParse(lower[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            duration = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        if (lower.EndsWith("s", StringComparison.Ordinal)
            && double.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (lower.EndsWith("m", StringComparison.Ordinal)
            && double.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            duration = TimeSpan.FromMinutes(minutes);
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bareSeconds))
        {
            duration = TimeSpan.FromSeconds(bareSeconds);
            return true;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
        {
            return true;
        }

        duration = TimeSpan.Zero;
        return false;
    }

    private static TimeSpan ClampDuration(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:0.#}s"
            : $"{duration.TotalMinutes:0.#}m";
    }

    private bool TrySend(NetPeer peer, IProtocolMessage message, DeliveryMethod deliveryMethod)
    {
        try
        {
            var packet = _messageEncodeBuffer.Encode(message);
            peer.Send(packet.Buffer.AsSpan(0, packet.Length), 0, deliveryMethod);
            _metrics.RecordSent(message, packet.Length);
            return true;
        }
        catch (Exception exception)
        {
            _metrics.RecordSendFailure();
            Log.Warn($"Failed to send {message.Type} to {FormatPeer(peer)}: {exception.Message}");
            return false;
        }
    }

    private bool TrySend(NetPeer peer, byte[] packet, int length, DeliveryMethod deliveryMethod)
    {
        try
        {
            peer.Send(packet.AsSpan(0, length), 0, deliveryMethod);
            return true;
        }
        catch (Exception exception)
        {
            _metrics.RecordSendFailure();
            Log.Warn($"Failed to send {length} bytes to {FormatPeer(peer)}: {exception.Message}");
            return false;
        }
    }

    private static string FormatPeer(NetPeer peer)
    {
        return $"{peer.Address}:{peer.Port}";
    }

    private static int CountDueTicks(DateTimeOffset now, DateTimeOffset nextTickAt, TimeSpan tickInterval)
    {
        var count = 0;
        var dueAt = nextTickAt;
        while (now >= dueAt)
        {
            count++;
            dueAt += tickInterval;
        }

        return count;
    }

    private void DrainMainThreadActions()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Log.Error("Main-thread action failed", exception);
            }
        }
    }

    private long DrainPendingMovementElapsedTicks()
    {
        var elapsedTicks = _pendingMovementElapsedTicks;
        _pendingMovementElapsedTicks = 0;
        return elapsedTicks;
    }

    private void MarkDirtyDurableTile(WorldEntity entity)
    {
        if (!entity.IsDurable || !entity.CharacterId.HasValue)
        {
            return;
        }

        _dirtyDurableTiles[entity.CharacterId.Value] = new PendingTileSave(
            entity.CharacterId.Value,
            entity.DisplayName,
            entity.Tile);
    }

    private void CheckpointDirtyDurableTiles()
    {
        if (_serverTick < _nextPersistenceCheckpointTick)
        {
            return;
        }

        _nextPersistenceCheckpointTick = _serverTick + _options.PersistenceCheckpointTicks;
        if (_dirtyDurableTiles.Count == 0)
        {
            return;
        }

        foreach (var save in _dirtyDurableTiles.Values)
        {
            _persistence.EnqueueTile(save.CharacterId, save.DisplayName, save.Tile);
        }

        _dirtyDurableTiles.Clear();
    }

    private void QueueConnectedPlayersForPersistence()
    {
        foreach (var session in _sessions.Values.Where(session => session.IsAuthenticated))
        {
            QueueTileSave(session);
        }
    }

    private void QueueTileSave(ClientSession session)
    {
        if (TryGetSessionEntity(session, out var entity))
        {
            QueueTileSave(session, entity.Tile);
        }
    }

    private void QueueTileSave(ClientSession session, TileCoord tile)
    {
        _dirtyDurableTiles.Remove(session.CharacterId);
        _persistence.EnqueueTile(session.CharacterId, session.DisplayName, tile);
    }

    private readonly record struct PendingTileSave(Guid CharacterId, string DisplayName, TileCoord Tile);
}

internal readonly record struct EncodedPacket(byte[] Buffer, int Length);

internal sealed class ProtocolEncodeBuffer
{
    private readonly MemoryStream _stream;
    private readonly BinaryWriter _writer;

    public ProtocolEncodeBuffer(int initialCapacity = 1500)
    {
        _stream = new MemoryStream(initialCapacity);
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    public EncodedPacket Encode(IProtocolMessage message)
    {
        _stream.Position = 0;
        _stream.SetLength(0);
        ProtocolCodec.Encode(message, _writer);
        _writer.Flush();
        return new EncodedPacket(_stream.GetBuffer(), checked((int)_stream.Length));
    }
}
