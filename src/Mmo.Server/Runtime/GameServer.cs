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
    // serverTick(4) + snapshotSeq(4) + recipientStepSeq(4) + lastInputSeq(4, v36) + totalEntities(2) + isComplete(1)
    // + chunkIndex(2) + chunkCount(2). The +4 over v35 is the new continuous-migration LastInputSeq header field.
    private const int SnapshotHeaderBytes = 21;
    // Per-entity snapshot state wire size: networkId(2) + qx(2) + qy(2) + facing(1) + depleted(1) = 8, plus
    // COMBAT-S2A's public HP Health(2) + MaxHealth(2) = 12. CONTINUOUS MIGRATION (v36): qx/qy are the fixed-point
    // Q12.4 continuous position (two shorts) — same 4 bytes as the v35 tile shorts, so the per-entity size is unchanged.
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

    // Authored-tick clamp window for the attack-movement-ROOT (ApplyAttackMovementRootAuthored). The swing root is
    // anchored on the CLIENT's AUTHORED tick (carried on the wire) so the server and the client predictor compute the
    // identical root window under latency, but the authored tick is clamped to [serverTick - AuthoredTickPastWindow,
    // serverTick + AuthoredTickFutureLead] first so a far-past/far-future (tamper / clock skew) authored tick can't
    // poison the schedule. (Phase 1: formerly also bounded the NET3 authored-tick STEP COMMIT, which is now retired —
    // these constants remain solely for the swing-root anchor.)
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

    // CONTINUOUS MIGRATION (Phase 3, v36): anti-speedhack clamps for the per-input continuous move. The client now
    // sends DtSeconds (how much sim-time a frame represents), which the server integrates by — so it must be bounded
    // two ways:
    //   * MaxMoveInputDtSeconds: a per-input SANITY clamp. One frame's dt is tiny (~1/60s); 0.25s caps a single
    //     input to ~5 server ticks of motion so a lone huge-dt packet can't teleport (and a legitimately laggy
    //     frame still integrates fully).
    //   * MoveDtBurstAllowanceSeconds: the per-peer wall-clock dt BUDGET ceiling. The budget accrues REAL elapsed
    //     time each tick (capped at this) and each integrated input debits it; an input may consume only the
    //     remaining budget. So the TOTAL integrated sim-time over any window cannot exceed real elapsed time + this
    //     allowance — a flood of max-dt inputs advances ≈ real-time distance, never faster. The allowance (~0.4s)
    //     absorbs network/frame jitter (a burst of buffered inputs after a hitch) without ever permitting a
    //     sustained over-rate.
    // CONTINUOUS MIGRATION (Phase 4): the per-input dt SANITY clamp is now the SHARED Mmo.Shared.Domain.ContinuousMovement
    // .MaxInputDtSeconds — the SAME constant the client predictor clamps its predicted (and buffered) dt to AND the send
    // path clamps the frame dt to, so buffered == sent == server-integrated dt under normal play (the dt-alignment
    // linchpin; replay reproduces the server path with no correction).
    private const double MaxMoveInputDtSeconds = ContinuousMovement.MaxInputDtSeconds;
    private const double MoveDtBurstAllowanceSeconds = 0.4d;

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

    // LOOT P4b: reusable buffer of corpse entity ids due to decay this tick (collected, then despawned outside the
    // dictionary enumeration so we don't mutate _corpses while iterating it). Cleared each pass; no per-tick alloc.
    private readonly List<ulong> _corpseDecayScratch = [];

    // LIVING-ENEMIES P1: the server-side leashed-roam brain for EntityKind.Monster. Owns every monster's per-AI
    // state + a seeded PRNG (seeded off the map seed so a given world's roaming is reproducible in tests/repro
    // runs), and steps each monster through the SAME Zone.TryStep path players use. Constructed after _zone since
    // it closes over _zone.IsWalkable / _zone.TryStep. Stepped each tick by StepMonsterAi (a sibling pass to
    // StepHeldMovementIntents), paced off the step cooldown — so a monster never steps every tick.
    private readonly MonsterRoamAi _monsterAi;

    // LIVING-ENEMIES P2-POLISH: the table of monster TYPES (named templates — slime now) + their live-tunable,
    // replicated per-type tuning. Replaces the former single global monster.* tuning block: a spawned monster
    // remembers its type (via _monsterTypeOf), and StepMonsterAi reads that type's Tunables + SpeedMultiplier each
    // tick. Tick-rate-fixed at construction (for the tick-quantised pause/cooldown derivations).
    private readonly MonsterTypeRegistry _monsterTypes;

    // LOOT P4a: the shared loot-table store (slime_loot + the nested rare_material_pool). KillMonster resolves
    // the dead monster's type -> LootTableId -> table and rolls it. P4a only LOGS the rolled stacks (P4b spawns
    // the corpse that holds them); nothing is delivered to any inventory here.
    private readonly LootTableRegistry _lootTables = LootTableRegistry.CreateDefault(ItemRegistry.Default);

    // LOOT P4a: the seeded RNG for loot rolls, off the map seed (mixed with a constant so it isn't lockstep
    // with the roam AI's same-seed stream). Single-threaded tick loop => no lock needed. Deterministic for a
    // given world; the headless tests roll their OWN seeded Random so they don't depend on this stream.
    private readonly Random _lootRng;

    // LOOT P4b: the contribution ledger (group-loot groundwork). As players damage a monster, HandleAttack records
    // the damaging player's durable CharacterId here; on death KillMonster snapshots the contributor set onto the
    // corpse's eligibleLooters and forgets the entry. Solo => the eligible set is just the killer. Pure + tiny.
    private readonly ContributionLedger _contributionLedger = new();

    // LOOT P4b: the live corpses' server-side loot payloads, keyed by the Corpse WorldEntity's ENTITY id (stable for
    // the corpse's life; the network id can be reused after despawn). KillMonster adds one; the interact loot-all and
    // the decay pass remove + despawn. The contents stay SERVER-SIDE (never replicated) this phase. Tiny.
    private readonly Dictionary<ulong, Corpse> _corpses = [];

    // Per-monster type membership (entity id -> its type). Set on spawn; REMOVED on death (LIVING-ENEMIES P3 —
    // KillMonster calls _monsterTypeOf.Remove + _monsterAi.Forget, fixing the former add-only leak flagged in
    // todo/monster-types-followups.md). The AI step reads it to build that monster's Tunables from the live per-type
    // values. Tiny (a handful of monsters).
    private readonly Dictionary<ulong, MonsterType> _monsterTypeOf = [];

    // LIVING-ENEMIES P3: the PERSISTENT monster spawners, keyed by spawner id. A spawner owns a monster, respawns it
    // after the type's delay when it dies, and is the red-tile anchor (the monster's leash home = the spawner tile).
    // `/monster` creates one. Server objects, not replicated entities — the red marker is sent via SpawnerMarkerMessage
    // keyed by SpawnerId (AOI-driven), so it survives the monster's death/respawn. Tiny (a handful).
    private readonly Dictionary<uint, MonsterSpawner> _spawners = [];

    // Reverse map: a live monster's entity id -> the spawner that owns it, so on death we find the spawner in O(1) to
    // schedule its respawn. Kept in lockstep with each spawner's LiveMonsterId (added on spawn, removed on death).
    private readonly Dictionary<ulong, MonsterSpawner> _spawnerOfMonster = [];

    // Monotonic spawner-id allocator (distinct id space from entity network ids — these key the red marker only).
    private uint _nextSpawnerId = 1;

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
        // LIVING-ENEMIES P2-POLISH: the monster-type registry (seeds the one "slime" type). Tick rate fixes the
        // pause/cooldown/scan tick-quantisation, mirroring how ServerTuning derived the old global monster.* ticks.
        // CONTINUOUS MIGRATION (Phase 8): built BEFORE the AI so the hop locomotion's live hop-distance provider can
        // read the default type's HopDistanceUnits fresh each hop.
        _monsterTypes = new MonsterTypeRegistry(options.TickRate);
        // LIVING-ENEMIES P1 + CONTINUOUS MIGRATION (Phase 8): seed the monster roam AI off the map seed so a given
        // world replays the same roaming (deterministic for repro/tests). Navigation is now CONTINUOUS (Euclidean
        // ranges, sub-tile targets) but movement HOPS — the injected HopLocomotion leaps the monster a collision-valid
        // HopDistanceUnits per cadence through the SAME swept-circle wall derivation + body radius players collide at
        // (Zone.QueryNearbyWalls + ContinuousCollision), with Velocity left at Zero (the sparse-update jump preserved).
        // Hop distance + body radius are read FRESH each hop so a live retune applies next tick.
        _monsterAi = new MonsterRoamAi(
            options.MapSeed,
            _zone.IsWalkable,
            new HopLocomotion(
                () => _monsterTypes.Default.HopDistanceUnits,
                () => _tuning.BodyRadiusUnits,
                _zone.QueryNearbyWalls,
                _zone.ApplyMonsterLanding),
            FindMonsterAggroTarget,
            TryResolveMonsterTarget,
            ApplyMonsterAttack);
        // LOOT P4a: seed the loot RNG off the map seed (mixed so it's not the roam AI's identical stream).
        _lootRng = new Random(unchecked(options.MapSeed * 31 + 0x100712));
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
        // CONTINUOUS MIGRATION (Phase 4, v37): replicate the live authoritative body radius (the ServerTuning knob, default
        // 0.5) so the client predictor collides against the SAME radius the server integrates with (the wall-determinism gap).
        TrySend(peer, new ServerHelloMessage(ServerName, ProtocolCodec.Version, _options.TickRate, _options.StepCooldownMs, _options.InterestRadius, (float)_tuning.BodyRadiusUnits), DeliveryMethod.ReliableOrdered);
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
                QueueTileSave(session, entity.TileCoord);
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

        // LIVING-ENEMIES P3: while a player is DEAD (the brief respawn delay), drop its action inputs — movement
        // (intent/input/commit), movement-mode flips, and attacks — so it can't act while downed. Chat/ack/admin still
        // flow (a dead admin can still see/ack snapshots and use the panel). The session is server-paced, so suppressing
        // the inputs is enough; the entity is teleported + refilled on respawn.
        if (session.IsAuthenticated && session.IsDead && IsSuppressedWhileDead(message.Type))
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
                    // CONTINUOUS MIGRATION (Phase 3, v36): per-input continuous movement — integrate this input by its
                    // own dt ON THE RECEIVE PATH (the experiment model), with the anti-speedhack clamps applied.
                    HandleMoveIntent(session, intent);
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
            case LootActionMessage loot:
                if (session.IsAuthenticated)
                {
                    HandleLootAction(session, loot.CorpseNetworkId, loot.Kind, loot.TemplateKey);
                }
                break;
            default:
                TrySend(peer, new ServerErrorMessage("unsupported_message", $"Unsupported {message.Type}."), DeliveryMethod.ReliableOrdered);
                break;
        }
    }

    // LIVING-ENEMIES P3: the action message types suppressed while a player is dead (movement + attacks). Non-action
    // messages (chat, snapshot ack, admin tuning/stat, hello/login) are NOT suppressed so the client stays responsive.
    private static bool IsSuppressedWhileDead(MessageType type) =>
        type is MessageType.MoveIntent or MessageType.Attack
			or MessageType.InteractRequest or MessageType.LootAction;

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
                        // Seed the player's tiles/sec speed stat — LIVE in Phase 1 (the integrator reads it).
                        RefreshSpeedStat(entity);
                        _metrics.RecordLogin(true, Stopwatch.GetElapsedTime(loginStartedAt));
                        TrySend(peer, new LoginResultMessage(true, character.CharacterId, character.DisplayName, role, entity.TileCoord, ""), DeliveryMethod.ReliableOrdered);
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
                        // LIVING-ENEMIES P2-POLISH: replicate the per-monster-TYPE tuning so an admin's F1 Monster tab
                        // can list the types (dropdown) and show + edit the live values immediately on login.
                        SendMonsterTuning(current);
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
            tile = entity.TileCoord;
            // Hand the live in-memory inventory to the taking-over login so any not-yet-flushed harvest
            // gains survive the relogin. FlushInventory still enqueues its dirty changes for persistence;
            // the quantities live on this same object, so nothing is lost either way.
            inventory = entity.Inventory;
            _networkIds.Return(entity.NetworkId);
            QueueTileSave(session, entity.TileCoord);
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
            // CONTINUOUS MIGRATION (Phase 3, v36): PLAYER integration is now 100% input-driven (HandleMoveIntent on the
            // receive path). This tick pass no longer integrates — it (a) credits each session's anti-speedhack dt
            // budget by the elapsed tick time and (b) does the keepalive/stop housekeeping (a wedged "moving" client
            // is force-stopped; a stale player's velocity is zeroed for AOI/animation). Monsters keep the fixed-cadence
            // tile-step path below, UNTOUCHED.
            CreditMoveDtBudgetsAndKeepalive();
            // LIVING-ENEMIES P1: a sibling movement pass that steps each roaming Monster off the step cooldown
            // (the TILE-STEP path — monsters stay tile-stepped; the continuous integrator is PLAYER-only), so
            // monsters idle near home and occasionally stroll within a leash.
            StepMonsterAi();
        }

        using (tickBudget.Measure(TickBudgetCategory.Other))
        {
            RespawnResourceNodes();
            RegenEnemies();
            // LIVING-ENEMIES P3: spawn fresh monsters whose spawner's respawn delay elapsed, and respawn dead players
            // whose downed window elapsed. Both poll tiny sets (spawners / sessions) and no-op when nothing is due.
            RespawnMonsters();
            RespawnPlayers();
            // LOOT P4b: despawn any corpse whose decay deadline has arrived (unlooted corpses don't linger forever).
            DecayCorpses();
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
            // LIVING-ENEMIES P3: sync the persistent spawner red-tile markers for this viewer (AOI-driven). Only when
            // there are spawners to consider; the viewer's own entity is the AOI center.
            if (_spawners.Count > 0 || session.KnownSpawnerIds.Count > 0)
            {
                if (TryGetSessionEntity(session, out var viewerEntity))
                {
                    SyncSpawnerMarkers(session, viewerEntity);
                }
            }
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
        _zone.World.GatherInterestCandidates(recipientEntity.TileCoord, _aoiQueryRadiusTiles, _aoiCandidateScratch);

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
        // CONTINUOUS MIGRATION (Phase 3, v36): the recipient-scoped last INTEGRATED input seq rides the header too
        // (after RecipientStepSeq), so the Phase-4 predictor can trim/replay its unacked input buffer against it.
        var recipientLastInputSeq = recipient.LastInputSeq;

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
                SendKeepAliveSnapshot(recipient, recipientStepSeq, recipientLastInputSeq, visible.Count, tickBudget, ref sentBytes, ref sentPackets);
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
                    recipientLastInputSeq,
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
        uint recipientLastInputSeq,
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
            // this the seq would never reach an idle client. CONTINUOUS MIGRATION (v36): the LastInputSeq rides
            // the keep-alive too (same reason — an idle player's input ack must still reach the client).
            packet = _snapshotEncodeBuffer.EncodeWorldSnapshot(
                _serverTick,
                snapshotSequence,
                recipientStepSeq,
                recipientLastInputSeq,
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
                entity.TileCoord,
                entity.Facing,
                EffectiveStepCooldownMs(entity));
            TrySend(recipient.Peer, packet, DeliveryMethod.ReliableOrdered, MessageType.EntitySpawn);
            recipient.RememberKnownEntity(entity.NetworkId);
        }

        // LIVING-ENEMIES P3: the red anchor is now the persistent SPAWNER (not the transient monster), replicated via
        // a separate AOI-driven SpawnerMarker pass below — so it stays put across the monster's death/respawn.
    }

    // LIVING-ENEMIES P3: per-recipient AOI sync of the persistent SPAWNER red-tile markers. A spawner is a server
    // object, not a world entity, so it has no EntitySpawn; this is the parallel "spawn/despawn" for its marker.
    // For each spawner whose tile is within the viewer's interest radius and that the viewer doesn't yet know, send
    // SpawnerMarker(Active=true) (place the red tile); for each KNOWN spawner that has left AOI or no longer exists,
    // send Active=false (drop it). Reliable-ordered, like the entity spawn/despawn pair. The set is tiny (a handful),
    // and the diff is against the viewer's _knownSpawnerIds so a steady-state in-AOI spawner costs one Contains check.
    private readonly List<uint> _spawnerForgetScratch = [];

    private void SyncSpawnerMarkers(ClientSession recipient, WorldEntity recipientEntity)
    {
        // Place markers for spawners that newly entered AOI.
        foreach (var spawner in _spawners.Values)
        {
            var inAoi = IsTileInInterest(recipientEntity.Position, spawner.Tile, _tuning.InterestRadius);
            if (inAoi && !recipient.KnowsSpawner(spawner.SpawnerId))
            {
                TrySend(recipient.Peer, new SpawnerMarkerMessage(spawner.SpawnerId, spawner.Tile, true), DeliveryMethod.ReliableOrdered);
                recipient.RememberKnownSpawner(spawner.SpawnerId);
            }
        }

        // Drop markers for known spawners that left AOI or were removed.
        _spawnerForgetScratch.Clear();
        foreach (var spawnerId in recipient.KnownSpawnerIds)
        {
            var stillVisible = _spawners.TryGetValue(spawnerId, out var spawner)
                && IsTileInInterest(recipientEntity.Position, spawner.Tile, _tuning.InterestRadius);
            if (!stillVisible)
            {
                _spawnerForgetScratch.Add(spawnerId);
            }
        }

        foreach (var spawnerId in _spawnerForgetScratch)
        {
            TrySend(recipient.Peer, new SpawnerMarkerMessage(spawnerId, default, false), DeliveryMethod.ReliableOrdered);
            recipient.ForgetKnownSpawner(spawnerId);
        }
    }

    // Whether `target` (a spawner's authored integer tile) is within `interestRadius` (Euclidean, no hysteresis) of
    // the viewer's CONTINUOUS float position. Plain radius test for the spawner marker — it does not need the entity
    // hysteresis (the marker is cheap + reliable, and a sub-tile flicker at the boundary is invisible since the
    // marker is static). CONTINUOUS MIGRATION (Phase 6): the viewer side is now the precise float Position; the
    // spawner stays a tile centre (FromTile) because spawners are authored on integer tiles. So the marker
    // flips on/off at the viewer's true sub-tile distance to the spawner, matching the entity AOI test.
    private static bool IsTileInInterest(WorldVector viewerPosition, TileCoord target, float interestRadius)
    {
        var delta = WorldVector.FromTile(target) - viewerPosition;
        return delta.LengthSquared <= interestRadius * (double)interestRadius;
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

    // CONTINUOUS MIGRATION (Phase 6): the PRECISE interest distance is now computed on the entities' continuous
    // float Position (WorldVector, tile units), not the rounded .TileCoord. So an entity's in/out-of-AOI is
    // decided by its TRUE sub-tile distance to the viewer, the thing integer-tile distance could not express.
    // Returned as a float (the radius/hysteresis comparands are float tiles); the underlying double distance is
    // well within float range for any sane interest radius, and the value only feeds the radius² compare + the
    // snapshot sort key. The grid candidate gather stays a coarse rounded-tile superset (see
    // ResolveAoiQueryRadiusTiles) — this float test is the exact filter applied to the gathered candidates.
    private static float DistanceSquared(WorldEntity a, WorldEntity b)
    {
        return (float)(b.Position - a.Position).LengthSquared;
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

    // Tile half-extent an AOI grid query must cover so the rounded-tile cell gather stays a strict SUPERSET of
    // the precise FLOAT interest test (Phase 6). The gather centers on the viewer's ROUNDED TileCoord and the
    // grid keys candidates on their ROUNDED TileCoord, but IsEntityInInterest now measures the EXIT radius
    // (interest + hysteresis) on the continuous float Positions. Bounding the rounding both sides, per axis:
    //   |Tc - Tv| <= |Tc - Pc| + |Pc - Pv| + |Pv - Tv| <= 0.5 + (exit radius) + 0.5 = exitRadius + 1
    // (each round() is at most 0.5 off its true position, and the axis gap never exceeds the Euclidean exit
    // distance). So any entity that can pass the float test lies within Chebyshev (exitRadius + 1) tiles of the
    // viewer's tile. Ceil that: the +1 is the load-bearing margin that the old integer-only gather lacked — a
    // sub-tile-further float candidate could otherwise round into a cell just outside the box and be dropped.
    private static int ResolveAoiQueryRadiusTiles(float interestRadius)
    {
        return (int)Math.Ceiling(interestRadius + InterestExitHysteresisTiles + RoundedGatherMarginTiles);
    }

    // The +1-tile superset margin above: 0.5 from rounding the viewer's continuous position to its gather-center
    // tile, plus 0.5 from rounding each candidate's continuous position to its grid-cell key. Named so the parity
    // test can stay in lockstep with the gather-radius math.
    internal const float RoundedGatherMarginTiles = 1f;

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
        // MIGRATION (Phase 3 Pass A): carry the entity's full continuous Position on the snapshot DTO. The codec
        // still quantizes it to a tile on the wire (v35 unchanged); Pass B sends it continuously. The double sim
        // position is never rounded here — only the wire projection is.
        return new EntityStateSnapshot(entity.NetworkId, entity.Position, entity.Facing, entity.IsDepleted, health, maxHealth);
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
    // (CombatTargeting.IsAttackableEnemy) so we never "regen" a player or a resource. TryRegenHealth clamps at max,
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
            if (CombatTargeting.IsRegeneratingEnemy(entity))
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

        // LOOT P4c: interacting with a CORPSE OPENS the loot window (eligibility-gated), NOT a resource harvest and
        // no longer an immediate loot-all (P4b). Route it before the resource check so a corpse isn't rejected as
        // "not_resource". The window's buttons then drive take-item / loot-all / close via LootActionMessage.
        if (target.Kind == EntityKind.Corpse)
        {
            HandleCorpseOpen(session, actor, target);
            return;
        }

        if (target.Resource is null)
        {
            SendInteractResult(session, false, "not_resource");
            return;
        }

        if (!IsInInteractionRange(actor, target))
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

    // LOOT P4c: OPEN the loot window on a CORPSE the actor walked up to (an InteractRequest on a corpse). Gates:
    // adjacency (like a harvest) + the actor must be in the corpse's eligibleLooters (the contribution ledger's
    // contributors — solo = the killer). On success it records the open-loot pairing on the session and ships the
    // corpse's current contents (CorpseContents, Open=true) so the client shows the rarity-coloured window. No items
    // move here — taking is a separate LootAction the window's buttons send. A non-eligible / out-of-range / gone
    // corpse is rejected with the same machine reason the harvest path uses.
    private void HandleCorpseOpen(ClientSession session, WorldEntity actor, WorldEntity corpseEntity)
    {
        if (!_corpses.TryGetValue(corpseEntity.Id, out var corpse))
        {
            // Corpse kind but no live loot payload (already looted/decayed this tick, or a stale target): treat as gone.
            SendInteractResult(session, false, "no_target");
            return;
        }

        if (!IsInInteractionRange(actor, corpseEntity))
        {
            SendInteractResult(session, false, "too_far");
            return;
        }

        if (actor.CharacterId is not { } looterId || !corpse.IsEligible(looterId))
        {
            // Not a contributor to the kill (or a non-durable actor with no character id): rejected, window not opened.
            SendInteractResult(session, false, "not_eligible");
            return;
        }

        // Remember which corpse this session has open so a LootAction routes here and a despawn can close the window.
        session.SetOpenCorpse(corpseEntity.Id);
        SendInteractResult(session, true, "");
        SendCorpseContents(session, corpseEntity.NetworkId, corpse);
    }

    // LOOT P4c: a loot-window verb (take one stack / take all / close) on the corpse the session has OPEN. The action
    // is routed by the session's OpenCorpseEntityId (not blind trust of the client's network id — a stale window can't
    // loot a different corpse): the message's CorpseNetworkId must match the open corpse's network id. Close just drops
    // the pairing. Take/LootAll re-validate the corpse still exists + adjacency + eligibility (the player may have
    // walked away or the corpse decayed), transfer via the Corpse primitives, push the inventory delta + toast + the
    // refreshed CorpseContents, and despawn the corpse INSTANTLY if the action emptied it (no lingering empty body).
    private void HandleLootAction(ClientSession session, uint corpseNetworkId, LootActionKind kind, string templateKey)
    {
        // Close: drop the pairing only if it matches (a close for a different/stale corpse is harmless). No reply
        // needed — the client closed its own window; this just releases the server-side pairing.
        if (kind == LootActionKind.Close)
        {
            session.SetOpenCorpse(null);
            return;
        }

        if (session.OpenCorpseEntityId is not { } openCorpseId)
        {
            // No window open server-side (already closed / never opened): ignore — the client's window is stale.
            return;
        }

        if (!_corpses.TryGetValue(openCorpseId, out var corpse)
            || !_zone.World.TryGet(openCorpseId, out var corpseEntity)
            || corpseEntity.NetworkId != corpseNetworkId)
        {
            // The open corpse is gone (decayed/despawned) or the message targets a different corpse than the one this
            // session has open. Close the (now invalid) window and forget the pairing.
            session.SetOpenCorpse(null);
            SendCorpseClosed(session, corpseNetworkId);
            return;
        }

        if (!TryGetSessionEntity(session, out var actor))
        {
            return;
        }

        if (!IsInInteractionRange(actor, corpseEntity))
        {
            // Walked out of range with the window open: close it (the client drops the panel on this Open=false).
            session.SetOpenCorpse(null);
            SendCorpseClosed(session, corpseEntity.NetworkId);
            return;
        }

        if (actor.CharacterId is not { } looterId || !corpse.IsEligible(looterId) || actor.Inventory is null)
        {
            // Eligibility can't change for a live corpse, but re-gate defensively (and a missing inventory is a no-op).
            return;
        }

        if (kind == LootActionKind.TakeItem)
        {
            var take = corpse.TryTakeItem(templateKey, actor.Inventory);
            if (take.Took)
            {
                SendInventoryUpdate(session, [new ItemStack(take.Transferred.TemplateKey, actor.Inventory.QuantityOf(take.Transferred.TemplateKey))]);
                SendSystem(session, $"Looted: {FormatLootSummary([take.Transferred])}.");
            }

            FinishLootAction(session, corpseEntity, corpse, take.CorpseEmptied);
            return;
        }

        // LootAll.
        var result = corpse.TryLootAll(actor.Inventory);
        if (result.Looted)
        {
            var changed = new List<ItemStack>(result.Transferred.Count);
            foreach (var moved in result.Transferred)
            {
                changed.Add(new ItemStack(moved.TemplateKey, actor.Inventory.QuantityOf(moved.TemplateKey)));
            }

            SendInventoryUpdate(session, changed);
            SendSystem(session, $"Looted: {FormatLootSummary(result.Transferred)}.");
        }

        FinishLootAction(session, corpseEntity, corpse, result.CorpseEmptied);
    }

    // LOOT P4c: after a take/loot-all, either despawn the now-empty corpse INSTANTLY (which closes the window via
    // SendCorpseClosed + forgets the pairing) or refresh the still-open window with the remaining contents. Shared by
    // the take-item and loot-all paths so "empty => instant despawn, no lingering body" is one place.
    private void FinishLootAction(ClientSession session, WorldEntity corpseEntity, Corpse corpse, bool corpseEmptied)
    {
        if (corpseEmptied)
        {
            // Instant despawn the moment the last item leaves (parity with the old grab-all path): no empty corpse ever
            // sits on the ground waiting for decay. DespawnCorpse closes the window for every session that had it open.
            DespawnCorpse(corpseEntity.Id);
            return;
        }

        // Still has loot: refresh the open window with what remains.
        SendCorpseContents(session, corpseEntity.NetworkId, corpse);
    }

    // LOOT P4c: ship an OPEN corpse's current contents to the owner so the loot window shows/refreshes (rarity-coloured).
    // Resolves each stack's rarity from the item registry (Common for an unknown key — defensive). DisplayName is NOT
    // sent; the client resolves it from its own registry (falling back to the key), keeping the wire thin.
    private void SendCorpseContents(ClientSession session, uint corpseNetworkId, Corpse corpse)
    {
        var contents = corpse.Contents;
        var items = new List<CorpseItem>(contents.Count);
        foreach (var stack in contents)
        {
            var rarity = _itemRegistry.TryGet(stack.TemplateKey, out var definition) ? definition.Rarity : Rarity.Common;
            items.Add(new CorpseItem(stack.TemplateKey, stack.Quantity, rarity));
        }

        TrySend(session.Peer, new CorpseContentsMessage(corpseNetworkId, true, items), DeliveryMethod.ReliableOrdered);
    }

    // LOOT P4c: tell the client to CLOSE the loot window for a corpse (Open=false, empty items) — the corpse emptied,
    // decayed, despawned, or the player walked out of range. Reliable-ordered so a close is never lost (a stuck-open
    // window would let the client send LootActions the server now rejects, but never loot — still, close cleanly).
    private void SendCorpseClosed(ClientSession session, uint corpseNetworkId)
    {
        TrySend(session.Peer, new CorpseContentsMessage(corpseNetworkId, false, []), DeliveryMethod.ReliableOrdered);
    }

    // LOOT P4b: formats a looted-stacks list into a human "2x Slime Gel, 1x Arcane Dust" toast, using the item
    // registry's display names (falling back to the raw key for an unknown template — defensive only).
    private string FormatLootSummary(IReadOnlyList<ItemStack> stacks)
    {
        var summary = new StringBuilder();
        for (var i = 0; i < stacks.Count; i++)
        {
            if (i > 0)
            {
                summary.Append(", ");
            }

            var name = _itemRegistry.TryGet(stacks[i].TemplateKey, out var definition)
                ? definition.DisplayName
                : stacks[i].TemplateKey;
            summary.Append(stacks[i].Quantity).Append("x ").Append(name);
        }

        return summary.ToString();
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
        _zone.World.GatherInterestCandidates(actor.TileCoord, _aoiQueryRadiusTiles, _aoiInteractCandidateScratch);
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
    // CONTINUOUS MIGRATION (Phase 9): the interact REACH gate (harvest a node / open + loot a corpse). Floats the
    // former tile Chebyshev <= 1 adjacency to a Euclidean distance on the CONTINUOUS positions — same int->float
    // pattern as Phase 6 (AOI) and Phase 7 (combat). The actor moves off-grid, so its sub-tile offset now counts;
    // the target (resource node / corpse) is still authored on a tile centre, so target.Position is that centre.
    // The radius (InteractionTuning.InteractionRadiusTiles, 1.5) is SHARED with the client's HarvestTargeting so
    // the player sees harvestable exactly what this gate accepts; compared squared to skip the sqrt.
    private static bool IsInInteractionRange(WorldEntity actor, WorldEntity target)
    {
        return (actor.Position - target.Position).LengthSquared <= InteractionTuning.InteractionRadiusTilesSquared;
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
                ? "commands: /help, /role, /who, /metrics, /speed <multiplier>, /monster [name], /stress, /stress status, /stress start [clients] [duration], /stress stop"
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
                HandleMonsterCommand(sender, parts);
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
        // Keep the player's tiles/sec speed stat tracking the multiplier — LIVE in Phase 1 (the integrator reads it).
        RefreshSpeedStat(entity);
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
    // shows an overhead HP bar and is hittable (CombatTargeting.IsAttackableEnemy now includes Monster). The
    // server then idles it near home and occasionally strolls it within the leash. It spawns on the caller's own
    // tile (always walkable — the caller stands there); replication + client interpolation render it as a moving
    // cube for free. LIVING-ENEMIES P2: it now also AGGROS the nearest player in range, CHASES (leashed to home),
    // and ATTACKS when adjacent (the player TAKES damage — HP floors at 0, no death/respawn yet).
    private void HandleMonsterCommand(ClientSession sender, string[] parts)
    {
        if (!TryGetSessionEntity(sender, out var actor))
        {
            SendSystem(sender, "monster: no controllable entity.");
            return;
        }

        // LIVING-ENEMIES P2-POLISH: /monster <name> spawns that TYPE; /monster with no name defaults to the only type
        // (slime). An unknown name is a clear error listing the available names (so a typo is obvious, not silent).
        MonsterType type;
        if (parts.Length >= 2)
        {
            if (!_monsterTypes.TryGet(parts[1], out type))
            {
                var names = string.Join(", ", _monsterTypes.Types.Select(t => t.Id));
                SendSystem(sender, $"monster: unknown type '{parts[1]}'. Available: {names}.");
                return;
            }
        }
        else
        {
            type = _monsterTypes.Default;
        }

        // LIVING-ENEMIES P3: /monster now creates a PERSISTENT SPAWNER at the caller's tile (the spawner owns + respawns
        // the monster, and is the red-tile anchor that survives a kill). The spawner immediately spawns its first
        // monster. The spawner tile = the monster's leash home; it must be walkable (the caller stands there).
        var spawner = new MonsterSpawner(_nextSpawnerId++, actor.TileCoord, type);
        _spawners[spawner.SpawnerId] = spawner;
        var monster = SpawnMonsterForSpawner(spawner);

        var effectiveMs = EffectiveStepCooldownMs(monster);
        SendSystem(
            sender,
            $"monster: spawner #{spawner.SpawnerId} for {type.DisplayName} at {spawner.Tile.X},{spawner.Tile.Y}, hp={type.MaxHealth}, step={effectiveMs}ms, roamRadius={type.RoamRadius}, aggro={type.AggroRadius}, leash={type.ChaseLeash}, atk={type.AttackDamage}/{type.AttackCooldownMs}ms, respawn={type.RespawnMs}ms.");
        Log.Info($"{sender.DisplayName} created spawner #{spawner.SpawnerId} for {type.DisplayName} at {spawner.Tile} (type={type.Id}).");
    }

    // LIVING-ENEMIES P3: spawns a fresh full-HP monster of the spawner's type at the spawner tile, wires it to the AI +
    // type maps, and attaches it to the spawner. Shared by the initial /monster spawn AND each respawn. The red marker
    // is a separate persistent spawner concept, so spawning a monster does NOT send a per-monster home anymore.
    private WorldEntity SpawnMonsterForSpawner(MonsterSpawner spawner)
    {
        var type = spawner.Type;
        // Rent throws only on the (ushort-space) exhaustion the dummy/resource spawns also rely on never hitting.
        var monster = _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Monster, type.DisplayName, spawner.Tile, Direction8.S);
        // The monster takes its TYPE's stats/AI tuning. MaxHealth (spawn at full) + the move-speed multiplier (which
        // feeds the EffectiveStepCooldown path so it steps on its OWN type-derived cadence — outrunnable). Remember the
        // type for the AI step.
        monster.SetMaxHealthFull(type.MaxHealth);
        monster.TrySetSpeedMultiplier(type.MoveSpeedMultiplier);
        // Seed the monster's tiles/sec speed stat from its type multiplier — dormant for monsters (they HOP via the
        // cadence gate, not the velocity integrator; Velocity stays Zero). Kept for parity with the player /speed path.
        RefreshSpeedStat(monster);
        _monsterTypeOf[monster.Id] = type;

        // Register with the roam AI: the spawner tile is the leash home; start Idle with an initial randomized pause,
        // tick-quantised off THIS type's pause bounds.
        var tunables = _monsterTypes.BuildTunables(type);
        _monsterAi.Register(monster, _serverTick, tunables.PauseMinTicks, tunables.PauseMaxTicks, tunables.AggroScanIntervalTicks);

        // Link the monster to its spawner (both directions) so a death finds the spawner in O(1).
        spawner.AttachMonster(monster.Id);
        _spawnerOfMonster[monster.Id] = spawner;
        return monster;
    }

    // LIVING-ENEMIES P3: a monster DIED (HP hit 0 from a player attack). Despawn the entity (EntityDespawn to AOI
    // viewers + remove from the world/spatial index), clean up its AI + type state (the cleanup the P3 follow-up asked
    // for — no leak), and notify its spawner to schedule the respawn. The persistent spawner + its red marker stay.
    private void KillMonster(WorldEntity monster)
    {
        var monsterId = monster.Id;
        var deathTile = monster.TileCoord;

        // Remove from the world (also unhooks the spatial index). Do NOT free the network id here: RollAndSpawnCorpse
        // below rents an id and the pool REUSES freed ids, so freeing now lets the corpse rent the just-despawned
        // monster's SAME id. An in-AOI client still "knows" that id, so EnsureEntitySpawns skips the corpse's spawn
        // (KnowsEntity == true) and the corpse NEVER RENDERS (it exists server-side, so its loot window still opens —
        // the "no corpse on an in-AOI death" bug). Free it at the END, after the corpse has rented a fresh id.
        var despawned = _zone.Despawn(monsterId, out var removed);

        // LOOT P4b: roll this monster's loot and spawn a CORPSE at the death tile holding it, tagged with the
        // eligible-looter set (the contribution ledger's contributors) + the loot mode + a decay deadline. Done
        // BEFORE the type/ledger cleanup below (it reads the type's LootTableId + the ledger). Nothing is delivered
        // to any inventory here — a player loots the corpse by interacting with it.
        if (_monsterTypeOf.TryGetValue(monsterId, out var deadType))
        {
            RollAndSpawnCorpse(deadType, monsterId, deathTile);
        }

        // LOOT P4b: forget this monster's contribution ledger entry (snapshotted onto the corpse above) so the
        // ledger is cleaned up with the monster and never leaks — alongside the AI/type cleanup below.
        _contributionLedger.Forget(monsterId);

        // Clean up AI + type membership (fixes the former add-only leak — see todo/monster-types-followups.md P2/P3).
        _monsterAi.Forget(monsterId);
        _monsterTypeOf.Remove(monsterId);

        // Notify the owning spawner so it schedules a respawn after the type's delay (read live).
        if (_spawnerOfMonster.Remove(monsterId, out var spawner))
        {
            var respawnTicks = _monsterTypes.RespawnTicks(spawner.Type);
            spawner.NotifyMonsterDied(monsterId, _serverTick, respawnTicks);
            Log.Info($"Monster {removed?.NetworkId} (spawner #{spawner.SpawnerId}) died; respawn in {respawnTicks} ticks.");
        }

        // Now free the monster's network id — the corpse (if one spawned) has already rented a DIFFERENT id, so the
        // pool's reuse can't hand the corpse the still-known monster id and get its spawn skipped on in-AOI clients.
        if (despawned)
        {
            _networkIds.Return(removed.NetworkId);
        }
    }

    // LOOT P4b: roll the dead monster's loot table and, if it yielded anything, spawn a replicated Corpse entity at
    // the death tile holding the rolled stacks SERVER-SIDE. The corpse is tagged with the eligible-looter set (the
    // contribution ledger's contributors — solo = the killer), the loot mode (FfaAmongEligible default), and a decay
    // deadline (now + the live corpse-decay duration). An empty LootTableId or a roll that dropped nothing spawns NO
    // corpse (a no-loot kill is silent), so corpses only appear where there is something to take.
    private void RollAndSpawnCorpse(MonsterType type, ulong monsterId, TileCoord deathTile)
    {
        if (string.IsNullOrEmpty(type.LootTableId))
        {
            return;
        }

        var stacks = _lootTables.Roll(type.LootTableId, _lootRng);
        if (stacks.Count == 0)
        {
            return;
        }

        // The eligible-looter set = the contributors recorded as they damaged the monster (solo = just the killer).
        var eligible = _contributionLedger.Contributors(monsterId);

        // The death tile is where the monster stood (always walkable — it occupied it), so the corpse spawns there.
        // It is a transient world entity of EntityKind.Corpse, so it AOI-replicates + renders + is interactable
        // through the existing paths with no new replication fork. DisplayName drives the client's visual choice
        // (falls back to the Box archetype for a non-Player/Resource kind today).
        var corpseEntity = _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Corpse, "Corpse", deathTile, Direction8.S);
        var decayAtTick = _serverTick + _tuning.CorpseDecayTicks;
        var corpse = new Corpse(corpseEntity.Id, stacks, eligible, LootMode.FfaAmongEligible, decayAtTick);
        _corpses[corpseEntity.Id] = corpse;

        Log.Info($"[LOOT P4b] {type.Id} died at {deathTile}; spawned corpse #{corpseEntity.NetworkId} holding " +
                 $"{corpse.Contents.Count} stack(s), eligible={eligible.Count}, decay@{decayAtTick}.");
    }

    // LOOT P4b: per-tick decay pass. Despawns any corpse whose decay deadline has arrived even if it still holds
    // unlooted loot (UO-style). Removing the entity from the world makes the AOI pass send EntityDespawn to viewers
    // (the SAME exit path a looted/forgotten entity uses) and frees the network id. Polls the tiny corpse set; no-op
    // when empty. The decay duration is a live-tunable global (loot.corpseDecayMs).
    private void DecayCorpses()
    {
        if (_corpses.Count == 0)
        {
            return;
        }

        _corpseDecayScratch.Clear();
        foreach (var corpse in _corpses.Values)
        {
            if (corpse.IsDecayed(_serverTick))
            {
                _corpseDecayScratch.Add(corpse.EntityId);
            }
        }

        foreach (var corpseId in _corpseDecayScratch)
        {
            DespawnCorpse(corpseId);
            Log.Info($"[LOOT P4b] corpse (entity {corpseId}) decayed unlooted; despawned.");
        }
    }

    // LOOT P4b/P4c: removes a corpse's world entity + its server-side loot payload, freeing the network id. The AOI
    // pass turns the world removal into an EntityDespawn for viewers. Shared by the loot-empties path (instant) and the
    // decay pass. LOOT P4c: also CLOSES the loot window for any session that had this corpse open (forgets the pairing
    // + sends Open=false) so no client is left with a window for a gone corpse. Idempotent-ish: a missing id is a no-op.
    private void DespawnCorpse(ulong corpseEntityId)
    {
        if (!_corpses.Remove(corpseEntityId))
        {
            return;
        }

        // Capture the network id BEFORE despawning so we can address the close to the viewers' windows.
        var corpseNetworkId = _zone.World.TryGet(corpseEntityId, out var corpseEntity) ? corpseEntity.NetworkId : 0u;
        foreach (var session in _sessions.Values)
        {
            if (session.OpenCorpseEntityId == corpseEntityId)
            {
                session.SetOpenCorpse(null);
                SendCorpseClosed(session, corpseNetworkId);
            }
        }

        if (_zone.Despawn(corpseEntityId, out var removed))
        {
            _networkIds.Return(removed.NetworkId);
        }
    }

    // LIVING-ENEMIES P3: per-tick spawner respawn pass. For each spawner whose respawn delay has elapsed, spawn a fresh
    // full-HP monster of its type at its tile. Iterates the (tiny) spawner set directly; SpawnMonsterForSpawner clears
    // the schedule via AttachMonster. The marker is already present (it persisted across the death), so a respawn just
    // re-adds a roaming monster under the existing red tile.
    private void RespawnMonsters()
    {
        if (_spawners.Count == 0)
        {
            return;
        }

        foreach (var spawner in _spawners.Values)
        {
            if (spawner.IsRespawnDue(_serverTick))
            {
                SpawnMonsterForSpawner(spawner);
            }
        }
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

        // LIVING-ENEMIES P2-POLISH: per-monster-TYPE keys ("<typeId>.<field>", e.g. slime.roamRadius) are owned by the
        // monster-type registry (the per-type tuning replaced the former global monster.* block). Try it first; on a
        // hit, broadcast the replicated per-type snapshot so the F1 Monster tab re-seeds to the post-clamp values.
        if (_monsterTypes.TryApply(key, value, out var monsterApplied))
        {
            BroadcastMonsterTuning();
            // LIVESPEED-DESYNC: the AI paces a monster off its TYPE's LIVE MoveSpeedMultiplier each tick, but the
            // monster ENTITY's SpeedMultiplier (the source of EntitySpawn / MovementSpeedChanged cadence the client
            // interpolates at) is copied only once at spawn. Editing "<typeId>.moveSpeed" therefore re-paced the
            // server AI while the client kept interpolating at the stale spawn cadence → a growing desync. Re-push the
            // edited type's MoveSpeedMultiplier onto every already-spawned monster of that type and re-broadcast the
            // cadence (reusing the player /speed path), so AI ticks, entity SpeedMultiplier, and client interpolation
            // stay in lockstep on a live edit. Re-applying an unchanged multiplier is a no-op and broadcasts nothing,
            // so this safely runs after ANY of the type's edits, not just moveSpeed.
            PropagateMonsterTypeSpeedToSpawned(key);
            SendSystem(sender, $"tuning: {key} = {ServerTuningRegistry.Format(monsterApplied)} (applied live).");
            Log.Info($"{sender.DisplayName} set tuning {key}={ServerTuningRegistry.Format(monsterApplied)} (requested {value}).");
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

    // LIVESPEED-DESYNC: after a per-type tuning edit ("<typeId>.<field>"), re-sync the edited type's already-spawned
    // monsters so their ENTITY SpeedMultiplier (the EntitySpawn / MovementSpeedChanged cadence source the client
    // interpolates at) matches the type's LIVE MoveSpeedMultiplier the AI now paces off (StepMonsterAi reads it fresh
    // each tick via EffectiveStepCooldownTicksFor). Without this the AI re-paced while clients kept interpolating at
    // the stale spawn cadence → the "VERY desynced" slime. We resolve the edited type from the key, then for each
    // spawned monster OF THAT TYPE re-apply the multiplier and, only when it actually changed, re-broadcast the new
    // effective cadence via the SAME player /speed path (so the client re-quantises its interpolation identically).
    // TrySetSpeedMultiplier is a no-op + returns false when unchanged, so a non-moveSpeed edit costs only the walk.
    private void PropagateMonsterTypeSpeedToSpawned(string key)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0 || dot >= key.Length - 1)
        {
            return;
        }

        if (!_monsterTypes.TryGet(key[..dot], out var editedType))
        {
            return;
        }

        foreach (var entity in _zone.World.Entities)
        {
            if (entity.Kind != EntityKind.Monster)
            {
                continue;
            }

            // Compare by reference: _monsterTypeOf stores the SAME MonsterType instance the registry mutates in place,
            // so == is exact identity with the just-edited type (no id re-parse needed).
            if (!_monsterTypeOf.TryGetValue(entity.Id, out var type) || !ReferenceEquals(type, editedType))
            {
                continue;
            }

            if (entity.TrySetSpeedMultiplier(editedType.MoveSpeedMultiplier))
            {
                // Keep the tiles/sec speed stat tracking the retuned multiplier — LIVE for players in Phase 1.
                RefreshSpeedStat(entity);
                BroadcastMovementSpeedChanged(entity, EffectiveStepCooldownMs(entity));
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

                // LOOT P4b: record this attacker as a contributor to each MONSTER it damaged (the eligibility
                // groundwork). Keyed by the monster's entity id + the attacker's durable CharacterId, so on death the
                // contributor set becomes the corpse's eligibleLooters. Only monsters yield loot, so only they are
                // ledgered; a Dummy/Npc hit is ignored here.
                if (damaged.Victim.Kind == EntityKind.Monster && attacker.CharacterId is { } contributorId)
                {
                    _contributionLedger.RecordDamage(damaged.Victim.Id, contributorId, damaged.Amount);
                }
            }

            Log.Info($"{session.DisplayName} free-aim hit {hits} target(s) for {damage} each (aim {aimRadians:F2} rad).");

            // LIVING-ENEMIES P3: a MONSTER victim whose HP hit 0 DIES. Check after the damage numbers (so the final hit
            // still floats) and after the loop (the scratch buffer is reused inside KillMonster's despawn path). Only
            // monsters die from a player attack here; dummies floor at 0 + regen, players can't hit players.
            foreach (var damaged in _damagedVictimScratch)
            {
                if (damaged.Victim.Kind == EntityKind.Monster && damaged.Victim.Stats.Health <= 0)
                {
                    KillMonster(damaged.Victim);
                }
            }
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

    // LIVING-ENEMIES P2-POLISH: replicate the per-monster-TYPE tuning to one client (login initial truth). Reliable-
    // ordered, like SendCombatTuning. The client mirrors it so the F1 Monster tab shows + edits the live values.
    private void SendMonsterTuning(ClientSession session)
    {
        TrySend(session.Peer, new MonsterTuningMessage(_monsterTypes.BuildSnapshot()), DeliveryMethod.ReliableOrdered);
    }

    // LIVING-ENEMIES P2-POLISH: push the current per-type tuning to every authenticated client. Called when a
    // "<typeId>.<field>" key changes so any open F1 Monster tab re-seeds to the post-clamp values. Global (not
    // AOI-scoped), like BroadcastCombatTuning — every authenticated session gets it.
    private void BroadcastMonsterTuning()
    {
        var message = new MonsterTuningMessage(_monsterTypes.BuildSnapshot());
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

    // LOOT P4c (monster-types follow-up #1): the effective step cooldown in TICKS for an arbitrary speed
    // MULTIPLIER, sharing the exact same base-cooldown ÷ multiplier + min/max clamp as the per-entity path
    // (WorldEntity.EffectiveStepCooldownTicks). StepMonsterAi feeds the monster's TYPE's LIVE MoveSpeedMultiplier
    // here each tick (not the entity's spawn-time-copied SpeedMultiplier), so editing "<typeId>.moveSpeed" on the
    // F1 Monster tab re-paces ALREADY-SPAWNED monsters on the next tick — consistent with the other live Tunables.
    // A non-positive multiplier (impossible after the registry's [0.1, 5] clamp) falls back to 1.0 defensively.
    private uint EffectiveStepCooldownTicksFor(double speedMultiplier)
    {
        var multiplier = speedMultiplier > 0 ? speedMultiplier : 1.0;
        var scaled = _tuning.StepCooldownTicks / multiplier;
        var ticks = (long)Math.Max(1, Math.Round(scaled, MidpointRounding.AwayFromZero));
        return (uint)Math.Clamp(ticks, (long)MinEffectiveStepCooldownTicks, (long)MaxEffectiveStepCooldownTicks);
    }

    // The entity's effective step cooldown in MS for the wire (EntitySpawn / MovementSpeedChanged). Derived
    // from the effective TICKS so it round-trips to the same tick count when the client re-quantises it via
    // MovementCadence.EffectiveStepCadenceMs — keeping server and client cadence in lockstep. Clamped to a
    // ushort (the wire field); the ms clamp keeps it well within range.
    private ushort EffectiveStepCooldownMs(WorldEntity entity)
    {
        var ms = EffectiveStepCooldownTicks(entity) * (1000d / _options.TickRate);
        return (ushort)Math.Clamp((int)Math.Round(ms, MidpointRounding.AwayFromZero), 1, ushort.MaxValue);
    }

    // Refresh an entity's tiles/sec speed stat from the base move speed × its current SpeedMultiplier. Called on
    // spawn + on every SpeedMultiplier change so the stat tracks the live multiplier. PLAYERS consume
    // SpeedUnitsPerSecond as the per-input continuous integrator's speed (base 1000/StepCooldownMs ⇒ one tile per
    // StepCooldownMs at multiplier 1.0, so a held cardinal crosses tiles at ≈ the old tile-step cadence). Monsters
    // keep the EffectiveStepCooldownTicks tile-step path, so for them this stat is dormant.
    private void RefreshSpeedStat(WorldEntity entity)
    {
        entity.SetSpeedUnitsPerSecond(_tuning.BaseMoveSpeedUnitsPerSecond * entity.SpeedMultiplier);
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): integrate ONE per-input continuous MoveIntent on the RECEIVE path (the
    // experiment model). Only a FRESH input (InputSeq > session.LastInputSeq) is processed; a stale/duplicate input is
    // ignored. The fresh input ALWAYS advances LastInputSeq (so the snapshot ACKs it — the client trims its buffer),
    // then integrates by its OWN dt with the guards that formerly lived on the fixed-tick pass moved here:
    //   * dead player (downed) → no motion (the dead-player guard);
    //   * swing-root freeze (IsMovementFrozen) → the input ACKs but produces ZERO motion (the rooted player);
    //   * a (0,0) direction → STOP (zero velocity, IsMoving=false);
    //   * otherwise integrate Position by the dt-clamped velocity in the RAW direction (normalized by the integrator),
    //     through the shared swept-circle wall collision (Zone.IntegrateMovement).
    // ANTI-SPEEDHACK: the client-supplied dt is first SANITY-clamped to [0, MaxMoveInputDtSeconds], then debited
    // against the per-peer wall-clock dt BUDGET (ConsumeMoveDtBudget) — so the integration uses at most the dt the
    // budget allows. Over any window the peer's integrated sim-time cannot exceed real elapsed + the burst allowance.
    // MONSTERS are untouched (they never send MoveIntent; they tile-step via StepMonsterAi).
    private void HandleMoveIntent(ClientSession session, MoveIntentMessage intent)
    {
        if (!session.TryBeginMoveInput(intent.InputSeq, _serverTick))
        {
            return; // stale/duplicate input — ignore (no integrate, no re-ack).
        }

        if (!TryGetSessionEntity(session, out var entity))
        {
            return;
        }

        // The RAW client direction. A zero vector is the explicit STOP. NaN/Inf (tamper) collapses to a stop. The
        // continuous integrator (ComputeMoveDelta) scales the passed vector by SpeedUnitsPerSecond WITHOUT
        // normalizing, so NORMALIZE here — otherwise a (1,1) diagonal would travel √2 faster than a cardinal (the
        // whole point of Direction8.ToUnitVector in the old path). Magnitude therefore never throttles or boosts.
        var rawDir = new WorldVector(SanitizeAxis(intent.DirX), SanitizeAxis(intent.DirY));
        var moving = rawDir.LengthSquared > 0d;
        var unitDir = moving ? rawDir.Normalized() : WorldVector.Zero;

        // Guards: a DEAD (downed) player or a swing-root-frozen player ACKs the input but does NOT move. A (0,0)
        // input is a stop. In every non-moving case zero the velocity so the entity never glides.
        if (!moving || session.IsDead || entity.IsMovementFrozen(_serverTick))
        {
            session.SetMoving(false);
            entity.StopMovement();
            return;
        }

        // ANTI-SPEEDHACK: sanity-clamp the per-input dt, then debit the per-peer wall-clock budget. The integration
        // dt is whatever the budget allows (0 when a flood has drained it) — so a peer cannot out-integrate real time.
        var sanitizedDt = Math.Clamp(SanitizeAxis(intent.DtSeconds), 0d, MaxMoveInputDtSeconds);
        var dtSeconds = session.ConsumeMoveDtBudget(sanitizedDt);

        session.SetMoving(true);
        if (dtSeconds <= 0d)
        {
            // Budget exhausted (flood) — the input is acked + faces the direction (ComputeMoveDelta sets Velocity +
            // Facing), but a zero dt advances no distance. The entity holds position; no tile crossing.
            entity.ComputeMoveDelta(unitDir, 0d);
            return;
        }

        // Integrate through the shared swept-circle wall collision (the SAME walls + radius the Phase-4 predictor
        // will replay against). bodyRadius is read fresh from the live tuning so a continuous.bodyRadius retune
        // takes effect on the next input.
        if (_zone.IntegrateMovement(entity, unitDir, dtSeconds, _tuning.BodyRadiusUnits))
        {
            // A rounded-tile crossing — persist the new durable tile (mirrors the old tile-step path).
            MarkDirtyDurableTile(entity);
        }
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): the per-tick housekeeping that replaced the fixed-tick player integrator.
    // Player integration is now 100% input-driven (HandleMoveIntent); this pass only (a) CREDITS each authenticated
    // session's anti-speedhack dt budget by the elapsed tick time (so the budget tracks real elapsed time) and (b)
    // does the keepalive/stop housekeeping for AOI/animation: a "moving" session that has gone silent past the
    // keepalive timeout (a wedged-but-connected client) is force-STOPPED (velocity zeroed, IsMoving cleared) so its
    // avatar doesn't appear stuck mid-stride and AOI/animation see it at rest. A dead/rooted player that is still
    // flagged moving is likewise zeroed. dtBudget credit is the FIXED tick interval (the server ticks at a fixed
    // cadence, so this equals real elapsed time over many ticks — and is deterministic for the flood test).
    private void CreditMoveDtBudgetsAndKeepalive()
    {
        var tickSeconds = 1.0 / _options.TickRate;
        var keepaliveTimeoutTicks = (uint)Math.Max(1, (int)Math.Ceiling(MoveIntentKeepaliveTimeout.TotalMilliseconds / (1000d / _options.TickRate)));

        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated)
            {
                continue;
            }

            // Credit the anti-speedhack budget by the elapsed tick time (capped at the burst allowance inside).
            session.CreditMoveDtBudget(tickSeconds, MoveDtBurstAllowanceSeconds);

            if (!TryGetSessionEntity(session, out var entity))
            {
                continue;
            }

            // Keepalive/stop housekeeping. If the session is still flagged moving but has gone silent past the
            // timeout, or is dead/rooted, force it to rest so it neither animates as walking nor glides.
            var stale = _serverTick - session.LastMoveIntentTick >= keepaliveTimeoutTicks;
            if (session.IsMoving && (stale || session.IsDead || entity.IsMovementFrozen(_serverTick)))
            {
                session.ClearMoveIntent();
                entity.StopMovement();
            }
        }
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): collapse a NaN/Inf wire axis (tamper / corruption) to 0 before it reaches
    // the integrator, so a hostile DirX/DirY/DtSeconds can never produce a NaN position or an unbounded step.
    private static double SanitizeAxis(float value)
    {
        return float.IsFinite(value) ? value : 0d;
    }

    // LIVING-ENEMIES P1 + CONTINUOUS MIGRATION (Phase 8): the per-tick monster AI pass (sibling to
    // StepHeldMovementIntents). For each Monster entity, the MonsterRoamAi advances its Idle↔Roaming↔Chasing↔Returning
    // state machine and, when moving, HOPS once per move cadence toward its continuous nav target via the injected
    // HopLocomotion — a discrete collision-valid leap (Velocity stays Zero; the sparse-update jump is preserved). The
    // hop's own cadence gate (WorldEntity.TryBeginHop / _nextEligibleTick) paces the monster to one leap per cooldown
    // (fed each tick, leaps only on cadence), and the AI's pause timers keep it Idle (still) most of the time. The
    // per-type Tunables (Euclidean ranges) + the cadence are read fresh each tick so a "<typeId>.*" admin retune takes
    // effect immediately.
    //
    // Iterates the live entity collection directly (no per-tick allocation); the count is tiny. A hop mutates a
    // monster's Position and (on a tile cross) migrates its spatial-grid bucket but never adds/removes a dictionary
    // entry, so iterating Entities while stepping is safe — same as RegenEnemies.
    private void StepMonsterAi()
    {
        if (_monsterAi.TrackedCount == 0)
        {
            return;
        }

        // LIVING-ENEMIES P2-POLISH: each monster's Tunables come from ITS TYPE (read fresh from the live per-type
        // values so a "<typeId>.*" admin retune takes effect on the next tick), not a single global block. The
        // tick-quantised pause/cooldown/scan + the derived de-aggro hysteresis are computed by the type registry.
        // De-aggro range and the aggro-scan cadence stay DERIVED (coupled to their source values).
        foreach (var entity in _zone.World.Entities)
        {
            if (entity.Kind != EntityKind.Monster)
            {
                continue;
            }

            // Resolve the monster's type (falls back to the default if somehow untracked — e.g. a legacy spawn).
            if (!_monsterTypeOf.TryGetValue(entity.Id, out var type))
            {
                type = _monsterTypes.Default;
            }

            // LOOT P4c (monster-types follow-up #1): pace the monster off its TYPE's LIVE MoveSpeedMultiplier read
            // fresh each tick — NOT the entity's spawn-time-copied SpeedMultiplier — so an admin retuning
            // "<typeId>.moveSpeed" on the F1 tab speeds up / slows down ALREADY-SPAWNED monsters next tick, like the
            // other live Tunables. Same base ÷ multiplier + min/max clamp as the player path.
            _monsterAi.StepMonster(
                entity,
                _serverTick,
                EffectiveStepCooldownTicksFor(type.MoveSpeedMultiplier),
                _monsterTypes.BuildTunables(type));
        }
    }

    // LIVING-ENEMIES P2: the AI's aggro-scan callback — find the NEAREST ALIVE PLAYER within `aggroRadius` (Chebyshev)
    // of `monster`, via the SAME spatial index the combat resolver uses (GatherInterestCandidates), so occupancy can
    // never diverge from AOI/replication. Players only (never another monster/dummy/resource), and only alive ones
    // (Health > 0 — a downed player at 0 HP is not a fresh aggro target; an already-chasing monster keeps attacking it
    // via the resolve path, but a NEW acquisition skips it). Returns the closest by Chebyshev distance, ties broken by
    // the smaller entity id for determinism. THROTTLED by the AI (it only calls this every ~0.5 s per monster), so the
    // per-tick scan the P1 review flagged is avoided.
    private bool FindMonsterAggroTarget(WorldEntity monster, int gatherRadius, out ulong targetId, out WorldVector targetPosition)
    {
        // CONTINUOUS MIGRATION (Phase 8): `gatherRadius` is the AI's COARSE tile/Chebyshev pre-filter (⌈Euclidean
        // aggro⌉ + 1) — a strict superset of the Euclidean aggro disc, so this gather drops no in-range target. The
        // precise Euclidean range test is done by the AI on the returned Position; here we just return the NEAREST
        // alive player by Euclidean distance on Position (ties → smaller id for determinism), and the AI accepts it
        // only if it is actually within the Euclidean aggro radius.
        _zone.World.GatherInterestCandidates(monster.TileCoord, gatherRadius, _monsterAggroScratch);

        var bestDistSq = double.MaxValue;
        WorldEntity? best = null;
        foreach (var candidate in _monsterAggroScratch)
        {
            if (candidate.Kind != EntityKind.Player || candidate.Stats.Health <= 0)
            {
                continue;
            }

            var distSq = (candidate.Position - monster.Position).LengthSquared;
            if (distSq < bestDistSq || (distSq == bestDistSq && (best is null || candidate.Id < best.Id)))
            {
                bestDistSq = distSq;
                best = candidate;
            }
        }

        if (best is null)
        {
            targetId = 0;
            targetPosition = default;
            return false;
        }

        targetId = best.Id;
        targetPosition = best.Position;
        return true;
    }

    // LIVING-ENEMIES P2: the AI's target-resolve callback — look up a chased target's CURRENT tile + alive flag so the
    // chase re-reads the player's live position each step and detects target-lost (despawn / logout) for de-aggro.
    // Returns false if the entity no longer exists at all; `alive` is its Health > 0 (a player whose HP hit 0 is still
    // resolvable but not alive → the AI de-aggros and returns home, since there is no death/respawn this phase).
    private bool TryResolveMonsterTarget(ulong targetId, out WorldVector targetPosition, out bool alive)
    {
        if (_zone.World.TryGet(targetId, out var target) && target.Kind == EntityKind.Player)
        {
            // CONTINUOUS MIGRATION (Phase 8): return the target's CONTINUOUS Position so the chase re-reads the
            // player's live sub-tile position each hop and the Euclidean de-aggro/leash test is exact.
            targetPosition = target.Position;
            alive = target.Stats.Health > 0;
            return true;
        }

        targetPosition = default;
        alive = false;
        return false;
    }

    // LIVING-ENEMIES P2: the AI's attack callback — the monster faces its target and deals `attackDamage` to the
    // PLAYER through the SAME ApplyDamage + DamageEventMessage path a player's attack uses (no combat fork). The AI
    // owns the WHEN (adjacency + the monster's own attack cooldown); this owns the HOW. The damage number is broadcast
    // to the victim AND nearby viewers — the victim is the PLAYER, NOT the attacker, so it is NOT excluded (it has no
    // client-side prediction of incoming damage, unlike its own outgoing swings). LIVING-ENEMIES P3: HP reaching 0 now
    // KILLS the player (marks the session dead + schedules the respawn); a dead/downed player is guarded out (no further
    // hits, no double-death) until RespawnPlayers teleports it back to spawn at full HP.
    private void ApplyMonsterAttack(WorldEntity monster, ulong targetId, int attackDamage)
    {
        if (!_zone.World.TryGet(targetId, out var target))
        {
            return;
        }

        // Face the victim (turn-in-place, no move) so the attack reads on the client; bumps StateRevision to replicate.
        // Sign-of-delta → the 8-direction facing (the same greedy mapping the chase step uses); same-tile leaves
        // facing unchanged (can't happen — the AI only attacks at Chebyshev >= 1, but guard anyway).
        if (TileDeltaToFacing(monster.TileCoord, target.TileCoord) is { } facing)
        {
            monster.TrySetFacing(facing);
        }

        // LIVING-ENEMIES P3: a DEAD (downed) player takes no further hits while waiting to respawn — guard before
        // applying damage so a monster adjacent at the moment of death can't keep hammering the 0-HP body or re-trigger
        // death. (The AI also de-aggros a 0-HP target, but a hit resolved the same tick as death must still be guarded.)
        if (target.OwnerSession is { IsDead: true })
        {
            return;
        }

        // Authoritative damage rides the snapshot HP field (the HUD bar falls). A real change floats a number; a hit
        // on an already-0-HP player is a no-op (no number, no spam). Broadcast to ALL viewers incl. the victim.
        if (target.ApplyDamage(attackDamage))
        {
            BroadcastDamageEvent(target, attackDamage);
            Log.Info($"Monster {monster.NetworkId} hit {target.DisplayName} for {attackDamage} (hp now {target.Stats.Health}).");

            // LIVING-ENEMIES P3: HP hit 0 → the player DIES. Mark the session dead + schedule the respawn (a global
            // delay). The actual teleport-to-spawn + HP refill happens in the per-tick RespawnPlayers pass once the
            // delay elapses, so the dead-guard window above is honoured. MarkDead is a no-op if already dead.
            if (target.Stats.Health <= 0 && target.OwnerSession is { } session && session.MarkDead(_serverTick, _tuning.PlayerRespawnTicks))
            {
                SendSystem(session, "You died.");
                Log.Info($"{target.DisplayName} died; respawn in {_tuning.PlayerRespawnTicks} ticks.");
            }
        }
    }

    // LIVING-ENEMIES P3: per-tick player respawn pass. For each dead session whose respawn delay has elapsed, teleport
    // its entity back to the spawn point, refill HP, replicate the new vitals to the owner, and clear the dead flag.
    // Minimal — no corpse/loot/penalty/death-screen (Phase 4+). The teleport rides the snapshot (the client sees the
    // jump) and SendPlayerStats refills the owner's HUD bar.
    private void RespawnPlayers()
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated || !session.IsRespawnDue(_serverTick))
            {
                continue;
            }

            if (!TryGetSessionEntity(session, out var entity))
            {
                // Entity gone (disconnected mid-death) — just clear the flag so we stop polling it.
                session.MarkAlive();
                continue;
            }

            var spawnTile = _zone.ResolveSpawnTile(Zone.DefaultSpawnTile);
            _zone.Teleport(entity, spawnTile);
            entity.RestoreFullHealth();
            session.MarkAlive();
            // The teleport also resets the held move intent so the respawned player doesn't keep walking from a
            // pre-death key-hold.
            session.ClearMoveIntent();
            SendPlayerStats(session, entity);
            SendSystem(session, "You respawned.");
            Log.Info($"{session.DisplayName} respawned at {spawnTile}.");
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

    private void MarkDirtyDurableTile(WorldEntity entity)
    {
        if (!entity.IsDurable || !entity.CharacterId.HasValue)
        {
            return;
        }

        _dirtyDurableTiles[entity.CharacterId.Value] = new PendingTileSave(
            entity.CharacterId.Value,
            entity.DisplayName,
            entity.TileCoord);
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
            QueueTileSave(session, entity.TileCoord);
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
        uint lastInputSeq,
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
            lastInputSeq,
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
