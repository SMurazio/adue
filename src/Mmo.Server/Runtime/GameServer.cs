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
    // Per-entity snapshot state wire size: networkId(2) + x(2) + y(2) + facing(1) + depleted(1) = 8, plus
    // COMBAT-S2A's public HP Health(2) + MaxHealth(2) = 12.
    private const int EntityStateFixedBytes = 12;
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

    // S103 commit-step anti-cheat floor. A StepCommitRequest is accepted only if the entity is at least this
    // fraction of its step cooldown into the current step (elapsed >= CommitAcceptFraction * cooldown). The
    // legit client only commits once its cosmetic render has glided past its ~0.7 threshold (the F6 default),
    // which is BELOW this fraction in elapsed terms only after enough cooldown has passed — set to 0.5 so a
    // genuine release-commit (render ~70% onto the next tile, i.e. well past half the cooldown) is always
    // accepted, while a scripted commit fired below half the cooldown is rejected. On accept the server borrows
    // the next step's full cooldown (WorldEntity.TryCommitStep), so the average step rate stays capped at cadence
    // regardless — this fraction only sets how early within a step a commit may legitimately fire.
    private const double CommitAcceptFraction = 0.5d;

    // NET3 authored-tick commit window. A UoClientDriven commit is applied at its AUTHORED tick (the tick the
    // client's predictor banked the step on), but the authored tick is clamped to [serverTick - AuthoredTickPastWindow,
    // serverTick + AuthoredTickFutureLead] before it gates/schedules anything, so a far-past (very stale recovered
    // commit / tamper) or far-future (clock skew) authored tick can't poison the schedule. The past window covers a
    // generous loss-recovery depth (the redundant window is ~8 commits ≈ 8 cadences ≈ 24 ticks at 150 ms cadence;
    // 64 ticks ≈ 3.2 s gives ample slack for a deep recovery without letting an ancient tick through). The future
    // lead is tiny: the predictor leads the server by ~1-2 in-flight steps, so a few ticks of lead is legitimate
    // (uplink jitter / a frame that banked slightly ahead), but a commit authored far in the future is clamped down
    // so the schedule can never run ahead of real time by more than this — the rate stays capped at cadence.
    private const uint AuthoredTickPastWindow = 64;
    private const uint AuthoredTickFutureLead = 4;

    // COMBAT-S2B / FREEAIM / COMBAT-TUNING: the free-aim attack feel-knobs (per-entity attack cooldown, swing-root
    // duration, sector half-angle + radius, damage per hit) are no longer hard constants here — they now live on the
    // mutable ServerTuning holder, are LIVE-tunable via the combat.* AdminSetTuning keys, and are REPLICATED to each
    // client (CombatTuningSnapshot) so the client's wedge/predictor/cooldown-viz match the server's resolution
    // instead of duplicating their own constants. HandleAttack + FreeAimSectorResolver read _tuning each attack.

    // Keepalive safety timeout for held movement intents (~1 s). The client resends its current intent
    // every ~500 ms; if a "moving" session goes silent for longer than this (a wedged-but-connected
    // client), the tick loop clears its intent so it stops walking. A real disconnect already despawns
    // the entity, so this only guards the wedged-client edge case. See docs/movement-input-model.md.
    private static readonly TimeSpan MoveIntentKeepaliveTimeout = TimeSpan.FromSeconds(1);

    private readonly ServerOptions _options;
    // S60 live-tuning holder, seeded from _options. The game loop reads the tunable params (step cooldown,
    // interest radius) through THIS, not _options, so an admin AdminSetTuning can change them mid-run.
    private readonly ServerTuning _tuning;
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
    // COMBAT-S2B: reused candidate buffer for the melee-cone occupancy query (entities in the cells overlapping the
    // attacker's cone), filtered to the exact cone tiles at the call site. Single-threaded tick loop, so reuse is safe.
    private readonly List<WorldEntity> _attackCandidateScratch = [];
    // COMBAT-QOL: reused buffer of the victims a resolved attack actually damaged (entity + amount), so HandleAttack
    // can emit one AOI-gated cosmetic DamageEventMessage per real hit without allocating per attack. Single-threaded
    // tick/handler path, so reuse across attacks is safe.
    private readonly List<FreeAimSectorResolver.DamagedVictim> _damagedVictimScratch = [];
    // LIVING-ENEMIES P2: reused candidate buffer for a monster's throttled aggro scan (players in the cells around
    // the monster, then filtered to alive players within aggroRadius). Single-threaded tick loop (StepMonsterAi runs
    // in TickCore, not concurrently with the snapshot pass), so reusing one buffer across all monsters is safe.
    private readonly List<WorldEntity> _monsterAggroScratch = [];
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

    // LIVING-ENEMIES P1: the server-side leashed-roam brain for EntityKind.Monster. Owns every monster's per-AI
    // state + a seeded PRNG (seeded off the map seed so a given world's roaming is reproducible in tests/repro
    // runs), and steps each monster through the SAME Zone.TryStep path players use. Constructed after _zone since
    // it closes over _zone.IsWalkable / _zone.TryStep. Stepped each tick by StepMonsterAi (a sibling pass to
    // StepHeldMovementIntents), paced off the step cooldown — so a monster never steps every tick.
    private readonly MonsterRoamAi _monsterAi;

    // Half-extent (in tiles) of the cell neighborhood an AOI query must examine. The grid returns every
    // entity in the cells overlapping [viewer ± this], and the per-entity interest test then filters to
    // the exact set. It MUST cover the interest EXIT radius (interest radius + hysteresis), so a
    // hysteresis-retained entity sitting between the entry and exit radius is never dropped — dropping
    // one would be both a visible bug and an anti-cheat hole. Derived from the LIVE interest radius and
    // recomputed whenever AdminSetTuning changes it (S60); the entity-grid cell size stays fixed (a pure
    // perf knob — correctness is independent of it, the query box just grows to cover a larger radius).
    private int _aoiQueryRadiusTiles;

    private uint _serverTick;
    private uint _nextPersistenceCheckpointTick;
    private long _pendingMovementElapsedTicks;
    private long _traceStartTimestamp;
    private int _snapshotsSentThisTick;

    public GameServer(ServerOptions options, ICharacterRepository characters)
    {
        _options = options;
        _tuning = new ServerTuning(options);
        _aoiQueryRadiusTiles = ResolveAoiQueryRadiusTiles(_tuning.InterestRadius);
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
        // LIVING-ENEMIES P1: seed the monster roam AI off the map seed so a given world replays the same roaming
        // (deterministic for repro/tests); it steps monsters through _zone's walkability + tile-step path.
        _monsterAi = new MonsterRoamAi(
            options.MapSeed,
            _zone.IsWalkable,
            (entity, direction, tick, cooldownTicks) => _zone.TryStep(entity, direction, tick, cooldownTicks),
            FindMonsterAggroTarget,
            TryResolveMonsterTarget,
            ApplyMonsterAttack);
        ScatterResourceNodes();
        SpawnDummies();
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
            case MoveInputMessage input:
                if (session.IsAuthenticated)
                {
                    HandleMoveInput(session, input);
                }
                break;
            case StepCommitRequestMessage commit:
                if (session.IsAuthenticated)
                {
                    HandleStepCommit(session, commit.Sequence, commit.Direction);
                }
                break;
            case StepCommitBatchMessage batch:
                if (session.IsAuthenticated)
                {
                    HandleStepCommitBatch(session, batch);
                }
                break;
            case MovementModeMessage mode:
                if (session.IsAuthenticated)
                {
                    // UO1: record whether this session drives its own movement (client-driven) so the tick loop
                    // stops auto-pacing its held MoveIntent (StepHeldMovementIntents skips client-driven sessions).
                    // No stepping happens here; the entity advances ONLY via the per-step StepCommitRequest stream.
                    session.SetClientDrivenMovement(mode.ClientDriven);
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
            case AdminSetTuningMessage tuning:
                if (session.IsAuthenticated)
                {
                    HandleAdminSetTuning(session, tuning.Key, tuning.Value);
                }
                break;
            case AdminSetStatMessage stat:
                if (session.IsAuthenticated)
                {
                    HandleAdminSetStat(session, stat.Stat, stat.Value);
                }
                break;
            case AttackMessage attack:
                if (session.IsAuthenticated)
                {
                    HandleAttack(session, attack.Sequence, attack.Kind, attack.AimAngle, attack.AuthoredTick);
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
                        // COMBAT-S1: replicate the player's initial vitals (full 100/100 each by default) so the
                        // HUD bars render real values immediately on login, not the F5 stub.
                        SendPlayerStats(current, entity);
                        // COMBAT-TUNING: replicate the current combat feel-knobs so the client's free-aim wedge mesh,
                        // swing-root prediction, and radial cooldown indicator derive from the server's authoritative
                        // values immediately on login (not stale client constants).
                        SendCombatTuning(current);
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
            // LIVING-ENEMIES P1: a sibling movement pass that steps each roaming Monster off the step cooldown
            // (same tile-step path as players), so monsters idle near home and occasionally stroll within a leash.
            StepMonsterAi();
        }

        using (tickBudget.Measure(TickBudgetCategory.Other))
        {
            RespawnResourceNodes();
            RegenEnemies();
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
            Log.Info($"snapshot for {session.DisplayName}: visible={visibleCount}/{entities.Count}, radius={_tuning.InterestRadius:0.#}, chunks={sentPackets}/{chunkCount}, bytes={sentBytes}");
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
                if (IsEntityInInterest(recipientEntity, candidate, recipient, _tuning.InterestRadius))
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
            if (IsEntityInInterest(recipientEntity, candidate, recipient, _tuning.InterestRadius))
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

        // S76: the recipient-scoped step sequence for this build. Read once from the recipient's OWN entity and
        // ride it on every snapshot header (real-delta AND keep-alive), even when that entity is idle and gets
        // delta'd out of the payload below — it is recipient metadata, not entity payload.
        var recipientStepSeq = recipientEntity.StepSequence;

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
                SendKeepAliveSnapshot(recipient, recipientStepSeq, visible.Count, tickBudget, ref sentBytes, ref sentPackets);
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
                    recipientStepSeq,
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
        uint recipientStepSeq,
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
            // S76: the keep-alive carries the recipient's step seq in the header even though its payload is
            // empty — an idle player's own entity is exactly the case that gets no entity delta, so without
            // this the seq would never reach an idle client.
            packet = _snapshotEncodeBuffer.EncodeWorldSnapshot(
                _serverTick,
                snapshotSequence,
                recipientStepSeq,
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
        // COMBAT-S2A: replicate PUBLIC HP (current + max) on the per-entity state for the overhead bar. Only
        // entities that actually HAVE vitals report HP (players + dummies); everything else (resource nodes,
        // any future stat-less kind) replicates 0/0, which the client reads as "no HP" and hides the bar for.
        // WorldEntity.Stats defaults to 100/100 for every kind, so the kind gate — not the value — is what
        // distinguishes "has HP" from "no HP". Mana/stamina are deliberately NOT here (owner-only via
        // PlayerStatsMessage). Clamp to ushort defensively (HP is small and non-negative in practice).
        var (health, maxHealth) = HasPublicHealth(entity.Kind)
            ? (ToHealthWire(entity.Stats.Health), ToHealthWire(entity.Stats.MaxHealth))
            : ((ushort)0, (ushort)0);
        return new EntityStateSnapshot(entity.NetworkId, entity.Tile, entity.Facing, entity.IsDepleted, health, maxHealth);
    }

    // Which entity kinds expose a public HP bar. Players, dummies, and (LIVING-ENEMIES P1) roaming Monsters carry
    // CharacterStats that drive the overhead bar; resources and anything else do not (they replicate 0/0 and the
    // client hides the bar — the client gate is purely MaxHealth>0, so adding Monster here is the only touch
    // needed to show its bar).
    private static bool HasPublicHealth(EntityKind kind) =>
        kind is EntityKind.Player or EntityKind.Dummy or EntityKind.Monster;

    private static ushort ToHealthWire(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

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

    // COMBAT-S2A: spawn a couple of stationary "Dummy" enemies near the primary spawn so a logged-in player
    // immediately sees a hittable target with a partial red overhead HP bar (the proof the current/max HP
    // replication renders). They are EntityKind.Dummy transients with CharacterStats (HP); their current HP is
    // dev-set to a PARTIAL value so the bar shows a non-full fill. No behaviour/AI/damage/regen this stage —
    // they just stand there and replicate their HP via the snapshot like any other entity. Placement is
    // deterministic: a short Chebyshev-radius scan outward from the first spawn tile for distinct walkable
    // tiles, so the same world regenerates the same dummies on restart (no PRNG, no clock).
    private void SpawnDummies()
    {
        const int dummyCount = 2;
        const int partialHealth = 70; // out of the CharacterStats.Default MaxHealth (100) → a 70% bar.

        var anchor = _zone.SpawnTiles.Count > 0 ? _zone.SpawnTiles[0] : Zone.DefaultSpawnTile;
        var placed = 0;
        // Ring-scan outward from the anchor (radius 2 onward so dummies don't land on the spawn tile itself),
        // taking the first `dummyCount` distinct walkable tiles. Deterministic iteration order = deterministic
        // layout. Bounded radius so a pathological map can't loop forever; if fewer than dummyCount tiles are
        // found, we simply spawn fewer (a target is still present).
        for (var radius = 2; radius <= 6 && placed < dummyCount; radius++)
        {
            for (var dy = -radius; dy <= radius && placed < dummyCount; dy++)
            {
                for (var dx = -radius; dx <= radius && placed < dummyCount; dx++)
                {
                    // Only the ring at this Chebyshev radius (skip the filled interior visited at smaller radii).
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                    {
                        continue;
                    }

                    var tile = anchor.Offset(dx, dy);
                    if (!_zone.IsWalkable(tile))
                    {
                        continue;
                    }

                    var dummy = _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Dummy, "Dummy", tile, Direction8.S);
                    // Partial HP so the overhead bar is visibly non-full (proves current/max rendering).
                    dummy.TrySetStatCurrent(StatKind.Health, partialHealth);
                    placed++;
                }
            }
        }
    }

    // O(depleted) respawn: the schedule pops only nodes whose respawn tick has arrived and flips them back
    // to Available; StateRevision is already bumped by TryRespawnResource so the refreshed availability
    // re-replicates by AOI (no extra work needed in the callback). Still-available nodes are never visited.
    private void RespawnResourceNodes()
    {
        _resourceRespawns.DrainDue(_serverTick, static _ => { });
    }

    // COMBAT-QOL: heal stationary enemy targets (Dummy/Npc) toward MaxHealth at a HEAVY per-tick rate so a hit dummy
    // refills fast and stays a permanent test target. Gated on the SAME kinds that can TAKE damage
    // (MeleeConeResolver.IsAttackableEnemy) so we never "regen" a player or a resource. TryRegenHealth clamps at max,
    // no-ops at full (so a healthy dummy costs nothing), and bumps StateRevision only on a real change — the refilled
    // HP rides the existing snapshot HP field, so the overhead bar fills automatically. NO DamageEventMessage is ever
    // emitted here: only real damage floats a number. Iterates the live entity collection directly (no per-tick
    // allocation); the count is tiny (a couple of dummies) so the linear scan is negligible.
    private void RegenEnemies()
    {
        var perTick = _tuning.EnemyRegenPerTick;
        if (perTick <= 0)
        {
            return;
        }

        foreach (var entity in _zone.World.Entities)
        {
            // LIVING-ENEMIES P1: regen the STATIONARY targets only (Dummy/Npc), NOT roaming Monsters — a Monster
            // is attackable but does not heal back this phase (its HP depletes and stays). Gate on the narrower
            // IsRegeneratingEnemy, not IsAttackableEnemy (which now also includes Monster).
            if (MeleeConeResolver.IsRegeneratingEnemy(entity))
            {
                entity.TryRegenHealth(perTick);
            }
        }
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

            if (IsEntityInInterest(actor, candidate, session, _tuning.InterestRadius))
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
                ? "commands: /help, /role, /who, /metrics, /speed <multiplier>, /monster, /stress, /stress status, /stress start [clients] [duration], /stress stop"
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
            case "monster":
                HandleMonsterCommand(sender);
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

    // LIVING-ENEMIES P1: admin dev command /monster — spawns an EntityKind.Monster at the CALLER's current tile
    // (mirroring the SpawnDummies setup but at the sender, like a "/dummy here"), records that tile as the
    // monster's leash HOME, and registers it with the roam AI. The monster carries CharacterStats (full HP) so it
    // shows an overhead HP bar and is hittable (MeleeConeResolver.IsAttackableEnemy now includes Monster). The
    // server then idles it near home and occasionally strolls it within the leash. It spawns on the caller's own
    // tile (always walkable — the caller stands there); replication + client interpolation render it as a moving
    // cube for free. LIVING-ENEMIES P2: it now also AGGROS the nearest player in range, CHASES (leashed to home),
    // and ATTACKS when adjacent (the player TAKES damage — HP floors at 0, no death/respawn yet).
    private void HandleMonsterCommand(ClientSession sender)
    {
        if (!TryGetSessionEntity(sender, out var actor))
        {
            SendSystem(sender, "monster: no controllable entity.");
            return;
        }

        // Rent throws only on the (ushort-space) exhaustion the dummy/resource spawns also rely on never hitting.
        var monster = _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Monster, "Monster", actor.Tile, Direction8.S);
        // Register with the roam AI: the spawn tile is the leash home; start Idle with an initial randomized pause.
        _monsterAi.Register(monster, _serverTick, _tuning.PauseMinTicks, _tuning.PauseMaxTicks, _tuning.MonsterAggroScanIntervalTicks);

        SendSystem(
            sender,
            $"monster: spawned at {monster.Tile.X},{monster.Tile.Y} (home), roamRadius={_tuning.RoamRadius}, pause={_tuning.PauseMinMs}-{_tuning.PauseMaxMs}ms, aggro={_tuning.AggroRadius}, leash={_tuning.ChaseLeash}, atk={_tuning.MonsterAttackDamage}/{_tuning.MonsterAttackCooldownMs}ms.");
        Log.Info($"{sender.DisplayName} spawned a monster at {monster.Tile} (home).");
    }

    // S60 live-tuning handler. ADMIN-GATED (the same role check as /speed and /metrics): a non-admin
    // request is ignored and logged, never applied. An admin request is looked up in the registry, which
    // clamps/validates and applies it to the mutable ServerTuning holder live; the next AOI pass / step
    // reads the new value. Unknown keys are ignored + logged. No echo message in v1 — the client shows the
    // value it sent (note: post-clamp authoritative value is only in the server log). No persistence.
    private void HandleAdminSetTuning(ClientSession sender, string key, double value)
    {
        if (sender.Role != ClientRole.Admin)
        {
            Log.Warn($"Denied AdminSetTuning from non-admin {sender.DisplayName}: {key}={value}.");
            return;
        }

        if (!ServerTuningRegistry.TryApply(_tuning, key, value, out var applied))
        {
            Log.Warn($"Ignored AdminSetTuning from {sender.DisplayName}: unknown/invalid key '{key}' (value {value}).");
            return;
        }

        // Interest radius feeds the precomputed AOI query box; recompute it so a larger live radius still
        // gathers the full candidate superset (a smaller one just queries a tighter box). Cheap + only on
        // apply, never per tick. (The base step cooldown is no longer a live knob — SPEED1 pinned it.)
        if (key == ServerTuningRegistry.InterestRadiusKey)
        {
            _aoiQueryRadiusTiles = ResolveAoiQueryRadiusTiles(_tuning.InterestRadius);
        }

        // COMBAT-TUNING: a combat.* change must reach every client so the wedge mesh, swing-root prediction, and
        // radial cooldown viz re-derive from the new authoritative values — broadcast the full replicated snapshot.
        if (ServerTuningRegistry.IsCombatKey(key))
        {
            BroadcastCombatTuning();
        }

        SendSystem(sender, $"tuning: {key} = {ServerTuningRegistry.Format(applied)} (applied live).");
        Log.Info($"{sender.DisplayName} set tuning {key}={ServerTuningRegistry.Format(applied)} (requested {value}).");
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

            if (IsEntityInInterest(viewerEntity, entity, session, _tuning.InterestRadius))
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // COMBAT-S1 dev-set handler. ADMIN-GATED (same role check as /speed and AdminSetTuning): a non-admin request
    // is ignored and logged, never applied. An admin request sets the CALLER's own local-player vital current
    // value (server clamps to [0, max] inside TrySetStatCurrent); on a real change the authoritative vitals are
    // replicated back to the owner via PlayerStatsMessage so the HUD bars track it. No damage/regen — this is the
    // Stage-1 hook to watch the bars move end-to-end.
    private void HandleAdminSetStat(ClientSession sender, byte stat, int value)
    {
        if (sender.Role != ClientRole.Admin)
        {
            Log.Warn($"Denied AdminSetStat from non-admin {sender.DisplayName}: stat={stat} value={value}.");
            return;
        }

        if (stat > (byte)StatKind.Stamina)
        {
            Log.Warn($"Ignored AdminSetStat from {sender.DisplayName}: unknown stat {stat}.");
            return;
        }

        if (!TryGetSessionEntity(sender, out var entity))
        {
            return;
        }

        if (entity.TrySetStatCurrent((StatKind)stat, value))
        {
            SendPlayerStats(sender, entity);
            Log.Info($"{sender.DisplayName} set stat {(StatKind)stat}={value} (now {entity.Stats}).");
        }
    }

    // COMBAT-S2B: server-authoritative resolution of an attack action. The first real combat path. Flow:
    //   1. Dedup on the session's OWN attack cursor (_lastAttackSeq, ClientSession) — entirely separate from the
    //      movement cursors. A stale/duplicate attack seq is rejected here and never resolves.
    //   2. Resolve the attacker entity; reject if it is still on its INDEPENDENT per-entity attack cooldown
    //      (WorldEntity.TryBeginAttack, ~600 ms) — the cooldown cannot be bypassed by spamming (a rejected attack
    //      mutates nothing and the cursor is already burned, so the re-send is also deduped).
    //   3. FREEAIM: decode the client's continuous aim angle and resolve a GEOMETRIC SECTOR (half-angle + radius)
    //      about the attacker's world position — replacing the facing-derived tile cone. The aim is a client-chosen
    //      continuous value the server validates purely by geometry (like the move direction), so it stays
    //      server-authoritative.
    //   4. Apply the live combat.damage to each ENEMY whose tile-centre falls in the sector — Dummy/Npc only, NEVER another
    //      Player (no friendly fire) and never the attacker itself. The reduced HP rides the existing 2a public-HP
    //      snapshot field, so the target's overhead bar drops automatically (no dedicated reply). HP may reach 0.
    private void HandleAttack(ClientSession session, uint sequence, AttackKind kind, ushort aimAngle, uint authoredTick)
    {
        // (1) Own attack cursor dedup — PARALLEL to, but independent of, the movement cursors. A stale or duplicate
        // attack seq is dropped before any cooldown/cone work. The cursor advances even on a later cooldown reject,
        // so a re-sent (already-seen) attack can never resolve twice.
        if (!session.TryConsumeAttackSequence(sequence))
        {
            return;
        }

        if (!TryGetSessionEntity(session, out var attacker))
        {
            return;
        }

        // Only the melee sector exists this stage; the codec already range-validated the kind, so anything else is a
        // future kind we don't resolve yet.
        if (kind != AttackKind.MeleeCone)
        {
            return;
        }

        // (2) Independent per-entity attack cooldown. A still-cooling attacker is rejected and changes nothing.
        // COMBAT-TUNING: read the cooldown LIVE from _tuning (combat.attackCooldownMs) so an admin tweak takes effect
        // on the next attack; the replicated snapshot keeps the client's radial cooldown indicator matching it.
        if (!attacker.TryBeginAttack(_serverTick, _tuning.AttackCooldownTicks))
        {
            return;
        }

        // SWING-COMMIT: an ACCEPTED swing briefly ROOTS the attacker's MOVEMENT (a committed swing) by bumping its
        // next-eligible MOVEMENT tick forward (a FLOOR, never a shorten). Same machinery as the step cooldown, so
        // the client predictor mirrors it exactly (MmoClient.SendAttack -> LocalPlayerPredictor) using the SAME
        // CombatTuning.RootTicks math.
        //
        // SWING-COMMIT-FIX: anchor the root on the CLIENT's AUTHORED tick (carried on the wire), not on _serverTick
        // (the RECEIVE tick). Under latency the server receives the attack ~d ticks after the client sent + rooted
        // its predictor, so a receive-tick anchor ends the server's root LATER than the predictor's → the predictor
        // steps before the server's root expires → reject → rubberband. Anchoring both sides on the same authored
        // tick (clamped to a window around _serverTick so a hostile client can't root far in the future/past, exactly
        // like TryCommitStepAuthored bounds its authored tick) makes the two root windows identical. This MIRRORS the
        // NET3 authored-tick step commit — same estimator on the client, same clamp bounds on the server.
        // COMBAT-TUNING: the swing-root duration is now the LIVE combat.rootMs (via _tuning.AttackRootTicks, which
        // uses the SAME CombatTuning.RootTicks conversion the client predictor mirrors off the replicated rootMs) —
        // so steady-state both sides root for the identical window. A brief transient mismatch right as rootMs is
        // tweaked is acceptable for a dev tuning tool (the per-client snapshot lands a frame later).
        attacker.ApplyAttackMovementRootAuthored(
            authoredTick,
            _serverTick,
            _tuning.AttackRootTicks,
            AuthoredTickPastWindow,
            AuthoredTickFutureLead);

        // (3+4) FREEAIM: resolve the geometric sector + apply damage. The whole resolution (the radius/angle test
        // against entity world positions, the grid occupancy query, the friendly-fire gate, and the damage) lives in
        // the testable static FreeAimSectorResolver — HandleAttack only owns the cursor dedup + cooldown gate and the
        // aim decode around it.
        // COMBAT-TUNING: half-angle / radius / damage are read LIVE from _tuning (combat.halfAngleDeg /
        // combat.radiusTiles / combat.damage) — the SAME values replicated to the client so the drawn wedge equals
        // the resolved danger area.
        var aimRadians = AimAngle.ToRadians(aimAngle);
        var damage = _tuning.AttackDamage;
        var hits = FreeAimSectorResolver.ResolveAndDamage(
            _zone.World,
            attacker,
            aimRadians,
            _tuning.FreeAimHalfAngleRadians,
            _tuning.FreeAimRadiusTiles,
            damage,
            _attackCandidateScratch,
            _damagedVictimScratch);
        if (hits > 0)
        {
            // COMBAT-QOL: float a cosmetic damage number over each victim that actually lost HP. AOI-gated to the
            // victim's viewers, EXCLUDING the attacker's OWN session — the attacker now PREDICTS its own numbers
            // client-side (instant on swing, no round-trip), so re-sending the server event would double the number.
            // Other AOI observers have no prediction, so they still receive it. Cosmetic only — the authoritative HP
            // already rode the snapshot via ApplyDamage. Regen never reaches here, so only real damage pops a number.
            foreach (var damaged in _damagedVictimScratch)
            {
                BroadcastDamageEvent(damaged.Victim, damaged.Amount, session);
            }

            Log.Info($"{session.DisplayName} free-aim hit {hits} target(s) for {damage} each (aim {aimRadians:F2} rad).");
        }
    }

    // COMBAT-QOL: send a cosmetic DamageEventMessage for `victim` to every authenticated viewer whose AOI currently
    // includes it — the SAME viewer-scoping as BroadcastMovementSpeedChanged, so a damage number appears exactly
    // where the entity is replicated and nowhere else. UNRELIABLE: a dropped number is harmless (the next snapshot
    // already carries the true HP), and reliable retransmit would only add latency. A viewer that does not yet know
    // the entity is skipped — it has no visual to float a number over and the spawn carries the HP.
    //
    // FREEAIM-PREDICT: `excludeSession` (the ATTACKER) is skipped — the attacker PREDICTS its own damage numbers
    // client-side (instant on swing), so sending it the event too would pop a SECOND number. Other observers have no
    // such prediction, so they still get the event. Pass null to broadcast to every viewer (no exclusion).
    private void BroadcastDamageEvent(WorldEntity victim, int amount, ClientSession? excludeSession = null)
    {
        var newHealth = (ushort)Math.Clamp(victim.Stats.Health, 0, ushort.MaxValue);
        var message = new DamageEventMessage(victim.NetworkId, amount, newHealth);
        foreach (var session in _sessions.Values)
        {
            if (ReferenceEquals(session, excludeSession))
            {
                continue;
            }

            if (!session.IsAuthenticated || !session.KnowsEntity(victim.NetworkId))
            {
                continue;
            }

            if (!TryGetSessionEntity(session, out var viewerEntity))
            {
                continue;
            }

            if (IsEntityInInterest(viewerEntity, victim, session, _tuning.InterestRadius))
            {
                TrySend(session.Peer, message, DeliveryMethod.Unreliable);
            }
        }
    }

    // Replicates an entity's authoritative vitals to its OWNER. Owner-only + reliable-ordered, like the inventory
    // snapshot — vitals stay off the hot snapshot path. Sent on login (initial truth) and on every change.
    private void SendPlayerStats(ClientSession session, WorldEntity entity)
    {
        TrySend(session.Peer, new PlayerStatsMessage(entity.Stats), DeliveryMethod.ReliableOrdered);
    }

    // COMBAT-TUNING: replicate the current combat feel-knobs to one client. Reliable-ordered, like PlayerStats —
    // sent on login (initial truth) and (via BroadcastCombatTuning) on every combat.* change. The snapshot is the
    // single source the client mirrors; its wedge/predictor/cooldown all re-derive from it.
    private void SendCombatTuning(ClientSession session)
    {
        TrySend(session.Peer, new CombatTuningMessage(_tuning.CombatSnapshot), DeliveryMethod.ReliableOrdered);
    }

    // COMBAT-TUNING: push the current combat snapshot to every authenticated client. Called when a combat.* tuning
    // key changes so all clients re-derive the wedge/predictor/cooldown from the new authoritative values. Combat
    // tuning is global (not per-entity/AOI-scoped), so every authenticated session gets it regardless of AOI.
    private void BroadcastCombatTuning()
    {
        var message = new CombatTuningMessage(_tuning.CombatSnapshot);
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated)
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
            _tuning.StepCooldownTicks,
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

            // UO1: a client-driven session paces its OWN movement via the per-step StepCommitRequest stream. The
            // server must NOT also step it from the held MoveIntent here, or the held-intent pacer and the commits
            // would both advance the entity (double-stepping / 2x speed). The MoveIntent is still recorded (for
            // facing/keepalive) — it is just not paced. Default sessions are unaffected.
            if (session.ClientDrivenMovement)
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

    // LIVING-ENEMIES P1: the per-tick monster AI pass (sibling to StepHeldMovementIntents). For each Monster
    // entity, the MonsterRoamAi advances its Idle↔Roaming state machine and, when Roaming, takes ONE greedy
    // tile-step toward its leashed destination through Zone.TryStep — the SAME path players use, so facing /
    // StepSequence / AOI migration / replication / client interpolation all happen for free. The TryStep cooldown
    // gate paces the monster to one tile per step cooldown (it is fed each tick but only steps on cadence), and
    // the AI's pause timers keep it Idle (still) most of the time. Roam radius + pause bounds are read fresh from
    // the live _tuning each tick so a monster.* admin retune takes effect immediately. Monsters move at the base
    // step cadence (SpeedMultiplier 1.0 → EffectiveStepCooldownTicks).
    //
    // Iterates the live entity collection directly (no per-tick allocation); the count is tiny. TryStep mutates a
    // monster's Tile and migrates its spatial-grid bucket but never adds/removes a dictionary entry, so iterating
    // Entities while stepping is safe — same as RegenEnemies.
    private void StepMonsterAi()
    {
        if (_monsterAi.TrackedCount == 0)
        {
            return;
        }

        // Snapshot the live tunables ONCE per pass (read from _tuning so a monster.* admin retune takes effect on the
        // next tick). LIVING-ENEMIES P2: aggro/chase/attack knobs ride alongside the P1 roam knobs; de-aggro range and
        // the aggro-scan cadence are DERIVED in ServerTuning (hysteresis + perf throttle coupled to their sources).
        var tunables = new MonsterRoamAi.Tunables(
            RoamRadius: _tuning.RoamRadius,
            PauseMinTicks: _tuning.PauseMinTicks,
            PauseMaxTicks: _tuning.PauseMaxTicks,
            AggroRadius: _tuning.AggroRadius,
            DeaggroRadius: _tuning.MonsterDeaggroRadius,
            ChaseLeash: _tuning.ChaseLeash,
            AttackRange: _tuning.AttackRange,
            AttackDamage: _tuning.MonsterAttackDamage,
            AttackCooldownTicks: _tuning.MonsterAttackCooldownTicks,
            AggroScanIntervalTicks: _tuning.MonsterAggroScanIntervalTicks);

        foreach (var entity in _zone.World.Entities)
        {
            if (entity.Kind != EntityKind.Monster)
            {
                continue;
            }

            _monsterAi.StepMonster(
                entity,
                _serverTick,
                EffectiveStepCooldownTicks(entity),
                tunables);
        }
    }

    // LIVING-ENEMIES P2: the AI's aggro-scan callback — find the NEAREST ALIVE PLAYER within `aggroRadius` (Chebyshev)
    // of `monster`, via the SAME spatial index the combat resolver uses (GatherInterestCandidates), so occupancy can
    // never diverge from AOI/replication. Players only (never another monster/dummy/resource), and only alive ones
    // (Health > 0 — a downed player at 0 HP is not a fresh aggro target; an already-chasing monster keeps attacking it
    // via the resolve path, but a NEW acquisition skips it). Returns the closest by Chebyshev distance, ties broken by
    // the smaller entity id for determinism. THROTTLED by the AI (it only calls this every ~0.5 s per monster), so the
    // per-tick scan the P1 review flagged is avoided.
    private bool FindMonsterAggroTarget(WorldEntity monster, int aggroRadius, out ulong targetId, out TileCoord targetTile)
    {
        _zone.World.GatherInterestCandidates(monster.Tile, aggroRadius, _monsterAggroScratch);

        var bestDist = int.MaxValue;
        WorldEntity? best = null;
        foreach (var candidate in _monsterAggroScratch)
        {
            if (candidate.Kind != EntityKind.Player || candidate.Stats.Health <= 0)
            {
                continue;
            }

            var dist = Math.Max(
                Math.Abs(candidate.Tile.X - monster.Tile.X),
                Math.Abs(candidate.Tile.Y - monster.Tile.Y));
            if (dist > aggroRadius)
            {
                continue;
            }

            if (dist < bestDist || (dist == bestDist && (best is null || candidate.Id < best.Id)))
            {
                bestDist = dist;
                best = candidate;
            }
        }

        if (best is null)
        {
            targetId = 0;
            targetTile = default;
            return false;
        }

        targetId = best.Id;
        targetTile = best.Tile;
        return true;
    }

    // LIVING-ENEMIES P2: the AI's target-resolve callback — look up a chased target's CURRENT tile + alive flag so the
    // chase re-reads the player's live position each step and detects target-lost (despawn / logout) for de-aggro.
    // Returns false if the entity no longer exists at all; `alive` is its Health > 0 (a player whose HP hit 0 is still
    // resolvable but not alive → the AI de-aggros and returns home, since there is no death/respawn this phase).
    private bool TryResolveMonsterTarget(ulong targetId, out TileCoord targetTile, out bool alive)
    {
        if (_zone.World.TryGet(targetId, out var target) && target.Kind == EntityKind.Player)
        {
            targetTile = target.Tile;
            alive = target.Stats.Health > 0;
            return true;
        }

        targetTile = default;
        alive = false;
        return false;
    }

    // LIVING-ENEMIES P2: the AI's attack callback — the monster faces its target and deals `attackDamage` to the
    // PLAYER through the SAME ApplyDamage + DamageEventMessage path a player's attack uses (no combat fork). The AI
    // owns the WHEN (adjacency + the monster's own attack cooldown); this owns the HOW. The damage number is broadcast
    // to the victim AND nearby viewers — the victim is the PLAYER, NOT the attacker, so it is NOT excluded (it has no
    // client-side prediction of incoming damage, unlike its own outgoing swings). HP floors at 0 in ApplyDamage; a
    // 0-HP player simply takes a no-op clamp on further hits (no death/respawn — Phase 3+).
    private void ApplyMonsterAttack(WorldEntity monster, ulong targetId, int attackDamage)
    {
        if (!_zone.World.TryGet(targetId, out var target))
        {
            return;
        }

        // Face the victim (turn-in-place, no move) so the attack reads on the client; bumps StateRevision to replicate.
        // Sign-of-delta → the 8-direction facing (the same greedy mapping the chase step uses); same-tile leaves
        // facing unchanged (can't happen — the AI only attacks at Chebyshev >= 1, but guard anyway).
        if (TileDeltaToFacing(monster.Tile, target.Tile) is { } facing)
        {
            monster.TrySetFacing(facing);
        }

        // Authoritative damage rides the snapshot HP field (the HUD bar falls). A real change floats a number; a hit
        // on an already-0-HP player is a no-op (no number, no spam). Broadcast to ALL viewers incl. the victim.
        if (target.ApplyDamage(attackDamage))
        {
            BroadcastDamageEvent(target, attackDamage);
            Log.Info($"Monster {monster.NetworkId} hit {target.DisplayName} for {attackDamage} (hp now {target.Stats.Health}).");
        }
    }

    // LIVING-ENEMIES P2: the 8-direction facing from `from` toward `to` by the SIGN of each axis delta (the same
    // greedy mapping MonsterRoamAi uses to step). Null only when the two tiles coincide (no facing). Server-local so
    // the server doesn't depend on the client's CursorHeading.
    private static Direction8? TileDeltaToFacing(TileCoord from, TileCoord to)
    {
        var sx = Math.Sign(to.X - from.X);
        var sy = Math.Sign(to.Y - from.Y);
        return (sx, sy) switch
        {
            (0, -1) => Direction8.N,
            (1, -1) => Direction8.NE,
            (1, 0) => Direction8.E,
            (1, 1) => Direction8.SE,
            (0, 1) => Direction8.S,
            (-1, 1) => Direction8.SW,
            (-1, 0) => Direction8.W,
            (-1, -1) => Direction8.NW,
            _ => null,
        };
    }

    // S103 commit-step on release. A client whose cosmetic render glided past its commit threshold onto the
    // next tile at key-release asks the server to finish that one step early (instead of snapping back). Validate
    // the sequence (on the dedicated commit cursor, NET6 — so a stale/duplicate commit can't fire twice and a commit
    // never resurrects a stopped intent), resolve the entity, then attempt a single server-validated commit step
    // with the anti-cheat floor. On accept the tile advances + StepSequence bumps, so the next snapshot's confirmed
    // tile + RecipientStepSeq carry the result to the client (no dedicated reply). On reject nothing changes and the
    // snapshot still shows the old tile (the client reads that as "snap back"). Tracing mirrors the held-step path.
    // NET1 Stage 1: ingest a redundant-unreliable MoveInput packet. The packet carries the newest input
    // (HeadSeq/Moving/Direction) plus a window of prior inputs as deltas off HeadSeq. We reconstruct each
    // input's sequence, then apply them in ASCENDING seq order through the EXISTING TryUpdateMoveIntent,
    // which dedups (seq <= LastMoveSeq dropped) and advances the held intent. Because every packet repeats
    // the full window, a dropped packet's intermediate state change is recovered from any later packet that
    // still carries it — no head-of-line stall, no retransmit bunching. The stepping model is untouched:
    // StepHeldMovementIntents still paces the entity off the held intent this fed.
    private void HandleMoveInput(ClientSession session, MoveInputMessage input)
    {
        foreach (var (seq, moving, direction) in ExtractFreshMoveInputs(input, session.LastMoveSeq))
        {
            // The EXISTING held-intent path dedups (seq <= LastMoveSeq dropped) and advances the cursor; we
            // pre-filtered + ordered so each fresh seq applies exactly once, oldest-first, landing on the head.
            session.TryUpdateMoveIntent(seq, moving, direction, _serverTick);
        }
    }

    // NET1 Stage 1 (pure, unit-testable): given a redundant MoveInput packet and the last seq already accepted,
    // returns the fresh inputs (seq > lastSeq) in ASCENDING seq order — head plus window entries reconstructed
    // from their deltas (entrySeq = HeadSeq - SeqDelta). Already-seen and malformed (delta 0 / underflowing)
    // entries are dropped. Applying the result oldest-first feeds the held-intent path each new input once and
    // ends on the newest (head) state; a "dropped head" packet's state change is recovered from a later
    // packet's window because that window still carries it.
    internal static IReadOnlyList<(uint Seq, bool Moving, Direction8 Direction)> ExtractFreshMoveInputs(
        MoveInputMessage input,
        uint lastSeq)
    {
        var fresh = new List<(uint Seq, bool Moving, Direction8 Direction)>(input.Window.Count + 1);
        if (input.HeadSeq > lastSeq)
        {
            fresh.Add((input.HeadSeq, input.Moving, input.Direction));
        }

        for (var i = 0; i < input.Window.Count; i++)
        {
            var entry = input.Window[i];
            if (entry.SeqDelta == 0 || entry.SeqDelta > input.HeadSeq)
            {
                // 0 would alias the head; a delta past HeadSeq underflows — drop the malformed entry.
                continue;
            }

            var entrySeq = input.HeadSeq - entry.SeqDelta;
            if (entrySeq > lastSeq)
            {
                fresh.Add((entrySeq, entry.Moving, entry.Direction));
            }
        }

        // Ascending by seq (small n). Distinct seqs are guaranteed by construction (head once; window deltas
        // are distinct off a single head), so a stable insertion sort suffices.
        fresh.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        return fresh;
    }

    // NET2/NET3: ingest a redundant-unreliable StepCommitBatch. The packet carries the newest committed step
    // (HeadSeq/HeadTick/Direction) plus a window of prior committed steps as seq/tick deltas off the head. We
    // reconstruct each commit's (sequence, authored tick), then apply them in ASCENDING seq order — the cursor
    // dedups (TryConsumeCommitSequence drops seq <= LastCommitSeq, NET6) and each fresh commit is applied at its AUTHORED
    // tick (NET3: TryCommitStepAuthored gates/schedules the cooldown on authored time, not the receive tick). This
    // is the loss-desync fix: a dropped commit recovered BUNDLED with the next ([C2,C3] in one packet) used to land
    // at the same receive tick — C2 accepted, C3 rejected "too early". Keying the schedule on authored ticks lets
    // C2(authored T+3) advance the schedule to T+6 so C3(authored T+6) is then accepted. Forward replay only (the
    // window delivers recovered commits in order); a genuine reorder is dropped gracefully (Stage 4 owns rollback).
    private void HandleStepCommitBatch(ClientSession session, StepCommitBatchMessage batch)
    {
        // NET6: gate the commit window on the COMMIT cursor (LastCommitSeq), not the intent cursor. The two are
        // now independent, so an interleaved STOP/direction-change intent (which advances LastMoveSeq) can no
        // longer pre-dedup an unconfirmed commit out of this window before its re-send lands.
        foreach (var (seq, authoredTick, direction) in ExtractFreshStepCommits(batch, session.LastCommitSeq))
        {
            HandleAuthoredStepCommit(session, seq, authoredTick, direction);
        }
    }

    // NET2/NET3 (pure, unit-testable): given a redundant StepCommitBatch and the last seq already accepted on the
    // COMMIT cursor (NET6 — the caller passes session.LastCommitSeq, not the intent cursor), returns the fresh
    // commits (seq > lastSeq) in ASCENDING seq order — head plus window
    // entries reconstructed from their deltas (entrySeq = HeadSeq - SeqDelta; entryTick = HeadTick - TickDelta).
    // Already-seen and malformed (seq delta 0 / underflowing, or a tick delta that underflows the head tick) entries
    // are dropped. Applying the result oldest-first feeds the commit path each new step once, each carrying the
    // AUTHORED tick the client banked it on; a "dropped head" batch's commit is recovered from a later batch's
    // window with its authored tick intact.
    internal static IReadOnlyList<(uint Seq, uint AuthoredTick, Direction8 Direction)> ExtractFreshStepCommits(
        StepCommitBatchMessage batch,
        uint lastSeq)
    {
        var fresh = new List<(uint Seq, uint AuthoredTick, Direction8 Direction)>(batch.Window.Count + 1);
        if (batch.HeadSeq > lastSeq)
        {
            fresh.Add((batch.HeadSeq, batch.HeadTick, batch.Direction));
        }

        for (var i = 0; i < batch.Window.Count; i++)
        {
            var entry = batch.Window[i];
            if (entry.SeqDelta == 0 || entry.SeqDelta > batch.HeadSeq)
            {
                // 0 would alias the head; a delta past HeadSeq underflows — drop the malformed entry.
                continue;
            }

            if (entry.TickDelta > batch.HeadTick)
            {
                // A tick delta past HeadTick underflows the authored tick — drop the malformed entry. (TickDelta 0
                // would tie the head's tick, which the client never emits since authored ticks increase, but it is
                // harmless if it slips through — the authored-tick spacing gate handles a too-close tick.)
                continue;
            }

            var entrySeq = batch.HeadSeq - entry.SeqDelta;
            if (entrySeq > lastSeq)
            {
                fresh.Add((entrySeq, batch.HeadTick - entry.TickDelta, entry.Direction));
            }
        }

        // Ascending by seq (small n). Distinct seqs are guaranteed by construction (head once; window deltas
        // are distinct off a single head), so a stable sort suffices. Seq order == authored-tick order (both
        // increase monotonically per accepted step), so this also yields ascending authored ticks.
        fresh.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        return fresh;
    }

    // NET3: apply ONE fresh commit at its authored tick. The cursor (TryConsumeCommitSequence) dedups; on accept the
    // entity steps via the authored-tick path (TryCommitStepAuthored), which clamps the authored tick to a recent
    // window and enforces cooldown SPACING on authored ticks (anti-speedhack) before scheduling on it.
    private void HandleAuthoredStepCommit(ClientSession session, uint sequence, uint authoredTick, Direction8 direction)
    {
        if (!session.TryConsumeCommitSequence(sequence, _serverTick))
        {
            return;
        }

        if (!TryGetSessionEntity(session, out var entity))
        {
            return;
        }

        if (_zone.TryCommitStepAuthored(
                entity,
                direction,
                authoredTick,
                _serverTick,
                EffectiveStepCooldownTicks(entity),
                AuthoredTickPastWindow,
                AuthoredTickFutureLead,
                out var result))
        {
            MarkDirtyDurableTile(entity);
        }

        if (result.CooldownElapsed)
        {
            // NET6: a commit step's seq is the COMMIT cursor (LastCommitSeq), not the intent cursor — report it
            // so the trace's seq column tracks the stream that actually advanced.
            _movementTrace.MoveStep(session, session.LastCommitSeq, result, _serverTick);
        }

        // DIAG1: surface the server-side recovery-chain counters on EVERY commit (accept AND reject). The
        // future-cap reject ("commit_too_early") carries CooldownElapsed=false, so MoveStep above skips it — this
        // line ensures the rejects-climbing-while-srvSeq-stalls (link-2) signal is always traced. Measurement only.
        _movementTrace.CommitCounters(session, entity, result.Reason, _serverTick);
    }

    // S103: the reliable per-step StepCommitRequest path (still defined; the wire send is now StepCommitBatch). Keeps
    // the receive-time S103 commit-step floor (TryCommitStep) for any client still using the legacy single-commit
    // message. The redundant StepCommitBatch path uses HandleAuthoredStepCommit (NET3) instead.
    private void HandleStepCommit(ClientSession session, uint sequence, Direction8 direction)
    {
        if (!session.TryConsumeCommitSequence(sequence, _serverTick))
        {
            return;
        }

        if (!TryGetSessionEntity(session, out var entity))
        {
            return;
        }

        if (_zone.TryCommitStep(entity, direction, _serverTick, EffectiveStepCooldownTicks(entity), CommitAcceptFraction, out var result))
        {
            MarkDirtyDurableTile(entity);
        }

        if (result.CooldownElapsed)
        {
            // NET6: report the commit cursor (see HandleAuthoredStepCommit).
            _movementTrace.MoveStep(session, session.LastCommitSeq, result, _serverTick);
        }

        // DIAG1: surface the server-side recovery-chain counters on every legacy commit too (see the authored path).
        _movementTrace.CommitCounters(session, entity, result.Reason, _serverTick);
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
        uint recipientStepSeq,
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
            recipientStepSeq,
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
