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
    private const int EntityStateFixedBytes = 8;
    private const int MaxBadPacketsBeforeDisconnect = 5;
    private const int DefaultStressClientCount = 120;
    private static readonly TimeSpan DefaultStressDuration = TimeSpan.FromSeconds(60);
    private const float InterestExitHysteresisTiles = 1f;
    private const float SnapshotRetentionBonusDistanceSquared = 144f;

    // Effective per-entity step cooldown is clamped to this ms range — the same [50, 5000] ms bounds the
    // global StepCooldownMs is validated against — so an arbitrary SpeedMultiplier (e.g. /speed 0.001 or
    // /speed 10000) can never produce a degenerate cadence that stalls or floods the tick loop. (S51)
    private const int MinEffectiveStepCooldownMs = 50;
    private const int MaxEffectiveStepCooldownMs = 5000;

    // Keepalive safety timeout for held movement intents (~1 s). The client resends its current intent
    // every ~500 ms; if a "moving" session goes silent for longer than this (a wedged-but-connected
    // client), the tick loop clears its intent so it stops walking. A real disconnect already despawns
    // the entity, so this only guards the wedged-client edge case. See docs/movement-input-model.md.
    private static readonly TimeSpan MoveIntentKeepaliveTimeout = TimeSpan.FromSeconds(1);

    private readonly ServerOptions _options;
    private readonly ICharacterRepository _characters;
    private readonly ItemRegistry _itemRegistry = ItemRegistry.Default;
    private readonly ResourceNodeRegistry _resourceNodes;
    private readonly PersistenceWriteBehindWorker _persistence;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _netManager;
    private readonly Dictionary<NetPeer, ClientSession> _sessions = new();
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly SyntheticClientLoad _syntheticLoad = new();
    private readonly ServerMetrics _metrics = new();
    private readonly ServerRuntimeGuard _runtimeGuard;
    private readonly ServerMovementTrace _movementTrace;
    private readonly ServerCadenceTrace _cadenceTrace = ServerCadenceTrace.FromEnvironment();
    private readonly NetworkIdPool _networkIds = new();
    private readonly Zone _zone;
    private readonly List<ClientSession> _authenticatedScratch = [];
    private readonly List<WorldEntity> _entityScratch = [];
    private readonly List<WorldEntity> _aoiCandidateScratch = [];
    private readonly List<WorldEntity> _aoiInteractCandidateScratch = [];
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
    // Depleted-only respawn schedule: per tick only nodes whose respawn time has arrived are processed,
    // so respawn work is O(depleted) regardless of how many available nodes the scatter placed. The placed
    // node entities themselves live in the zone's WorldState (replicated by AOI); only depleted ones are
    // tracked here.
    private readonly ResourceRespawnSchedule _resourceRespawns = new();

    // Half-extent (in tiles) of the cell neighborhood an AOI query must examine. The grid returns every
    // entity in the cells overlapping [viewer ± this], and the per-entity interest test then filters to
    // the exact set. It MUST cover the interest EXIT radius (interest radius + hysteresis), so a
    // hysteresis-retained entity sitting between the entry and exit radius is never dropped — dropping
    // one would be both a visible bug and an anti-cheat hole. Computed once from the configured radius.
    private readonly int _aoiQueryRadiusTiles;

    private uint _serverTick;
    private uint _nextPersistenceCheckpointTick;
    private long _pendingMovementElapsedTicks;
    private long _traceStartTimestamp;
    private int _snapshotsSentThisTick;

    public GameServer(ServerOptions options, ICharacterRepository characters)
    {
        _options = options;
        _aoiQueryRadiusTiles = ResolveAoiQueryRadiusTiles(options.InterestRadius);
        _characters = characters;
        _persistence = new PersistenceWriteBehindWorker(characters);
        _nextPersistenceCheckpointTick = options.PersistenceCheckpointTicks;
        _runtimeGuard = new ServerRuntimeGuard(_metrics);
        _movementTrace = new ServerMovementTrace(options);
        _resourceNodes = ResourceNodeRegistry.CreateDefault(_itemRegistry);
        _zone = Zone.CreateGenerated(
            options.WorldWidthTiles,
            options.WorldHeightTiles,
            options.MapSeed,
            TerrainGenerator.CurrentGenVersion,
            options.SpawnDistribution,
            ResolveEntityGridCellSize(options.InterestRadius));
        ScatterResourceNodes();
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
        using var timerResolution = WindowsTimerResolutionScope.Begin();
        if (timerResolution.IsActive)
        {
            Log.Info("Enabled Windows timer resolution: 1ms.");
        }
        else if (OperatingSystem.IsWindows())
        {
            Log.Warn($"Windows timer resolution request failed: result={timerResolution.BeginResult}.");
        }

        _netManager.Start(_options.Port);
        Log.Info($"Server listening on UDP {_options.Port}.");
        if (_cadenceTrace.Enabled)
        {
            Log.Info("Server cadence trace enabled: writing .run/server-cadence.csv and .run/server-steps.csv.");
        }

        _traceStartTimestamp = Stopwatch.GetTimestamp();
        var tickIntervalTimestampTicks = PreciseTickScheduler.TickIntervalTimestampTicks(_options.TickRate);
        var tickInterval = PreciseTickScheduler.ToTimeSpan(tickIntervalTimestampTicks);
        var nextTickAt = Stopwatch.GetTimestamp();
        var lastTickStartedAt = 0L;
        var tickBudget = new TickBudgetRecorder();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _netManager.PollEvents();
                _syntheticLoad.Poll();
                DrainMainThreadActions();

                var now = Stopwatch.GetTimestamp();
                var catchUpTicksThisIteration = PreciseTickScheduler.CountDueTicks(now, nextTickAt, tickIntervalTimestampTicks);
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
                    tickBudget.Reset();
                    var scheduleDrift = Stopwatch.GetElapsedTime(nextTickAt, now);
                    Tick(tickBudget);
                    var tickDuration = Stopwatch.GetElapsedTime(tickStartedAt);
                    var budgetSample = tickBudget.ToSample();
                    var gcSample = new GcCollectionSample(
                        GC.CollectionCount(0) - gen0Before,
                        GC.CollectionCount(1) - gen1Before,
                        GC.CollectionCount(2) - gen2Before);
                    _metrics.RecordTick(tickDuration, scheduleDrift, budgetSample, gcSample);
                    _movementTrace.TickHitch(
                        _serverTick,
                        interTickGap,
                        tickDuration,
                        scheduleDrift,
                        budgetSample,
                        catchUpTicksThisIteration,
                        gcSample,
                        tickInterval);
                    nextTickAt += tickIntervalTimestampTicks;
                }

                await PreciseTickScheduler.WaitUntilNextTickOrPollAsync(nextTickAt, cancellationToken);
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
            _cadenceTrace.Flush();
            _cadenceTrace.Dispose();
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
                FlushInventory(entity);
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
            case MoveIntentMessage intent:
                if (session.IsAuthenticated)
                {
                    // Input is state, not events: record the held intent (rejecting stale sequences) and
                    // let TickCore step the entity at the server's own cooldown cadence. No stepping
                    // happens on the receive path anymore. See docs/movement-input-model.md.
                    session.TryUpdateMoveIntent(intent.Sequence, intent.Moving, intent.Direction, _serverTick);
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
                    session.AcknowledgeSnapshot(ack.LastSnapshotSequence, _serverTick);
                }
                break;
            case InteractRequestMessage interact:
                if (session.IsAuthenticated)
                {
                    HandleInteract(session, interact.TargetNetworkId);
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
                var items = await _characters.LoadItemsAsync(character.CharacterId, CancellationToken.None);
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
                        var takeover = KickExistingSessionForCharacter(current, character.CharacterId);
                        var loginTile = ResolveLoginTile(takeover.Tile ?? character.Tile);
                        networkId = _networkIds.Rent();
                        // On account takeover, hand off the kicked session's in-memory Inventory (which may
                        // hold harvested items not yet flushed to the DB) instead of the DB-loaded stacks
                        // read at the start of this login. Without this, a mid-session relogin reloads the
                        // pre-harvest inventory and can later overwrite the kicked session's flushed gains.
                        var inventory = takeover.Inventory ?? new Inventory(_itemRegistry, items);
                        var entity = _zone.SpawnPlayer(networkId, character.CharacterId, character.DisplayName, loginTile, current, inventory);
                        current.Authenticate(entity.NetworkId, character.CharacterId, character.DisplayName, role, character.ZoneId);
                        current.AttachEntity(entity);
                        _metrics.RecordLogin(true, Stopwatch.GetElapsedTime(loginStartedAt));
                        TrySend(peer, new LoginResultMessage(true, character.CharacterId, character.DisplayName, role, entity.Tile, ""), DeliveryMethod.ReliableOrdered);
                        TrySend(peer, CreateZoneInfoMessage(), DeliveryMethod.ReliableOrdered);
                        // Send a full inventory snapshot so the client panel reflects the persisted (or
                        // handed-off, on takeover) contents immediately — otherwise it stays empty until the
                        // next harvest delta. Covers both login paths: `inventory` is whatever the entity was
                        // spawned with. Snapshot() yields only non-empty stacks; skip the send entirely when
                        // the inventory is empty — a fresh character's panel is already empty, so an empty
                        // update is a pointless packet.
                        var snapshot = inventory.Snapshot();
                        if (snapshot.Count > 0)
                        {
                            SendInventoryUpdate(current, snapshot);
                        }
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

    private TakeoverState KickExistingSessionForCharacter(ClientSession current, Guid characterId)
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
            ? default
            : KickAuthenticatedSession(existing, "logged_in_elsewhere", "Logged in elsewhere.");
    }

    private TakeoverState KickAuthenticatedSession(ClientSession session, string code, string message)
    {
        TrySend(session.Peer, new ServerErrorMessage(code, message), DeliveryMethod.ReliableOrdered);
        _sessions.Remove(session.Peer);
        _metrics.RecordPeerDisconnected();

        TileCoord? tile = null;
        Inventory? inventory = null;
        if (session.EntityId.HasValue && _zone.Despawn(session.EntityId.Value, out var entity))
        {
            tile = entity.Tile;
            // Hand the live in-memory inventory to the taking-over login so any not-yet-flushed harvest
            // gains survive the relogin. FlushInventory still enqueues its dirty changes for persistence;
            // the quantities live on this same object, so nothing is lost either way.
            inventory = entity.Inventory;
            _networkIds.Return(entity.NetworkId);
            QueueTileSave(session, entity.Tile);
            FlushInventory(entity);
        }
        else
        {
            _networkIds.Return(session.NetworkId);
            QueueTileSave(session);
        }

        _netManager.DisconnectPeer(session.Peer);
        Log.Info($"Kicked {session.DisplayName}: {message}");
        return new TakeoverState(tile, inventory);
    }

    private void Tick(TickBudgetRecorder tickBudget)
    {
        _runtimeGuard.TryRun("tick", () => TickCore(tickBudget));
    }

    private void TickCore(TickBudgetRecorder tickBudget)
    {
        _serverTick++;

        // Held-direction movement (protocol v15): step each session's entity at the server's own cooldown
        // cadence from its held intent, instead of acting on per-step client messages. DrainPending... is
        // kept for any movement work still timed off the tick thread (currently none → 0).
        tickBudget.RecordElapsed(TickBudgetCategory.Movement, DrainPendingMovementElapsedTicks());
        using (tickBudget.Measure(TickBudgetCategory.Movement))
        {
            StepHeldMovementIntents();
        }

        using (tickBudget.Measure(TickBudgetCategory.Other))
        {
            RespawnResourceNodes();
        }

        using (tickBudget.Measure(TickBudgetCategory.Persistence))
        {
            CheckpointDirtyDurableState();
        }

        BroadcastSnapshot(tickBudget);

        if (_cadenceTrace.Enabled)
        {
            _cadenceTrace.RecordTick(
                _serverTick,
                Stopwatch.GetElapsedTime(_traceStartTimestamp),
                _entityScratch,
                _snapshotsSentThisTick);
        }
    }

    private void BroadcastSnapshot(TickBudgetRecorder tickBudget)
    {
        _snapshotsSentThisTick = 0;
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
            TryBroadcastSnapshotToSession(session, _entityScratch, tickBudget);
        }
    }

    private void TryBroadcastSnapshotToSession(ClientSession session, IReadOnlyCollection<WorldEntity> entities, TickBudgetRecorder tickBudget)
    {
        try
        {
            BroadcastSnapshotToSession(session, entities, tickBudget);
        }
        catch (Exception exception)
        {
            _metrics.RecordRuntimeFault();
            Log.Error($"Runtime fault in snapshot for {session.DisplayName} #{session.NetworkId}.", exception);
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
            _snapshotsSentThisTick++;
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
            var packet = _messageEncodeBuffer.EncodeEntityDespawn(_serverTick, networkId);
            TrySend(recipient.Peer, packet, DeliveryMethod.ReliableOrdered, MessageType.EntityDespawn);
            recipient.ForgetEntityBaseline(networkId);
            recipient.ForgetKnownEntity(networkId);
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

        // Spatial-index candidate gather (S41): instead of scanning every world entity, query only the
        // cells overlapping the viewer's interest box. The grid returns a SUPERSET of the in-interest set
        // (it covers the exit/hysteresis radius), so applying the exact same IsEntityInInterest test to
        // the candidates below yields a result IDENTICAL to the old full scan. `entities.Count` (the total
        // world entity count) still drives the cap branch exactly as before.
        _zone.World.GatherInterestCandidates(recipientEntity.Tile, _aoiQueryRadiusTiles, _aoiCandidateScratch);

        if (_options.MaxVisibleEntities >= entities.Count)
        {
            foreach (var candidate in _aoiCandidateScratch)
            {
                if (IsEntityInInterest(recipientEntity, candidate, recipient, _options.InterestRadius))
                {
                    destination.Add(candidate);
                    visibleIds.Add(candidate.NetworkId);
                }
            }

            return;
        }

        foreach (var candidate in _aoiCandidateScratch)
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

        // Acked-baseline selection (S46): send an entity iff its current revision differs from the revision
        // the CLIENT has acknowledged. A dropped snapshot is never acked, so its entities stay unacked and
        // are re-included next tick → self-healing under loss, no periodic full heartbeat needed. A viewer
        // gone silent past the safety threshold is force-rebaselined first (acked map cleared) so per-viewer
        // pending state cannot grow without bound; a longer threshold disconnects a wedged client.
        ApplyUnackedSafetyBound(recipient);

        _payloadEntityScratch.Clear();
        foreach (var entity in visible)
        {
            if (!recipient.HasAckedCurrentRevision(entity))
            {
                _payloadEntityScratch.Add(entity);
            }
        }

        recipient.RememberSnapshotEntities(visible);

        if (_payloadEntityScratch.Count == 0)
        {
            // Per-recipient delta is empty (static AOI: nothing changed, entered, or left). With the periodic
            // full heartbeat gone (S46), sending nothing here would leave an idle-but-healthy viewer with no
            // snapshot to ack, so its ack-silence clock would grow until the disconnect bound wrongly dropped
            // it (~8 s on a perfect connection). Emit a low-rate EMPTY keep-alive instead: a tiny snapshot
            // carrying no entity states (isComplete=false so it does NOT reconcile/prune the client's visible
            // set) just so the client acks the sequence and stays alive. Tiny + low-rate → the dense-bandwidth
            // win (no full resend) is preserved.
            if (recipient.ShouldSendKeepAlive(_serverTick, SnapshotKeepAliveTicks))
            {
                SendKeepAliveSnapshot(recipient, visible.Count, tickBudget, ref sentBytes, ref sentPackets);
            }

            visibleCount = visible.Count;
            return;
        }

        // A snapshot is "complete" (the client may reconcile/prune to it) iff its payload carries every
        // currently-visible entity — i.e. nothing was omitted as already-acked. Re-baselines and the very
        // first snapshot are complete; an incremental delta is not (the client relies on reliable
        // EntityDespawn for AOI-exit removals, exactly as before).
        var isComplete = _payloadEntityScratch.Count == visible.Count;

        var maxEntitiesPerPacket = Math.Max(1, (MaxSequencedSnapshotBytes - ProtocolHeaderBytes - SnapshotHeaderBytes) / EstimateEntityStateBytes());
        chunkCount = Math.Max(1, (int)Math.Ceiling(_payloadEntityScratch.Count / (double)maxEntitiesPerPacket));
        var snapshotSequence = recipient.NextSnapshotSequence();

        // One per-seq carried record for the whole sequence (all chunks): the client acks the sequence only
        // once every chunk of it is received, so the baseline advances for the full payload atomically.
        var pending = recipient.BeginPendingSnapshot(snapshotSequence, _serverTick);
        foreach (var entity in _payloadEntityScratch)
        {
            pending.Add(entity.NetworkId, entity.StateRevision);
        }

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
                packet = _snapshotEncodeBuffer.EncodeWorldSnapshot(
                    _serverTick,
                    snapshotSequence,
                    visible.Count,
                    isComplete,
                    chunkIndex,
                    chunkCount,
                    _snapshotChunkScratch);
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

        // No "remember sent" baseline step: the acked baseline advances only on ACK (AcknowledgeSnapshot),
        // which is what makes a dropped snapshot self-heal — its carried entities stay unacked and re-send
        // next tick. We DO reset the keep-alive cadence clock, so a real delta defers the next empty
        // keep-alive: the keep-alive only fires after SnapshotKeepAliveTicks of NO snapshot at all.
        recipient.RememberSnapshotSentTick(_serverTick);
        visibleCount = visible.Count;
    }

    // Sends a single empty keep-alive WorldSnapshot to a viewer whose per-recipient delta is empty, so it
    // keeps acking and never trips the disconnect bound. The payload is empty and isComplete is FALSE — an
    // empty complete snapshot would tell the client to prune its entire visible set, so the keep-alive must
    // be non-complete to leave the client's known entities untouched. It opens NO pending record (carries no
    // entities, so there is nothing to baseline) but advances the cadence + disconnect clocks via
    // RememberSnapshotSentTick. The client acks it like any snapshot, refreshing its silence clock on ack.
    private void SendKeepAliveSnapshot(
        ClientSession recipient,
        int totalVisible,
        TickBudgetRecorder tickBudget,
        ref int sentBytes,
        ref int sentPackets)
    {
        var snapshotSequence = recipient.NextSnapshotSequence();
        _snapshotChunkScratch.Clear();

        EncodedPacket packet;
        using (tickBudget.Measure(TickBudgetCategory.Serialize))
        {
            packet = _snapshotEncodeBuffer.EncodeWorldSnapshot(
                _serverTick,
                snapshotSequence,
                totalVisible,
                isComplete: false,
                chunkIndex: 0,
                chunkCount: 1,
                _snapshotChunkScratch);
        }

        using (tickBudget.Measure(TickBudgetCategory.Network))
        {
            if (TrySend(recipient.Peer, packet.Buffer, packet.Length, DeliveryMethod.Unreliable))
            {
                sentBytes += packet.Length;
                sentPackets++;
            }
        }

        recipient.RememberSnapshotSentTick(_serverTick);
    }

    private void EnsureEntitySpawns(ClientSession recipient, IReadOnlyCollection<WorldEntity> authenticated)
    {
        foreach (var entity in authenticated)
        {
            if (recipient.KnowsEntity(entity.NetworkId))
            {
                continue;
            }

            var packet = _messageEncodeBuffer.EncodeEntitySpawn(
                entity.NetworkId,
                entity.CharacterId ?? Guid.Empty,
                entity.Kind,
                entity.DisplayName,
                entity.Tile,
                entity.Facing,
                EffectiveStepCooldownMs(entity));
            TrySend(recipient.Peer, packet, DeliveryMethod.ReliableOrdered, MessageType.EntitySpawn);
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

    // Tile half-extent an AOI grid query must cover: the interest EXIT radius (interest radius + the
    // hysteresis margin), rounded UP so the integer cell box fully contains the float exit circle. Any
    // entity that could pass IsEntityInInterest lies within this Chebyshev box of the viewer, so querying
    // it guarantees the grid returns a superset of the full-scan result.
    private static int ResolveAoiQueryRadiusTiles(float interestRadius)
    {
        return (int)Math.Ceiling(interestRadius + InterestExitHysteresisTiles);
    }

    // Spatial-index cell size (tiles): one cell ≈ the interest box, so a viewer query touches a small
    // fixed neighborhood (~3×3 cells) regardless of world size. Derived from the interest radius and
    // clamped to >= 1. Pure performance knob — correctness is independent of it (the query expands the
    // cell box to cover the exit radius for whatever cell size is chosen).
    private static int ResolveEntityGridCellSize(float interestRadius)
    {
        return Math.Max(1, (int)Math.Ceiling(interestRadius));
    }

    private static EntityStateSnapshot ToEntityStateSnapshot(WorldEntity entity)
    {
        return new EntityStateSnapshot(entity.NetworkId, entity.Tile, entity.Facing, entity.IsDepleted);
    }

    private static int EstimateEntityStateBytes()
    {
        return EntityStateFixedBytes;
    }

    // Safety bound for the acked baseline (S46). A healthy client acks every snapshot within ~1 RTT, so the
    // oldest-unacked age stays tiny. A wedged/silent-but-connected client never acks; without a bound its
    // per-viewer pending-snapshot ring would grow each tick. Two thresholds, both in ticks:
    //  - RebaselineTicks (~2 s): clear the acked baseline and re-send a complete snapshot. Cheap recovery
    //    if the client is merely lagging its acks; also bounds the pending ring (it is cleared here).
    //  - DisconnectTicks (~8 s): the client is genuinely wedged; drop it. (LiteNetLib's own 15 s
    //    DisconnectTimeout still covers a fully dead peer; this is the faster app-level bound.)
    private uint RebaselineUnackedThresholdTicks => (uint)Math.Max(1, _options.TickRate * 2);
    private uint DisconnectUnackedThresholdTicks => (uint)Math.Max(1, _options.TickRate * 8);

    // Low-rate EMPTY keep-alive cadence (~1 s): the longest a viewer with an empty per-recipient delta goes
    // without being sent SOME snapshot to ack. Well under the 2 s re-baseline / 8 s disconnect bounds, so an
    // idle-but-healthy viewer keeps acking and never trips them. One tiny empty packet/sec/idle-viewer is
    // negligible next to the dense-scene resend the acked baseline removed.
    private uint SnapshotKeepAliveTicks => (uint)Math.Max(1, _options.TickRate);

    private void ApplyUnackedSafetyBound(ClientSession recipient)
    {
        // Disconnect bound first: measured from the last ack (non-resetting), so a client that keeps being
        // re-baselined but never acks is still dropped once total silence passes the larger threshold.
        var silence = recipient.SilenceTicks(_serverTick);
        if (silence >= DisconnectUnackedThresholdTicks)
        {
            Log.Warn($"Disconnecting {recipient.DisplayName} #{recipient.NetworkId}: no snapshot ack for {silence} ticks.");
            _netManager.DisconnectPeer(recipient.Peer);
            recipient.ForceFullRebaseline();
            return;
        }

        // Cheap re-baseline bound: if the pending ring has been growing for the smaller threshold, clear the
        // acked baseline and re-send a complete snapshot. Bounds the per-viewer pending state.
        if (recipient.UnackedAgeTicks(_serverTick) >= RebaselineUnackedThresholdTicks)
        {
            recipient.ForceFullRebaseline();
        }
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

    // Deterministically scatters harvestable nodes of each registered type across the whole walkable map
    // (Zone owns the placement algorithm + min spacing; see PlanResourceNodeScatter). Server-owned world
    // entities, not session-derived; their transient state is server-memory only and respawns fresh on
    // restart — but the placement is deterministic from the map seed, so the same layout regenerates.
    private void ScatterResourceNodes()
    {
        foreach (var (definition, tile) in
            _zone.PlanResourceNodeScatter(_resourceNodes, _options.ResourceNodeDensityTilesPerNode))
        {
            _zone.SpawnResourceNode(_networkIds.Rent(), definition, tile);
        }
    }

    // O(depleted) respawn: the schedule pops only nodes whose respawn tick has arrived and flips them back
    // to Available; StateRevision is already bumped by TryRespawnResource so the refreshed availability
    // re-replicates by AOI (no extra work needed in the callback). Still-available nodes are never visited.
    private void RespawnResourceNodes()
    {
        _resourceRespawns.DrainDue(_serverTick, static _ => { });
    }

    // Server-authoritative resolution of a generic Interact verb. Harvest is the only dispatch target
    // for now: validate authentication, that the target is a visible-and-adjacent harvestable resource
    // node, and that the node is Available; on success grant the yield through the inventory service,
    // deplete the node, and reply to the owner. Every failure path replies with a reason and changes no
    // state. Rate-limited like other client input (a node is depleted for its respawn window, so spam
    // is naturally bounded, but we also guard against per-tick floods).
    private void HandleInteract(ClientSession session, uint targetNetworkId)
    {
        if (!session.TryConsumeInteract(_serverTick))
        {
            SendInteractResult(session, false, "rate_limited");
            return;
        }

        if (!TryGetSessionEntity(session, out var actor))
        {
            SendInteractResult(session, false, "no_actor");
            return;
        }

        if (!TryFindVisibleEntity(session, actor, targetNetworkId, out var target))
        {
            SendInteractResult(session, false, "no_target");
            return;
        }

        if (target.Resource is null)
        {
            SendInteractResult(session, false, "not_resource");
            return;
        }

        if (!IsAdjacent(actor.Tile, target.Tile))
        {
            SendInteractResult(session, false, "too_far");
            return;
        }

        if (!target.Resource.IsAvailable)
        {
            SendInteractResult(session, false, "depleted");
            return;
        }

        if (actor.Inventory is null)
        {
            SendInteractResult(session, false, "no_inventory");
            return;
        }

        var definition = target.Resource.Definition;
        var added = actor.Inventory.TryAdd(definition.YieldItemKey, definition.YieldQuantity);
        if (added <= 0)
        {
            // Inventory full for this item (or unknown yield): do not deplete a node for nothing.
            SendInteractResult(session, false, "inventory_full");
            return;
        }

        target.DepleteResource(_serverTick);
        // Schedule the respawn in the depleted-only schedule so RespawnResourceNodes never rescans
        // available nodes. Keyed by the node's freshly-computed respawn tick.
        _resourceRespawns.Schedule(target);
        SendInteractResult(session, true, "");
        SendInventoryUpdate(session, [new ItemStack(definition.YieldItemKey, actor.Inventory.QuantityOf(definition.YieldItemKey))]);
    }

    // Resolves a target network id to a world entity the requester can actually see (AOI is the security
    // boundary: a client may never interact with something outside its interest set). The actor itself
    // is always visible. Mirrors the AOI test used for snapshots so interaction and replication agree.
    private bool TryFindVisibleEntity(ClientSession session, WorldEntity actor, uint targetNetworkId, out WorldEntity target)
    {
        // Route interaction visibility through the SAME spatial index as snapshot AOI so "can replicate"
        // and "can interact" can never diverge (S38/S41). The grid returns a superset of the actor's
        // interest set; the exact IsEntityInInterest test below is the security boundary. A target outside
        // the query neighborhood is necessarily outside the interest radius too, so it would fail the test
        // anyway — same result as the old full scan, but without scanning every entity.
        _zone.World.GatherInterestCandidates(actor.Tile, _aoiQueryRadiusTiles, _aoiInteractCandidateScratch);
        foreach (var candidate in _aoiInteractCandidateScratch)
        {
            if (candidate.NetworkId != targetNetworkId)
            {
                continue;
            }

            if (IsEntityInInterest(actor, candidate, session, _options.InterestRadius))
            {
                target = candidate;
                return true;
            }

            break;
        }

        target = null!;
        return false;
    }

    private void SendInteractResult(ClientSession session, bool success, string reason)
    {
        TrySend(session.Peer, new InteractResultMessage(success, reason), DeliveryMethod.ReliableOrdered);
    }

    private void SendInventoryUpdate(ClientSession session, IReadOnlyList<ItemStack> changedStacks)
    {
        TrySend(session.Peer, new InventoryUpdateMessage(changedStacks), DeliveryMethod.ReliableOrdered);
    }

    // Adjacency = Chebyshev distance <= 1 tile, so a player standing on or in any of the 8 tiles around
    // a node (matching 8-directional movement) may harvest it.
    private static bool IsAdjacent(TileCoord a, TileCoord b)
    {
        return Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;
    }

    private ZoneInfoMessage CreateZoneInfoMessage()
    {
        // Ship the seed, not the tiles: the client regenerates the identical map locally via the shared
        // deterministic generator. ContentHash is computed over the same canonically-ordered set the
        // generator emits, so the client can compare against its own regeneration (drift/tamper check).
        var contentHash = TerrainGenerator.ContentHash(_zone.Width, _zone.Height, _zone.Seed, _zone.GenVersion);
        return new ZoneInfoMessage(_zone.Id, _zone.Width, _zone.Height, _zone.Seed, _zone.GenVersion, contentHash);
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
                ? "commands: /help, /role, /who, /metrics, /speed <multiplier>, /stress, /stress status, /stress start [clients] [duration], /stress stop"
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
            case "speed":
                HandleSpeedCommand(sender, parts);
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

    // Admin dev command (v1 way to exercise S51): /speed <multiplier> sets the CALLER's own entity speed
    // multiplier, the server recomputes its effective cadence, and a MovementSpeedChanged is replicated to
    // every viewer whose AOI currently includes the caller. /speed 1 resets to the base cadence. Item/buff-
    // driven speed is a separate follow-up; this command is just the hook to see varying cadence end-to-end.
    private void HandleSpeedCommand(ClientSession sender, string[] parts)
    {
        if (parts.Length < 2 ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
        {
            SendSystem(sender, "usage: /speed <multiplier> (e.g. /speed 2, /speed 0.5, /speed 1 to reset).");
            return;
        }

        if (!double.IsFinite(multiplier) || multiplier <= 0)
        {
            SendSystem(sender, "speed: multiplier must be a positive number.");
            return;
        }

        if (!TryGetSessionEntity(sender, out var entity))
        {
            SendSystem(sender, "speed: no controllable entity.");
            return;
        }

        var changed = entity.TrySetSpeedMultiplier(multiplier);
        var effectiveMs = EffectiveStepCooldownMs(entity);
        if (changed)
        {
            BroadcastMovementSpeedChanged(entity, effectiveMs);
        }

        SendSystem(
            sender,
            $"speed: multiplier={entity.SpeedMultiplier:0.###}, effective step cooldown={effectiveMs}ms"
                + (changed ? "." : " (unchanged)."));
        Log.Info($"{sender.DisplayName} set speed multiplier {entity.SpeedMultiplier:0.###} (cooldown={effectiveMs}ms).");
    }

    // Replicates an entity's new effective cadence to every authenticated viewer whose AOI currently
    // includes it (the entity's owner included). Reliable-ordered, like spawn/despawn — speed stays OFF the
    // hot snapshot path. Only viewers that already know the entity are notified; a viewer that has not yet
    // been sent the EntitySpawn will receive the up-to-date cadence in that spawn instead.
    private void BroadcastMovementSpeedChanged(WorldEntity entity, ushort stepCooldownMs)
    {
        var message = new MovementSpeedChangedMessage(entity.NetworkId, stepCooldownMs);
        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated || !session.KnowsEntity(entity.NetworkId))
            {
                continue;
            }

            if (!TryGetSessionEntity(session, out var viewerEntity))
            {
                continue;
            }

            if (IsEntityInInterest(viewerEntity, entity, session, _options.InterestRadius))
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
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

    private bool TrySend(NetPeer peer, EncodedPacket packet, DeliveryMethod deliveryMethod, MessageType messageType)
    {
        try
        {
            peer.Send(packet.Buffer.AsSpan(0, packet.Length), 0, deliveryMethod);
            _metrics.RecordSent(messageType, packet.Length);
            return true;
        }
        catch (Exception exception)
        {
            _metrics.RecordSendFailure();
            Log.Warn($"Failed to send {messageType} to {FormatPeer(peer)}: {exception.Message}");
            return false;
        }
    }

    private static string FormatPeer(NetPeer peer)
    {
        return $"{peer.Address}:{peer.Port}";
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

    // Minimum/maximum effective step cooldown expressed in TICKS, derived once from the ms clamp and the
    // configured tick rate. The per-entity cadence (base ÷ speed multiplier) is clamped to this range so a
    // silly multiplier can never break the tick loop. (S51)
    private uint MinEffectiveStepCooldownTicks =>
        (uint)Math.Max(1, (int)Math.Ceiling(MinEffectiveStepCooldownMs / (1000d / _options.TickRate)));

    private uint MaxEffectiveStepCooldownTicks =>
        (uint)Math.Max(1, (int)Math.Ceiling(MaxEffectiveStepCooldownMs / (1000d / _options.TickRate)));

    // The entity's clamped effective step cooldown in TICKS (what the step loop enforces) — base cooldown
    // ÷ the entity's speed multiplier, clamped to the tick bounds. Default multiplier 1.0 returns the
    // global StepCooldownTicks unchanged (behaviour parity with pre-S51).
    private uint EffectiveStepCooldownTicks(WorldEntity entity) =>
        entity.EffectiveStepCooldownTicks(
            _options.StepCooldownTicks,
            MinEffectiveStepCooldownTicks,
            MaxEffectiveStepCooldownTicks);

    // The entity's effective step cooldown in MS for the wire (EntitySpawn / MovementSpeedChanged). Derived
    // from the effective TICKS so it round-trips to the same tick count when the client re-quantises it via
    // MovementCadence.EffectiveStepCadenceMs — keeping server and client cadence in lockstep. Clamped to a
    // ushort (the wire field); the ms clamp keeps it well within range.
    private ushort EffectiveStepCooldownMs(WorldEntity entity)
    {
        var ms = EffectiveStepCooldownTicks(entity) * (1000d / _options.TickRate);
        return (ushort)Math.Clamp((int)Math.Round(ms, MidpointRounding.AwayFromZero), 1, ushort.MaxValue);
    }

    // Per-tick held-movement stepping. For every authenticated session whose intent is "moving", attempt
    // exactly one tile step in the held direction. WorldEntity.TryStep enforces the per-entity cooldown,
    // bounds, and walkability (the same validation as before) — a session that is still inside its
    // cooldown simply doesn't move this tick, and a blocked target keeps the intent so the entity steps
    // as soon as it is unblocked or redirected. A "moving" session that has not sent any intent (not even
    // a keepalive) within MoveIntentKeepaliveTimeout is force-stopped. See docs/movement-input-model.md.
    private void StepHeldMovementIntents()
    {
        var keepaliveTimeoutTicks = (uint)Math.Max(1, (int)Math.Ceiling(MoveIntentKeepaliveTimeout.TotalMilliseconds / (1000d / _options.TickRate)));

        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated || !session.MoveIntentMoving)
            {
                continue;
            }

            if (_serverTick - session.LastMoveIntentTick >= keepaliveTimeoutTicks)
            {
                session.ClearMoveIntent();
                continue;
            }

            if (!TryGetSessionEntity(session, out var entity))
            {
                continue;
            }

            var direction = session.MoveIntentDirection;
            if (_zone.TryStep(entity, direction, _serverTick, EffectiveStepCooldownTicks(entity), out var result))
            {
                MarkDirtyDurableTile(entity);
            }

            // Trace every cooldown-elapsed step attempt (accepted, blocked, or out-of-bounds); cooldown
            // no-ops are skipped to keep the trace readable, matching the old per-MoveStep granularity.
            if (result.CooldownElapsed)
            {
                _movementTrace.MoveStep(session, session.LastMoveSeq, result, _serverTick);
            }
        }
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

    // Single write-behind checkpoint for all durable per-character state. Gated to the configured
    // checkpoint cadence so no DB writes happen in the tick hot path; flushes dirty tiles and dirty
    // inventories together on the same boundary.
    private void CheckpointDirtyDurableState()
    {
        if (_serverTick < _nextPersistenceCheckpointTick)
        {
            return;
        }

        _nextPersistenceCheckpointTick = _serverTick + _options.PersistenceCheckpointTicks;

        FlushDirtyDurableTiles();
        FlushDirtyInventories();
    }

    private void FlushDirtyDurableTiles()
    {
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

    // Scans authenticated players' inventories and enqueues only those with pending changes (the
    // inventory itself tracks dirty template keys, so this is cheap when nothing changed). Draining
    // clears the inventory's dirty set, so each change is persisted at most once.
    private void FlushDirtyInventories()
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated || !TryGetSessionEntity(session, out var entity))
            {
                continue;
            }

            FlushInventory(entity);
        }
    }

    private void FlushInventory(WorldEntity entity)
    {
        if (entity.Inventory is not { HasPendingChanges: true } inventory || !entity.CharacterId.HasValue)
        {
            return;
        }

        _persistence.EnqueueItems(entity.CharacterId.Value, entity.DisplayName, inventory.DrainDirtyKeys());
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
            FlushInventory(entity);
        }
    }

    private void QueueTileSave(ClientSession session, TileCoord tile)
    {
        _dirtyDurableTiles.Remove(session.CharacterId);
        _persistence.EnqueueTile(session.CharacterId, session.DisplayName, tile);
    }

    private readonly record struct PendingTileSave(Guid CharacterId, string DisplayName, TileCoord Tile);

    // What a kicked session hands off to the login that took it over: its last tile and its live
    // in-memory inventory (both null when there was no existing session to kick).
    private readonly record struct TakeoverState(TileCoord? Tile, Inventory? Inventory);
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
        Reset();
        ProtocolCodec.Encode(message, _writer);
        return Finish();
    }

    public EncodedPacket EncodeWorldSnapshot(
        uint serverTick,
        uint snapshotSequence,
        int totalEntities,
        bool isComplete,
        int chunkIndex,
        int chunkCount,
        IReadOnlyList<EntityStateSnapshot> entities)
    {
        Reset();
        ProtocolCodec.EncodeWorldSnapshot(
            _writer,
            serverTick,
            snapshotSequence,
            totalEntities,
            isComplete,
            chunkIndex,
            chunkCount,
            entities);
        return Finish();
    }

    public EncodedPacket EncodeEntitySpawn(
        uint networkId,
        Guid characterId,
        EntityKind kind,
        string displayName,
        TileCoord tile,
        Direction8 facing,
        ushort stepCooldownMs)
    {
        Reset();
        ProtocolCodec.EncodeEntitySpawn(_writer, networkId, characterId, kind, displayName, tile, facing, stepCooldownMs);
        return Finish();
    }

    public EncodedPacket EncodeEntityDespawn(uint serverTick, uint networkId)
    {
        Reset();
        ProtocolCodec.EncodeEntityDespawn(_writer, serverTick, networkId);
        return Finish();
    }

    private void Reset()
    {
        _stream.Position = 0;
        _stream.SetLength(0);
    }

    private EncodedPacket Finish()
    {
        _writer.Flush();
        return new EncodedPacket(_stream.GetBuffer(), checked((int)_stream.Length));
    }
}
