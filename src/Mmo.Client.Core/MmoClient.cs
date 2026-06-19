using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Client.Core;

public sealed class MmoClient : IDisposable
{
    public const double RemoteInterpolationCadenceMultiplier = 1.3d;

    // Local playout buffer so the local player's tween isn't starved by snapshot tick-boundary jitter
    // (server confirms tiles on ~50ms tick boundaries). delay=0 starved (q stuck at 1); 0.5x (~75ms)
    // still dipped to 1; 1.0x cadence (~one full step) keeps a spare tile buffered so q holds ~2.
    // This trades a little local input latency for smoothness — the latency-free answer is client
    // prediction, which is deferred by design. Tunable: raise toward RemoteInterpolationCadenceMultiplier
    // (1.3) if it still dips, lower if start-of-move feels laggy.
    public const double LocalInterpolationCadenceMultiplier = 1.0d;
    private const uint PlaceholderSnapshotTtl = 60;

    private readonly ClientConnectionOptions _options;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _netManager;
    private readonly Dictionary<uint, ClientEntity> _entities = [];
    private readonly List<ChatLine> _chatLog = [];
    private readonly List<ClientError> _errors = [];
    private readonly HashSet<uint> _snapshotVisibleScratch = [];
    private readonly List<uint> _staleEntityScratch = [];
    private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly ClientMovementTrace _movementTrace;
    private readonly ClientInventory _inventory = new();

    // Acks the highest *contiguously*-received snapshot sequence (S47a), not the latest one seen, so the
    // server never advances a viewer's acked baseline past a sequence the client missed under UDP
    // loss/reorder — the prerequisite that makes S47b's cumulative step-deltas safe.
    private readonly SnapshotContiguityTracker _contiguity = new();

    private NetPeer? _serverPeer;
    private PendingSnapshot? _pendingSnapshot;
    private uint? _lastAppliedSnapshotSequence;
    private uint _moveSequence;
    private Guid _localCharacterId;
    private TileCoord? _loginTile;
    private TimeSpan _currentTime;
    private bool _disposed;

    // S53 local-player movement prediction. Created lazily once we know the zone (the blocked map), the
    // local entity, and its cadence; null until then (and on the web/headless paths that never input).
    // Local player ONLY — remote entities stay pure interpolation. See LocalPlayerPredictor.
    // ENABLED (S53 redo): the predictor now renders the local player at the predicted tile with its OWN
    // present-time step-tween (NOT the buffered interpolator that smoothed remote jitter by rendering the
    // past — that was the attempt-1 rubber-band), and reconciles UO-style by snapping/blending to the
    // server's truth on divergence. Click-to-move re-aims off the predicted tile. Revert criterion: a visible
    // rubber-band on keyboard OR mouse -> set this back to false (restores the pre-S53 confirmed-state path).
    private LocalPlayerPredictor? _predictor;
    private bool _predictionEnabled = true;

    public MmoClient(ClientConnectionOptions options)
        : this(options, ClientMovementTrace.FromEnvironment())
    {
    }

    internal MmoClient(ClientConnectionOptions options, ClientMovementTrace movementTrace)
    {
        _options = options;
        _movementTrace = movementTrace;
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
        _listener.NetworkLatencyUpdateEvent += (_, latency) => _movementTrace.UpdateLatency(latency);
        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    public ClientConnectionState State { get; private set; } = ClientConnectionState.Disconnected;

    public ServerInfo? Server { get; private set; }

    public ZoneModel? Zone { get; private set; }

    public ClientRole Role { get; private set; } = ClientRole.Player;

    internal Action<IProtocolMessage, DeliveryMethod>? OutboundSinkForTests { get; set; }

    public bool IsLoggedIn => State == ClientConnectionState.LoggedIn;

    public Guid LocalCharacterId => _localCharacterId;

    public uint? LocalNetworkId { get; private set; }

    public TileCoord? LocalTile => LocalNetworkId.HasValue && _entities.TryGetValue(LocalNetworkId.Value, out var entity)
        ? entity.Tile
        : _loginTile;

    public IReadOnlyList<ChatLine> ChatLog => _chatLog;

    public IReadOnlyList<ClientError> Errors => _errors;

    public int EntityCount => _entities.Count;

    public bool DebugMovementEnabled => _movementTrace.Enabled;

    public MovementDebugSnapshot MovementDebug => _movementTrace.Snapshot;

    // Client-side mirror of the owner's private inventory, updated by InventoryUpdate deltas. Read-only
    // view for the renderer; the server stays authoritative (each delta sets the new total).
    public ClientInventory Inventory => _inventory;

    // The most recent InteractResult the server sent, with a monotonic counter so a HUD can detect a new
    // result (success or a failure reason like "too_far"/"depleted") without an event subscription. Null
    // until the first interaction completes.
    public InteractResultInfo? LastInteractResult { get; private set; }

    public IReadOnlyList<ReplicatedEntity> Entities => _entities.Values.Select(static entity => entity.ToSnapshot()).ToArray();

    public IReadOnlyList<EntityRenderState> GetRenderStates()
    {
        return GetRenderStates(_currentTime);
    }

    public IReadOnlyList<EntityRenderState> GetRenderStates(TimeSpan now)
    {
        return _entities.Values.Select(entity => entity.ToRenderState(now)).ToArray();
    }

    public void CopyRenderStatesTo(ICollection<EntityRenderState> destination, TimeSpan now)
    {
        destination.Clear();
        foreach (var entity in _entities.Values)
        {
            destination.Add(entity.ToRenderState(now));
        }
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
        // Advance the local-player prediction AFTER draining inbound messages, so a snapshot that arrived
        // this poll re-bases the prediction before we project it forward to "now".
        _predictor?.Tick(now);
    }

    // Whether local-player movement prediction is active (S53). Disabling it (e.g. for an A/B feel check)
    // reverts the local player to the pre-S53 confirmed-tile interpolation path. Default on.
    public bool PredictionEnabled
    {
        get => _predictionEnabled;
        set => _predictionEnabled = value;
    }

    // The predicted local-player tile (S53), or null when prediction is inactive. This is the snappy,
    // ahead-of-confirmation position used for MOVEMENT rendering only. Harvest/interact targeting must use
    // LocalTile (the server-confirmed tile) instead — prediction must never authorize an interaction the
    // server will reject from its authoritative position.
    public TileCoord? PredictedLocalTile => _predictor?.PredictedTile;

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

    // Sends a held-direction movement intent (protocol v15). The server steps the entity at its own
    // cooldown cadence from this intent, so the client sends it on change (keydown / keyup / direction
    // change) plus a low-rate keepalive — NOT once per step. Reliable-ordered: a dropped "stop" must not
    // be lost. Direction is ignored by the server when moving is false. See docs/movement-input-model.md.
    public uint SendMoveIntent(bool moving, Direction8 direction)
    {
        var sequence = ++_moveSequence;
        Send(new MoveIntentMessage(sequence, moving, direction), DeliveryMethod.ReliableOrdered);
        _movementTrace.MoveSent(sequence, moving, direction);
        // S53: the held intent we just sent the server is exactly what the local predictor mirrors. Feed it
        // so the first step on keydown / the stop on keyup is predicted with no round-trip wait. The
        // predictor steps forward on each Poll(now); SetIntent only records the held state + arms the first
        // step. Created lazily here once the zone + local entity + cadence are all available.
        EnsurePredictor();
        _predictor?.SetIntent(moving, direction, _currentTime);
        return sequence;
    }

    // Creates the local-player predictor once everything it mirrors is known: prediction enabled, a zone
    // (the blocked map), and the local entity (its start tile + per-entity cadence). Idempotent — no-op once
    // created or while a prerequisite is missing. Anchored to the local entity's current confirmed tile so
    // it starts in lockstep with the server.
    private void EnsurePredictor()
    {
        if (_predictor is not null || !_predictionEnabled || Zone is null
            || !LocalNetworkId.HasValue || !_entities.TryGetValue(LocalNetworkId.Value, out var local))
        {
            return;
        }

        _predictor = local.AttachPredictor(ResolveCadence(local.StepCooldownMs), IsWalkableForPrediction, ResolveTurnDelay());
    }

    // Resolves the predictor's turn delay (ms): the ServerHello-advertised, tick-quantised value so the
    // predicted turn cost matches the server's TurnDelayTicks exactly. Falls back to the 80 ms default until
    // ServerHello lands (same default ServerOptions/the predictor ctor use).
    private double ResolveTurnDelay() => Server?.EffectiveTurnDelayMs ?? 80d;

    // Drops the local entity reference and its predictor (local despawn / AOI exit / logout). Nulling the
    // predictor lets EnsurePredictor re-attach a fresh one (anchored to the new confirmed tile) when the
    // local entity respawns, so a stale predictor never drives a removed entity's interpolator (S47b guard).
    private void ClearLocalEntity()
    {
        LocalNetworkId = null;
        _predictor = null;
    }

    // Walkability oracle for the local predictor: mirrors WorldEntity.TryStep / TileGrid.IsWalkable — a
    // tile is walkable iff it is in bounds and not blocked. Same rule TilePathfinder uses, so prediction and
    // the server agree on every step except timing.
    private bool IsWalkableForPrediction(TileCoord tile)
    {
        var zone = Zone;
        return zone is not null
            && tile.X >= 0 && tile.X < zone.Width
            && tile.Y >= 0 && tile.Y < zone.Height
            && !zone.IsBlocked(tile);
    }

    public void SendChat(string text)
    {
        Send(new ChatSendMessage(text), DeliveryMethod.ReliableOrdered);
    }

    // Sends a generic Interact (harvest) request for the entity the player is pointing at. Reliable-ordered
    // so a request is never silently dropped. No client-side prediction: the result lands later via the
    // owner-only InteractResult (and InventoryUpdate on success). The server re-validates authority/adjacency.
    public void SendInteractRequest(uint targetNetworkId)
    {
        Send(new InteractRequestMessage(targetNetworkId), DeliveryMethod.ReliableOrdered);
    }

    // S60 admin live-tuning: ask the server to set a tuning key (e.g. "move.stepCooldownMs") to a value.
    // Reliable-ordered. The server admin-gates and clamps/validates; a non-admin send is silently ignored
    // server-side. No client-side prediction — the panel just shows the value it sent.
    public void SendAdminSetTuning(string key, double value)
    {
        Send(new AdminSetTuningMessage(key, value), DeliveryMethod.ReliableOrdered);
    }

    // S63: live-applies a turn delay (ms) to the LOCAL predictor so the F4 panel can retune the turn feel in
    // lockstep with the server. The value is tick-quantised the same way the server quantises TurnDelayTicks
    // (via MovementCadence.EffectiveTurnDelayMs) so client and server agree to the tick. The F4 handler also
    // sends move.turnDelayMs to the server via AdminSetTuning; this keeps the predictor matched. Clamped to
    // the same [0, 1000] ms registry bound before quantisation.
    public void SetLocalTurnDelayMs(double turnDelayMs)
    {
        var clamped = Math.Clamp(turnDelayMs, 0d, 1000d);
        var tickRate = Server?.TickRate ?? 20;
        var quantised = MovementCadence.EffectiveTurnDelayMs((int)Math.Round(clamped, MidpointRounding.AwayFromZero), tickRate);
        if (LocalNetworkId is { } id && _entities.TryGetValue(id, out var local))
        {
            local.SetPredictorTurnDelay(quantised);
        }
    }

    public void RecordFrameHitch(double durationMs, int gc0, int gc1, int gc2)
    {
        _movementTrace.FrameHitch(durationMs, gc0, gc1, gc2, EntityCount, State);
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
                HandleServerHello(hello);
                break;
            case LoginResultMessage login:
                HandleLogin(login);
                break;
            case ZoneInfoMessage zone:
                HandleZoneInfo(zone);
                break;
            case EntitySpawnMessage spawn:
                UpsertEntity(spawn.NetworkId, spawn.CharacterId, spawn.Kind, spawn.DisplayName, spawn.Tile, spawn.Facing, spawn.StepCooldownMs);
                break;
            case MovementSpeedChangedMessage speed:
                HandleMovementSpeedChanged(speed);
                break;
            case EntityDespawnMessage despawn:
                _entities.Remove(despawn.NetworkId);
                if (LocalNetworkId == despawn.NetworkId)
                {
                    ClearLocalEntity();
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
            case InteractResultMessage interact:
                HandleInteractResult(interact);
                break;
            case InventoryUpdateMessage inventory:
                _inventory.Apply(inventory.ChangedStacks);
                break;
        }
    }

    private void HandleInteractResult(InteractResultMessage interact)
    {
        var sequence = (LastInteractResult?.Sequence ?? 0) + 1;
        LastInteractResult = new InteractResultInfo(interact.Success, interact.Reason, sequence);
    }

    private void HandleZoneInfo(ZoneInfoMessage zone)
    {
        // Regenerate the map locally from the seed descriptor instead of consuming a tile payload.
        // If the server advertises a generator version this build can't produce, the terrain would be
        // wrong — fail loudly rather than render a mismatched map. The server stays authoritative for
        // movement regardless, so this is a diagnostic gate, not a security boundary.
        ZoneModel model;
        try
        {
            model = new ZoneModel(zone.ZoneId, zone.Width, zone.Height, zone.Seed, zone.GenVersion);
        }
        catch (Exception exception)
        {
            _errors.Add(new ClientError(
                "zone-gen-failed",
                $"Could not regenerate zone '{zone.ZoneId}' (seed={zone.Seed}, genVersion={zone.GenVersion}): {exception.Message}"));
            return;
        }

        if (model.ContentHash != zone.ContentHash)
        {
            // Loud diagnostic: client/server generator drift or tampering. We still apply the locally
            // generated map (the server validates movement against its own copy).
            _errors.Add(new ClientError(
                "zone-hash-mismatch",
                $"Zone '{zone.ZoneId}' content hash mismatch: local {model.ContentHash:X16} != server {zone.ContentHash:X16} "
                    + $"(seed={zone.Seed}, genVersion={zone.GenVersion}, {zone.Width}x{zone.Height}). Generator drift or tampering."));
        }

        Zone = model;
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

    internal void HandleMessageForTests(IProtocolMessage message)
    {
        HandleMessage(message);
    }

    private void HandleServerHello(ServerHelloMessage hello)
    {
        Server = new ServerInfo(hello.ServerName, hello.ProtocolVersion, hello.TickRate, hello.StepCooldownMs, hello.TurnDelayMs, hello.InterestRadiusTiles);
        RefreshInterpolatorCadence();
        // S63: adopt the advertised turn delay if the predictor is already attached (ServerHello can arrive
        // after the local entity spawned in a re-hello). New predictors seed it via EnsurePredictor.
        _entities.TryGetValue(LocalNetworkId ?? 0, out var local);
        local?.SetPredictorTurnDelay(ResolveTurnDelay());
    }

    private void HandleSnapshot(WorldSnapshotMessage snapshot)
    {
        if (_lastAppliedSnapshotSequence.HasValue && snapshot.SnapshotSequence <= _lastAppliedSnapshotSequence.Value)
        {
            return;
        }

        if (snapshot.ChunkCount <= 1)
        {
            ApplySnapshot(snapshot.ServerTick, snapshot.SnapshotSequence, snapshot.IsComplete, snapshot.Entities);
            AcknowledgeAppliedSnapshot(snapshot.SnapshotSequence, snapshot.IsComplete);
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
        AcknowledgeAppliedSnapshot(_pendingSnapshot.Sequence, _pendingSnapshot.IsFullSnapshot);
        _pendingSnapshot = null;
    }

    private void ApplySnapshot(uint serverTick, uint sequence, bool isComplete, IReadOnlyCollection<EntityStateSnapshot> entities)
    {
        _snapshotVisibleScratch.Clear();
        foreach (var state in entities)
        {
            _snapshotVisibleScratch.Add(state.NetworkId);
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

            var confirmation = entity.ApplySnapshot(state.Tile, state.Facing, _currentTime, sequence, state.Depleted);
            if (confirmation.TileChanged)
            {
                _movementTrace.TileConfirmed(
                    state.NetworkId,
                    state.Tile,
                    sequence,
                    DateTimeOffset.UtcNow,
                    confirmation.QueueDepth,
                    confirmation.EffectiveCadenceMs,
                    confirmation.RenderPosition);
            }
        }

        if (isComplete)
        {
            _staleEntityScratch.Clear();
            foreach (var networkId in _entities.Keys)
            {
                if (!_snapshotVisibleScratch.Contains(networkId))
                {
                    _staleEntityScratch.Add(networkId);
                }
            }

            foreach (var networkId in _staleEntityScratch)
            {
                _entities.Remove(networkId);
                if (LocalNetworkId == networkId)
                {
                    ClearLocalEntity();
                }
            }
        }
        else
        {
            PruneStalePlaceholders(sequence);
        }

        _lastAppliedSnapshotSequence = sequence;
    }

    // A snapshot just applied. Record it as received and ack the highest contiguously-received sequence
    // (the top of the gap-free prefix), NOT the just-applied one: if an earlier sequence was dropped, the
    // contiguous cursor stalls at the gap and the server won't advance the baseline past it. With no loss
    // the contiguous cursor equals the latest sequence, so this is a no-op vs. the old behavior. The ack is
    // Sequenced (droppable): a lost ack just means the server re-includes still-unacked entities next tick.
    //
    // isComplete marks a FULL (re-baseline / AOI-entry) snapshot: it re-establishes the whole visible set
    // from scratch, so any earlier gap is irrelevant. The tracker jumps the cursor to it, which is what
    // unstalls a permanently-lost middle sequence — the server's 2 s force-re-baseline sends exactly such a
    // complete snapshot, and acking ITS sequence lets the server stop re-baselining and converge.
    private void AcknowledgeAppliedSnapshot(uint snapshotSequence, bool isComplete)
    {
        var contiguous = _contiguity.Observe(snapshotSequence, isComplete);
        Send(new SnapshotAckMessage(contiguous), DeliveryMethod.Sequenced);
    }

    private void PruneStalePlaceholders(uint currentSequence)
    {
        _staleEntityScratch.Clear();
        foreach (var entity in _entities.Values)
        {
            if (entity.IsPlaceholder
                && currentSequence > entity.LastSeenSnapshotSequence
                && currentSequence - entity.LastSeenSnapshotSequence > PlaceholderSnapshotTtl)
            {
                _staleEntityScratch.Add(entity.NetworkId);
            }
        }

        foreach (var networkId in _staleEntityScratch)
        {
            _entities.Remove(networkId);
            if (LocalNetworkId == networkId)
            {
                ClearLocalEntity();
            }
        }
    }

    // stepCooldownMs is the entity's server-advertised effective cadence (from EntitySpawn); null on the
    // snapshot-created placeholder path, where the entity tweens at the ServerHello global until its real
    // EntitySpawn arrives. A 0 cooldown is treated as "absent" (no spawn has supplied a real value yet).
    private ClientEntity UpsertEntity(
        uint networkId,
        Guid characterId,
        EntityKind kind,
        string displayName,
        TileCoord tile,
        Direction8 facing,
        ushort? stepCooldownMs = null)
    {
        var isLocal = characterId != Guid.Empty && characterId == _localCharacterId;
        var effectiveCooldown = stepCooldownMs is > 0 ? stepCooldownMs : null;
        if (_entities.TryGetValue(networkId, out var existing))
        {
            existing.UpdateMetadata(characterId, kind, displayName, isLocal);
            if (effectiveCooldown.HasValue)
            {
                existing.SetStepCooldownMs(effectiveCooldown.Value, ResolveCadence(effectiveCooldown), existing.IsLocal);
            }

            // EntitySpawn carries no Depleted bit (that rides the AOI snapshot), so preserve whatever the
            // last snapshot set rather than resetting a known-depleted node to available.
            existing.ApplySnapshot(tile, facing, _currentTime, _lastAppliedSnapshotSequence ?? 0, existing.Depleted);
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
            CreateInterpolator(tile, isLocal, effectiveCooldown),
            effectiveCooldown);
        _entities[networkId] = entity;
        if (isLocal)
        {
            LocalNetworkId = networkId;
        }

        return entity;
    }

    private void HandleMovementSpeedChanged(MovementSpeedChangedMessage speed)
    {
        if (!_entities.TryGetValue(speed.NetworkId, out var entity))
        {
            return;
        }

        // A 0 cooldown means "fall back to the global cadence" (clear any per-entity override).
        ushort? cooldown = speed.StepCooldownMs > 0 ? speed.StepCooldownMs : null;
        entity.SetStepCooldownMs(cooldown, ResolveCadence(cooldown), entity.IsLocal);
    }

    // Resolves the tween step duration (ms) for a given per-entity cooldown: the entity's own advertised
    // cooldown when present, else the ServerHello global. Mirrors the server's tick-quantised derivation so
    // client and server cadence stay in lockstep (no tween starvation/overrun).
    private double ResolveCadence(ushort? stepCooldownMs)
    {
        var tickRate = Server?.TickRate ?? 20;
        if (stepCooldownMs is > 0)
        {
            return MovementCadence.EffectiveStepCadenceMs(stepCooldownMs.Value, tickRate);
        }

        return Server?.EffectiveStepCadenceMs ?? MovementCadence.EffectiveStepCadenceMs(140, 20);
    }

    // Recomputes every entity's tween cadence. Each entity keeps its OWN advertised cooldown if it has one
    // (per-entity speed, S51) and only falls back to the ServerHello global when it doesn't — so a global
    // refresh (e.g. ServerHello arriving) never clobbers a per-entity cadence.
    private void RefreshInterpolatorCadence()
    {
        foreach (var entity in _entities.Values)
        {
            var cadence = ResolveCadence(entity.StepCooldownMs);
            var delay = cadence * (entity.IsLocal ? LocalInterpolationCadenceMultiplier : RemoteInterpolationCadenceMultiplier);
            entity.UpdateInterpolationCadence(cadence, delay);
        }
    }

    private TileInterpolator CreateInterpolator(TileCoord initialTile, bool isLocal, ushort? stepCooldownMs)
    {
        var cadence = ResolveCadence(stepCooldownMs);
        var delay = cadence * (isLocal ? LocalInterpolationCadenceMultiplier : RemoteInterpolationCadenceMultiplier);
        return new TileInterpolator(initialTile, cadence, delay);
    }

    private void Send(IProtocolMessage message, DeliveryMethod deliveryMethod)
    {
        if (OutboundSinkForTests is not null)
        {
            OutboundSinkForTests(message, deliveryMethod);
            return;
        }

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
        // S53: non-null only for the local player while prediction is active. When present, the predictor
        // OWNS the render position via its own present-time step-tween (the buffered _interpolator — which
        // renders confirmed state in the past for jitter smoothing — is bypassed for the local player, the
        // attempt-1 rubber-band fix). Snapshots re-base the predictor instead of confirming the interpolator.
        private LocalPlayerPredictor? _predictor;

        public ClientEntity(
            uint networkId,
            Guid characterId,
            EntityKind kind,
            string displayName,
            TileCoord tile,
            Direction8 facing,
            bool isLocal,
            TileInterpolator interpolator,
            ushort? stepCooldownMs)
        {
            NetworkId = networkId;
            CharacterId = characterId;
            Kind = kind;
            DisplayName = displayName;
            Tile = tile;
            Facing = facing;
            IsLocal = isLocal;
            _interpolator = interpolator;
            StepCooldownMs = stepCooldownMs;
        }

        public uint NetworkId { get; }

        public Guid CharacterId { get; private set; }

        public EntityKind Kind { get; private set; }

        public string DisplayName { get; private set; }

        public TileCoord Tile { get; private set; }

        public Direction8 Facing { get; private set; }

        public bool IsLocal { get; private set; }

        // The entity's server-advertised effective step cooldown in ms (S51). Null = no per-entity value yet
        // (a snapshot-created placeholder); the entity then tweens at the ServerHello global cadence. Set by
        // EntitySpawn and MovementSpeedChanged. A 0 from the wire is normalised to null upstream.
        public ushort? StepCooldownMs { get; private set; }

        // Resource-node availability replicated via the AOI snapshot Depleted bit. Always false for
        // players/NPCs (the server never sets it for them). Carried separately from the interpolated
        // position so the renderer can grey/hide a node without affecting movement.
        public bool Depleted { get; private set; }

        public bool IsPlaceholder => CharacterId == Guid.Empty
            && Kind == EntityKind.Player
            && DisplayName.StartsWith("#", StringComparison.Ordinal);

        public uint LastSeenSnapshotSequence { get; private set; }

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

        public EntityConfirmationDebug ApplySnapshot(TileCoord tile, Direction8 facing, TimeSpan receivedAt, uint snapshotSequence, bool depleted = false)
        {
            var previousTile = Tile;
            // Tile/Facing always track the SERVER-CONFIRMED state: LocalTile (harvest/click targeting) reads
            // it, and the renderer's AuthoritativeTile uses it. Prediction only affects the interpolated
            // render position, never this authoritative tile.
            Tile = tile;
            Depleted = depleted;
            LastSeenSnapshotSequence = snapshotSequence;
            if (_predictor is not null)
            {
                // Local predicted entity: re-base the prediction off the confirmed tile (the predictor owns
                // its own present-time render tween — the buffered interpolator is bypassed here). Facing
                // follows the prediction while moving, else the confirmed facing.
                _predictor.Reconcile(tile, receivedAt);
                _predictor.ConfirmFacing(facing);
                Facing = _predictor.Facing;
                return new EntityConfirmationDebug(
                    tile != previousTile,
                    0,
                    _predictor.CadenceMs,
                    _predictor.RenderPosition);
            }

            Facing = facing;
            _interpolator.Confirm(tile, receivedAt);
            return new EntityConfirmationDebug(
                tile != previousTile,
                _interpolator.QueueDepth,
                _interpolator.StepDurationMs,
                _interpolator.RenderPosition);
        }

        // S53: attaches a local-player predictor that takes over the render position with its own present-time
        // step-tween (the buffered interpolator is bypassed for the local player). Anchored to the current
        // confirmed tile + facing. Returns the predictor so the client can feed it held intent and tick it.
        // Idempotent: returns the existing one if already set.
        public LocalPlayerPredictor AttachPredictor(double cadenceMs, Func<TileCoord, bool> isWalkable, double turnDelayMs)
        {
            _predictor ??= new LocalPlayerPredictor(Tile, Facing, cadenceMs, isWalkable, turnDelayMs);
            return _predictor;
        }

        // S63: live-retunes the predictor's turn delay (F4 move.turnDelayMs). No-op if no predictor (the local
        // entity isn't predicting yet); EnsurePredictor seeds the current value when it attaches.
        public void SetPredictorTurnDelay(double turnDelayMs)
        {
            _predictor?.SetTurnDelay(turnDelayMs);
        }

        public void UpdateInterpolationCadence(double stepDurationMs, double interpolationDelayMs)
        {
            _interpolator.UpdateCadence(stepDurationMs, interpolationDelayMs);
        }

        // Applies a per-entity cadence (from EntitySpawn / MovementSpeedChanged). stepCooldownMs null clears
        // the override (the entity reverts to the global cadence the caller resolved). cadenceMs is the
        // already-resolved tween step duration; the interpolation delay is derived from it the same way as
        // CreateInterpolator (local vs remote playout buffer multiplier).
        public void SetStepCooldownMs(ushort? stepCooldownMs, double cadenceMs, bool isLocal)
        {
            StepCooldownMs = stepCooldownMs;
            var delay = cadenceMs * (isLocal ? LocalInterpolationCadenceMultiplier : RemoteInterpolationCadenceMultiplier);
            _interpolator.UpdateCadence(cadenceMs, delay);
            // S53: adopt the new cadence for prediction immediately (mirrors the server applying the new
            // EffectiveStepCooldown on MovementSpeedChanged) so predicted steps stay in lockstep.
            _predictor?.SetCadence(cadenceMs);
        }

        public ReplicatedEntity ToSnapshot()
        {
            return new ReplicatedEntity(NetworkId, CharacterId, Kind, DisplayName, Tile, Facing, IsLocal, Depleted);
        }

        public EntityRenderState ToRenderState(TimeSpan now)
        {
            // S53: the local predicted player renders from the predictor's OWN present-time tween (snappy, no
            // playout delay). Everything else (remote players, resources) keeps the buffered interpolator.
            var position = _predictor is not null ? _predictor.Sample(now) : _interpolator.Sample(now);
            // S59: render the predictor's LIVE facing for the local entity, so a predicted turn (no tile move)
            // rotates the avatar immediately instead of waiting for the next snapshot to sync Facing.
            var facing = _predictor is not null ? _predictor.Facing : Facing;
            return new EntityRenderState(NetworkId, CharacterId, Kind, DisplayName, position, Tile, facing, IsLocal, Depleted);
        }
    }

    private readonly record struct EntityConfirmationDebug(
        bool TileChanged,
        int QueueDepth,
        double EffectiveCadenceMs,
        RenderPosition RenderPosition);

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
