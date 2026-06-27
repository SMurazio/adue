using LiteNetLib;
using Mmo.Client.Core.Continuous;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Mmo.Shared.Protocol;

namespace Mmo.Client.Core;

public sealed class MmoClient : IDisposable
{
    // CONTINUOUS MIGRATION (Phase 4, v37): the local player PREDICTS per render frame — PredictAndSendMove mints the
    // input seq via the continuous ContinuousPredictor (PredictAndBuffer), sends the {seq, raw dir, dt} MoveIntent, and
    // the predictor integrates the predicted present immediately (zero render lag). It RECONCILES against each
    // snapshot's (Position, LastInputSeq), replaying its unacked buffer over the SHARED collision resolver / walls /
    // radius / speed the server uses, so steady walking and wall slides open NO correction (the determinism contract).
    // The LOCAL player renders the predictor's RenderX/Y; REMOTE entities + monsters render the continuous
    // RemotePositionInterpolator's fixed-delay playout glide (Phase 5 — one driver smooths both continuous players
    // and tile-stepped monsters; the per-kind TileInterpolator / MonsterHopInterpolator split was retired). The
    // obsolete tile LocalPlayerPredictor + its dead plumbing were DELETED earlier; targeting/harvest still read the
    // CONFIRMED tile, never the predicted/interpolated position. Prediction is always on (the dev-only A/B raw-vs-
    // predicted toggle was removed with the migration); the local player always renders the predictor.

    // remote-interp-tighten Part A: the remote jitter buffer was 1.3 * cadence (~1.3 tiles of steady-state lag) —
    // conservative for tile-stepped movement, which steps at a KNOWN regular cadence, so the buffer only needs to
    // absorb network ARRIVAL JITTER, not a full step. The jitter that matters is network (snapshots land on ~50ms
    // tick boundaries), NOT cadence-scaled, so the floor is a FIXED ms (one snapshot interval) rather than a pure
    // cadence multiple. The effective remote delay is now max(RemoteInterpolationCadenceMultiplier * cadence,
    // RemoteInterpolationMinBufferMs): at the 150ms default cadence that is max(75, 50) = 75ms — about half a tile
    // of lag instead of ~1.3 tiles, while still keeping >= one snapshot interval buffered so TryStartNextStep
    // reliably has the next tile ready under normal arrival jitter (no stalls/stutter). Lower bound is the fixed
    // floor; the multiplier carries the rest for slower (longer-cadence) entities. Live-overridable via
    // SetRemoteInterpolationBufferMs (the F1 Movement-tab "Remote interp buffer" knob); -1 = use this computed default.
    public const double RemoteInterpolationCadenceMultiplier = 0.5d;

    // remote-interp-tighten Part A: the FIXED minimum remote jitter buffer (ms) — ~one 20Hz snapshot interval.
    // This is the floor of the computed remote delay so the buffer always absorbs at least one snapshot-arrival
    // gap regardless of cadence; the network jitter it guards against is fixed-ms, not cadence-scaled.
    public const double RemoteInterpolationMinBufferMs = 50d;

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
    // COMBAT-QOL: a DRAIN queue of cosmetic damage events (one per DamageEventMessage) the presentation layer empties
    // each frame to spawn floating "-N" numbers. Unlike the chat/error logs (which accumulate), these are transient —
    // DrainDamageEvents copies and clears them so they never grow unbounded under rapid hits. Capped on enqueue so a
    // hostile flood can't balloon the buffer if the renderer ever stalls draining.
    private readonly List<DamageEvent> _damageEvents = [];
    private const int MaxBufferedDamageEvents = 256;
    private readonly HashSet<uint> _snapshotVisibleScratch = [];
    private readonly List<uint> _staleEntityScratch = [];
    private readonly long _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly ClientMovementTrace _movementTrace;
    private readonly ClientInventory _inventory = new();

    // S93: client-only artificial-latency injector (debug tooling, live F5). Inactive by default (0 ms ⇒ the
    // default I/O path is unchanged); when set > 0 it holds both outbound sends and inbound (decoded) messages
    // for a symmetric one-way delay so the movement models can be felt under real-world RTT.
    private readonly NetLatencySimulator _latency = new();

    // Acks the highest *contiguously*-received snapshot sequence (S47a), not the latest one seen, so the
    // server never advances a viewer's acked baseline past a sequence the client missed under UDP
    // loss/reorder — the prerequisite that makes S47b's cumulative step-deltas safe.
    private readonly SnapshotContiguityTracker _contiguity = new();

    private NetPeer? _serverPeer;
    private PendingSnapshot? _pendingSnapshot;
    private uint? _lastAppliedSnapshotSequence;
    // S76: the recipient-scoped step sequence from the most recent snapshot header (the server's count of our
    // own accepted tile moves). Stashed only — the predictor's reconcile is UNCHANGED this stage; S77 will
    // match this against the predicted step to fix the reconcile rubberband.
    private uint _lastRecipientStepSeq;

    // CONTINUOUS MIGRATION (Phase 3, v36): the last INTEGRATED input seq from the most recent snapshot header — the
    // highest MoveIntent InputSeq the server has applied for us. Stored only this phase (the Phase-4 predictor will
    // trim/replay its unacked input buffer against it). Exposed read-only for diagnostics / Phase-4.
    private uint _lastInputSeq;

    // DIAG1/NET5: snapshots-received-per-second rate (the `recv/s` confirm-channel-alive read-out). The original
    // DIAG1 metric used a tumbling 1-second window that ONLY republished on the next arrival after the window
    // elapsed: when arrivals slowed or stopped (idle, or a loss burst) the window never closed and the read-out
    // froze at a STALE value — misreading the same number (~1.0) at both 1% and 10% loss because what it actually
    // reported was the last full window, not the current rate. NET5 replaces it with a true TRAILING-WINDOW rate:
    // a ring of the last N arrival timestamps, and the rate is COMPUTED AT READ TIME (MovementDebug) as the count
    // of arrivals within the trailing one second up to the current clock — so it falls toward 0 the instant
    // arrivals stop and reads the real ~20/s under healthy delivery, regardless of when the last one landed.
    // Measurement only — fed by NoteSnapshotReceived on every applied snapshot; never influences movement.
    private const int SnapshotRecvTimestampCapacity = 64; // > one second of 20 Hz arrivals, with headroom
    private readonly TimeSpan[] _snapshotRecvTimestamps = new TimeSpan[SnapshotRecvTimestampCapacity];
    private int _snapshotRecvTimestampCount;
    private int _snapshotRecvTimestampHead; // index of the oldest entry

    // COMBAT-S2B: the attack stream's OWN monotonic sequence counter, entirely SEPARATE from the move seq (the
    // NET6 lesson — two streams must never share a cursor). Every SendAttack mints the next attack seq off THIS
    // counter only; it never touches the move seq, and the move seq never touches it. The server dedups attacks
    // on its matching independent _lastAttackSeq cursor.
    private uint _attackSeq;

    // MOVEMENT-ACTIONS Phase B1: the ACTION stream's OWN monotonic sequence counter — SEPARATE from BOTH the move seq
    // AND the attack seq (the NET6 lesson — a third stream gets a third cursor). Every SendAction mints the next action
    // seq off THIS counter only; it never touches the move/attack seqs, and they never touch it. The server dedups
    // actions on its matching independent _lastActionSeq cursor.
    private uint _nextActionSeq;

    private Guid _localCharacterId;
    private TileCoord? _loginTile;
    private TimeSpan _currentTime;
    private bool _disposed;

    // CONTINUOUS MIGRATION (Phase 4): the LOCAL-player continuous predictor (predict -> reconcile -> replay, smooth
    // zero-lag render). Created lazily by EnsurePredictor once we know the zone (blocked map), the local entity, and
    // the server body radius; null until then (and on the web/headless paths that never input). Prediction is now
    // THE model — always on; the dev-only A/B raw-vs-predicted toggle was removed with the migration. Local player
    // ONLY — remote entities stay raw (Phase 5). Lives on THIS outer class (not ClientEntity): the predicted
    // RenderX/RenderY overrides only the local entity's rendered position in the render-state builders; the confirmed
    // Tile/Position stay authoritative for targeting (S53 invariant). See Continuous.ContinuousPredictor.
    private ContinuousPredictor? _predictor;

    // CONTINUOUS MIGRATION (Phase 4a, re-attach freeze fix): the SINGLE persistent monotonic input-seq high-water for
    // ALL movement — the source of truth that survives predictor re-attach (respawn, AOI re-entry — each nulls the
    // predictor then rebuilds one). Both the predictor path and the pre-spawn path mint from THIS, so a sent seq is
    // ALWAYS strictly above every
    // previously-sent seq, hence above the server's acked cursor (_lastInputSeq) → the server NEVER rejects a
    // post-re-attach MoveIntent as a stale dup. (The bug it fixes: a fresh per-predictor counter restarted at 0 and
    // minted 1,2,3… <= the server's already-high cursor N → every MoveIntent rejected until it climbed back past N → a
    // multi-second rubberband/freeze proportional to session length.) EnsurePredictor SEEDS the new predictor from
    // this; PredictAndSendMove updates this from the seq actually minted each frame (both paths).
    private uint _inputSeqHighWater;

    // remote-interp-tighten Part A: a LIVE override (ms) for the remote jitter buffer, set by the F1 Movement-tab
    // "Remote interp buffer" knob so the user dials remote-render lag-vs-smoothness in-client with no restart. < 0
    // (the default) means "use the computed default" (max(multiplier*cadence, MinBufferMs)); >= 0 pins every remote
    // interpolator's delay to exactly this value live. Applies to all current AND future remote interpolators
    // (mirrors how camera smoothing applies live): SetRemoteInterpolationBufferMs re-pushes it onto every existing
    // remote entity immediately. Local-player path (predictor) is UNTOUCHED — this only moves the REMOTE buffer.
    private double _remoteInterpolationBufferOverrideMs = -1d;

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

    // CONTINUOUS MIGRATION (Phase 9): the local player's SERVER-CONFIRMED continuous position (WorldVector), the
    // authoritative state harvest/interact targeting reads — NOT the predicted/interpolated render position (S53).
    // Mirrors LocalTile but keeps the sub-tile offset (the player moves off-grid now), so the client's Euclidean
    // interact-reach check (HarvestTargeting) matches the server's gate, which reads actor.Position continuous. Falls
    // back to the login tile centre before the first snapshot, exactly like LocalTile.
    public WorldVector? LocalConfirmedPosition => LocalNetworkId.HasValue && _entities.TryGetValue(LocalNetworkId.Value, out var entity)
        ? entity.Position
        : (_loginTile is { } tile ? WorldVector.FromTile(tile) : null);

    public IReadOnlyList<ChatLine> ChatLog => _chatLog;

    public IReadOnlyList<ClientError> Errors => _errors;

    // COMBAT-QOL: copy any damage events received since the last call into `destination` (cleared first) and clear the
    // internal queue, so the presentation layer can spawn a floating number per event and the buffer never accumulates.
    // Returns the count copied. Called once per frame by the Godot root.
    public int DrainDamageEvents(List<DamageEvent> destination)
    {
        destination.Clear();
        if (_damageEvents.Count == 0)
        {
            return 0;
        }

        destination.AddRange(_damageEvents);
        _damageEvents.Clear();
        return destination.Count;
    }

    public int EntityCount => _entities.Count;

    public bool DebugMovementEnabled => _movementTrace.Enabled;

    // S76: the recipient-scoped step sequence from the latest snapshot header (server's count of our own
    // accepted tile moves). Exposed read-only for diagnostics / S77's reconcile; not yet consumed by the
    // predictor this stage.
    public uint LastRecipientStepSeq => _lastRecipientStepSeq;

    // CONTINUOUS MIGRATION (Phase 3, v36): the last integrated input seq from the latest snapshot header (the
    // server's count of our applied per-input MoveIntents). Read-only; stored for the Phase-4 predictor's input-buffer
    // trim/replay.
    public uint LastInputSeq => _lastInputSeq;

    // DIAG1: the live movement-debug read-out. The base snapshot (sent/confirmed tile, queue depth, cadence, latency,
    // render) comes from the trace; we overlay only the snapshot `recv/s` rate. CONTINUOUS MIGRATION (Phase 4): the
    // legacy tile-predictor overlay (pred/conf/lead + reconcile-outcome counters) is gone — the continuous predictor
    // has no step-seq, so those fields were removed and only recv/s is meaningful here.
    public MovementDebugSnapshot MovementDebug
    {
        get
        {
            // CONTINUOUS MIGRATION (Phase 4): the continuous predictor has NO step-seq / tile-reconcile tallies, so the
            // legacy predictor-overlay fields were removed from MovementDebugSnapshot. Only the trace + recv/s rate are
            // live now; we overlay the snapshot rate onto the trace snapshot here.
            return _movementTrace.Snapshot with { SnapshotsPerSecond = SnapshotsPerSecond };
        }
    }

    // Client-side mirror of the owner's private inventory, updated by InventoryUpdate deltas. Read-only
    // view for the renderer; the server stays authoritative (each delta sets the new total).
    public ClientInventory Inventory => _inventory;

    // COMBAT-S1: client-side mirror of the LOCAL player's authoritative vitals (HP/mana/stamina, current+max),
    // last replicated by PlayerStatsMessage. Null until the first PlayerStats arrives (right after login). The
    // HUD reads this read-only; the server stays authoritative — the dev-set window sends AdminSetStat and the
    // confirmed value lands back here.
    public CharacterStats? LocalStats { get; private set; }

    // COMBAT-TUNING: client-side mirror of the server's authoritative combat feel-knobs, last replicated by
    // CombatTuningMessage (login + on change). Null until the first snapshot arrives (right after login). The Godot
    // layer reads this read-only to rebuild the free-aim wedge mesh (half-angle/radius), drive the predictor's
    // swing-root (rootMs), and size the radial cooldown indicator (attackCooldownMs) — so the client never re-derives
    // combat numbers from its own constants. The server stays authoritative; the panel sends AdminSetTuning and the
    // confirmed snapshot lands back here. CombatTuningVersion bumps each time it changes so the Godot layer can
    // cheaply detect "the snapshot changed, rebuild the wedge" without comparing fields.
    public CombatTuningSnapshot? CombatTuning { get; private set; }
    public int CombatTuningVersion { get; private set; }

    // LIVING-ENEMIES P2-POLISH: client-side mirror of the server's per-monster-TYPE tuning, last replicated by
    // MonsterTuningMessage (login + on change). Null until the first snapshot arrives. The F1 Monster tab reads this
    // read-only to list the types (dropdown) and seed the per-type fields; the server stays authoritative (the panel
    // sends AdminSetTuning on "<typeId>.<field>" keys and the confirmed snapshot lands back here). MonsterTuningVersion
    // bumps each change so the Godot layer can cheaply detect "re-seed the panel" like CombatTuningVersion.
    public MonsterTuningSnapshot? MonsterTuning { get; private set; }
    public int MonsterTuningVersion { get; private set; }

    // LIVING-ENEMIES P3: persistent SPAWNER red-tile anchors, from SpawnerMarkerMessage (keyed by a stable spawner id,
    // NOT a monster network id). The Godot layer reads this read-only to paint a RED floor tile at each known spawner's
    // tile. An entry is added on SpawnerMarker(Active=true) (the spawner entered AOI) and dropped on Active=false (it
    // left AOI / was removed). Because the spawner — not the monster — owns the tile, the marker STAYS PUT across the
    // monster's death/respawn (the whole point of the P3 refactor).
    private readonly Dictionary<uint, TileCoord> _spawnerMarkers = [];

    public IReadOnlyDictionary<uint, TileCoord> SpawnerMarkers => _spawnerMarkers;

    // COMBAT-TUNING (radial cooldown): the client clock time of the most recent attack we SENT, and the cooldown
    // duration in effect when we sent it (snapshotted so a mid-cooldown tuning change doesn't retroactively rescale
    // the in-flight sweep). AttackCooldownRemainingFraction reads these against the live clock. This is a LOCAL
    // estimate for the HUD indicator only — the server remains authoritative for whether an attack actually resolves.
    private TimeSpan? _lastAttackSentAt;
    private double _lastAttackCooldownMs;

    // The most recent InteractResult the server sent, with a monotonic counter so a HUD can detect a new
    // result (success or a failure reason like "too_far"/"depleted") without an event subscription. Null
    // until the first interaction completes.
    public InteractResultInfo? LastInteractResult { get; private set; }

    // LOOT P4c: the OPEN corpse loot window's live contents, last replicated by CorpseContentsMessage. Null when no
    // window is open (never opened, or the server sent Open=false — emptied / decayed / out of range). The Godot HUD
    // reads this read-only to render the rarity-coloured loot panel; the server stays authoritative (the panel sends
    // SendLootItem / SendLootAll / SendCloseLoot and the confirmed contents land back here). CorpseLootVersion bumps on
    // every change (open / refresh / close) so the Godot layer can cheaply detect "rebuild the panel" without diffing.
    public ClientCorpseLoot? CorpseLoot { get; private set; }
    public int CorpseLootVersion { get; private set; }

    public IReadOnlyList<ReplicatedEntity> Entities => _entities.Values.Select(static entity => entity.ToSnapshot()).ToArray();

    public IReadOnlyList<EntityRenderState> GetRenderStates()
    {
        return GetRenderStates(_currentTime);
    }

    public IReadOnlyList<EntityRenderState> GetRenderStates(TimeSpan now)
    {
        return _entities.Values.Select(entity => entity.ToRenderState(now, LocalRenderOverride(entity))).ToArray();
    }

    public void CopyRenderStatesTo(ICollection<EntityRenderState> destination, TimeSpan now)
    {
        destination.Clear();
        foreach (var entity in _entities.Values)
        {
            destination.Add(entity.ToRenderState(now, LocalRenderOverride(entity)));
        }
    }

    // CONTINUOUS MIGRATION (Phase 4): the predicted RENDER position for the LOCAL player when a predictor is attached,
    // else null (raw render). The predictor lives on this outer class, so the render-state builders inject its smooth
    // RenderX/RenderY here; ClientEntity.ToRenderState applies it only to the local entity and only for the rendered
    // Position (the confirmed Tile stays authoritative for targeting — S53 invariant).
    private LocalRenderState? LocalRenderOverride(ClientEntity entity) =>
        entity.IsLocal && _predictor is { } predictor
            ? new LocalRenderState(
                new RenderPosition(predictor.RenderX, predictor.RenderY),
                // MOVEMENT-ACTIONS Phase B2 (carry-forward #1): the LOCAL player's PREDICTED airborne height (0 unless
                // mid-action). The avatar renders THIS, never its own replicated VerticalOffset (which is for remote
                // viewers) — one Z source, no double-count, and the end seam never pops to a lagging server Z.
                predictor.PredictedVerticalOffset)
            : null;

    // MOVEMENT-ACTIONS Phase B2: the LOCAL player's render override — the predictor's smooth RenderX/RenderY PLUS its
    // predicted airborne height. Carried together so ToRenderState applies both (position + Z) from the one predicted
    // source for the local entity, while remote entities keep gliding off their playout buffer.
    private readonly record struct LocalRenderState(RenderPosition Position, double VerticalOffset);

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
        // PollEvents fires NetworkReceive synchronously: with latency inactive each message is handled inline
        // (default path); with latency active each decoded message is buffered into the inbound queue instead.
        _netManager.PollEvents();
        // S93: when artificial latency is active, flush the symmetric delay queues for "now". Inbound is drained
        // BEFORE the driver tick (below) so a snapshot whose delay just elapsed re-bases the prediction this same
        // poll; outbound is flushed so held sends leave on schedule. HasPending keeps draining in-flight items
        // even right after latency is lowered to 0, so nothing queued under the old delay is stranded. At 0 ms
        // with empty queues this is a pair of cheap counter checks, so the default path stays free.
        if (_latency.Active || _latency.HasPending)
        {
            _latency.FlushInboundDue(now, HandleMessage);
            _latency.FlushOutboundDue(now, SendNow);
        }

        // CONTINUOUS MIGRATION (Phase 4): the per-frame predict + AdvanceRender are driven by the Godot caller on the
        // input/render path (PredictAndSendMove mints/sends the input and integrates the predicted present;
        // AdvanceRender once/frame decays the cosmetic render offset). Reconcile happens on snapshot apply. Nothing
        // extra to drive here per poll.
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

    // CONTINUOUS MIGRATION (Phase 4): the predictor MINTS the seq (PredictAndBuffer) then we Send the MoveIntent with
    // that SAME seq — sent == buffered. When no predictor is attached yet (pre-spawn) we fall back to a local counter so
    // the pre-spawn send path still works. AdvanceRender is driven once/frame by the caller (Poll path), not here.
    //
    // The raw world-axis direction (DirX/DirY; a zero vector is STOP) + the frame's dt. The caller drives this once PER
    // RENDER FRAME with that frame's delta (already clamped to 0.25 caller-side). PredictAndBuffer re-clamps to the
    // shared MaxInputDtSeconds and BUFFERS the clamped dt (buffered == sent == server-integrated). We Send the dt
    // AS-IS (the server re-clamps too). Before the predictor attaches (pre-spawn) we don't predict but still send via
    // the fallback counter. Sent UNRELIABLE (latest frame wins). Returns the seq sent.
    public uint PredictAndSendMove(float dirX, float dirY, float dtSeconds)
    {
        uint seq;
        var sendDirX = dirX;
        var sendDirY = dirY;
        if (_predictor is { } predictor)
        {
            // MOVEMENT-ACTIONS Phase B2: while an action owns movement, SEND the locked heading (not the held WASD) —
            // the predictor predicts the heading internally, and sending it means a server-REJECTED action still moves
            // along the heading there (a smaller, bounded reconcile) while an ACCEPTED action ignores the motion but
            // still ACKs the seq (trimming the buffer). Capture before PredictAndBuffer (which may end the action).
            if (predictor.IsActionActive)
            {
                sendDirX = (float)predictor.ActionHeadingX;
                sendDirY = (float)predictor.ActionHeadingY;
            }

            // The predictor integrates the predicted present immediately (zero latency) and returns the monotonic
            // input seq we stamp on the wire — so sent == buffered for byte-for-byte replay on reconcile. The
            // predictor was seeded from _inputSeqHighWater on attach (EnsurePredictor), so its minted seq is already
            // above the high-water; capture it back so the next re-attach seeds above THIS seq (re-attach freeze fix).
            seq = predictor.PredictAndBuffer(dirX, dirY, dtSeconds);
            _inputSeqHighWater = seq;
        }
        else
        {
            // Pre-spawn (predictor not yet attached): nothing to predict — mint the next seq off the SAME persistent
            // high-water so a later predictor attach (EnsurePredictor) seeds strictly above it and the server never
            // sees a stale dup.
            seq = ++_inputSeqHighWater;
        }

        Send(new MoveIntentMessage(seq, sendDirX, sendDirY, dtSeconds), DeliveryMethod.Unreliable);
        return seq;
    }

    // CONTINUOUS MIGRATION (Phase 4): advance the predictor's cosmetic render catch-up once per render frame (decays
    // the correction offset toward zero so a reconcile correction slides on smoothly). Driven by the Godot caller
    // exactly ONCE per frame. No-op when no predictor is attached (pre-spawn).
    public void AdvanceRender(double frameDtSeconds)
    {
        _predictor?.AdvanceRender(frameDtSeconds);
    }

    // Drops the local entity reference and its predictor (local despawn / AOI exit / logout). Nulling the
    // predictor lets EnsurePredictor re-attach a fresh one (anchored to the new confirmed position) when the
    // local entity respawns, so a stale predictor never drives a removed entity (S47b guard).
    private void ClearLocalEntity()
    {
        LocalNetworkId = null;
        _predictor = null;
        // COMBAT-S1: drop the cached vitals so a stale prior-session value can't briefly feed the HUD on reconnect
        // (the next login always re-sends PlayerStats). Reset alongside the other local-entity state.
        LocalStats = null;
    }

    // CONTINUOUS MIGRATION (Phase 4): attach a fresh continuous predictor for the LOCAL player when one isn't already
    // attached, anchored to the local entity's CURRENT confirmed Position. Idempotent (no-op if already attached) and
    // guarded so it only attaches once we have everything the predictor needs to stay deterministic with the server:
    // the local entity (start position + speed), the zone (blocked map for collision), and the server body radius
    // (replicated on ServerHello). Prediction is always on now, so the predictor attaches as soon as those are known.
    // Called at the lifecycle seams — after the local entity is upserted/spawned and at the end of ApplySnapshot — so
    // a respawn / AOI re-entry (which nulls the predictor via ClearLocalEntity) re-attaches to the fresh confirmed
    // position (the S47b-class guard).
    private void EnsurePredictor()
    {
        if (_predictor is not null
            || Zone is null
            || Server is null
            || LocalNetworkId is not { } localId
            || !_entities.TryGetValue(localId, out var localEntity))
        {
            return;
        }

        // CONTINUOUS MIGRATION (Phase 4a, re-attach freeze fix): SEED the fresh predictor's input-seq counter from the
        // client's persistent high-water so its first minted seq (++_nextInputSeq) is strictly above every
        // previously-sent seq — hence above the server's acked cursor. Without this seed a mid-session re-attach
        // restarts at 0 and the server rejects every MoveIntent until the counter climbs back past the cursor.
        _predictor = new ContinuousPredictor(
            DerivePredictorSpeed(localEntity),
            localEntity.Position.X,
            localEntity.Position.Y,
            Zone.BlockedTiles,
            Server.BodyRadiusUnits,
            _inputSeqHighWater);
    }

    // CONTINUOUS MIGRATION (Phase 4): derive the predictor integrate speed (units/sec) from the entity's tick-quantized
    // effective cadence — speed = 1000 / EffectiveStepCooldownMs. EXACT at multiplier 1.0; the fractional-multiplier
    // residual is bounded by one tick-quant and absorbed by the reconcile budget (documented accepted mispredict).
    // Matches the server's BaseMoveSpeedUnitsPerSecond = 1000/StepCooldownMs derivation at multiplier 1.0.
    private double DerivePredictorSpeed(ClientEntity entity)
    {
        var cadenceMs = ResolveCadence(entity.StepCooldownMs);
        return cadenceMs > 0d ? 1000d / cadenceMs : 0d;
    }

    // CONTINUOUS MIGRATION (Phase 4): reconcile the local predictor against the just-applied confirmed state — snap
    // the replay base to the server's authoritative Position and drop/replay the unacked input buffer against the
    // latest integrated input seq (stashed in _lastInputSeq off the snapshot header). Called from BOTH ApplySnapshot
    // paths: the in-snapshot path (local entity present in the payload while moving) AND the delta'd-out path (an idle
    // local player is absent from the payload but the header still rides LastInputSeq — without this the prediction
    // never re-anchors at rest). No-op when no predictor is attached.
    private void ReconcileLocalPredictor(ClientEntity localEntity)
    {
        _predictor?.Reconcile(localEntity.Position, _lastInputSeq);
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

    // COMBAT-S2B / FREEAIM: send a melee attack with a continuous AIM ANGLE. Mints the next sequence off the
    // DEDICATED _attackSeq counter (never _moveSequence) and sends RELIABLE-ORDERED so the attack is never silently
    // lost (attacks are low-rate, so reliable retransmit is fine — unlike movement's redundant-unreliable). No target
    // id: the server resolves a geometric SECTOR about `aimAngle` (the player→cursor world bearing the caller
    // computed and quantized via AimAngle.Quantize). No client-side damage prediction — the authoritative result
    // lands via the public-HP snapshot (the target's overhead bar drops); the caller may show a cosmetic swing/wedge
    // immediately. Returns the attack seq sent (for tests / diagnostics).
    public uint SendAttack(ushort aimAngle)
    {
        var sequence = ++_attackSeq;

        // CONTINUOUS MIGRATION (Phase 4): the continuous predictor has NO server-tick estimator and NO swing-root —
        // it reconciles every snapshot, so the client does NOT predict the attack-movement root this phase. Send
        // authoredTick = 0; the server still roots the attacker's movement authoritatively (clamped into its receive
        // window) and the predictor's reconcile absorbs any brief root mismatch. (The tile-predictor's
        // EstimateServerTick / ApplyAttackMovementRootAt swing-root mirror was deleted with LocalPlayerPredictor.)
        Send(new AttackMessage(sequence, AttackKind.MeleeCone, aimAngle, AuthoredTick: 0u), DeliveryMethod.ReliableOrdered);

        // COMBAT-TUNING (radial cooldown): record this swing's send time + the cooldown duration in effect now (the
        // replicated attackCooldownMs, falling back to the shared default before the first snapshot) so the HUD's
        // radial cooldown indicator on the LMB slot can sweep from now to now+cooldown. Local HUD estimate only —
        // the server stays authoritative for whether the attack actually resolved.
        _lastAttackSentAt = _currentTime;
        _lastAttackCooldownMs = CombatTuning?.AttackCooldownMs ?? DefaultAttackCooldownMs;

        return sequence;
    }

    // MOVEMENT-ACTIONS Phase B2: trigger a movement action (jump) AND PREDICT it locally (Model A — the client runs the
    // deterministic action on its own clock from the trigger, leading the server by ~RTT along the same arc; the
    // existing reconcile carries the lead and absorbs a rejection). Resolves the def from the SHARED registry, decodes
    // the launch heading to a unit vector, and calls the predictor's BeginAction — which DECLINES (returns false) if an
    // action is already active (one-at-a-time, design §2.8) or the trigger is degenerate. On a local decline we do NOT
    // send the intent, mirroring exactly what the server's can-act would reject — so the spam case never mispredicts.
    // Otherwise mint the DEDICATED _nextActionSeq (never the move/attack seq — the NET6 third-cursor lesson) and send
    // the ActionIntent RELIABLE-ORDERED. The wire still carries only (actionId, heading) — never a height/distance/
    // duration, which live in the server-side def (anti-cheat). AuthoredTick stays 0: Model A anchors the action at the
    // server RECEIPT tick (the measure-first probe showed an EstimateServerTick would only zero an invisible temporal
    // lead — see todo/N-phaseB2). Returns the action seq sent, or null if the trigger was declined locally.
    public uint? SendAction(byte actionId, ushort heading)
    {
        var headingVector = AimAngle.ToUnitVector(heading);

        // Predict locally when a predictor is attached. BeginAction enforces one-at-a-time + a non-degenerate trigger;
        // a decline means "don't send" (the server would reject it too). Resolve the def + derive the action's average
        // ground speed + duration from the SAME shared registry the server executes from, so predict == server on open
        // ground. An unknown id (no def) is dropped here, like the server's registry lookup.
        if (_predictor is { } predictor)
        {
            if (!MovementActionRegistry.Default.TryGet((ActionId)actionId, out var def))
            {
                return null;
            }

            var tickRate = Server?.TickRate ?? 20;
            var (actionSpeed, durationSeconds) = DeriveActionMotion(def, tickRate);
            // Mirror the server's per-action cooldown so a re-trigger inside the lockout is declined LOCALLY (no false
            // predicted jump the server would reject). The server arms it at the action's END for CooldownTicks.
            var cooldownSeconds = tickRate > 0 ? def.CooldownTicks / (double)tickRate : 0d;
            if (!predictor.BeginAction(
                    headingVector.X, headingVector.Y, actionSpeed, durationSeconds, def.JumpHeight, def.AirborneTicks, tickRate, cooldownSeconds))
            {
                return null; // declined locally (already in an action / cooling down / degenerate) — mirror server can-act
            }
        }

        var sequence = ++_nextActionSeq;
        Send(new ActionIntentMessage(sequence, actionId, heading, AuthoredTick: 0u), DeliveryMethod.ReliableOrdered);
        return sequence;
    }

    // MOVEMENT-ACTIONS Phase B2: derive the action's average ground SPEED (units/sec) and DURATION (seconds) from the
    // def + tick rate, so the client's per-frame prediction covers exactly the action's distance over its duration —
    // matching the server's per-tick ForwardArc executor on open ground (the determinism contract). The executor
    // advances def.ForwardDistanceUnits over def.DurationTicks ticks; average speed = distance × tickRate ÷ ticks, and
    // duration = ticks ÷ tickRate. A zero-duration/zero-tickRate def yields (0, 0) ⇒ BeginAction declines it.
    private static (double Speed, double DurationSeconds) DeriveActionMotion(MovementActionDef def, int tickRate)
    {
        if (def.DurationTicks == 0 || tickRate <= 0)
        {
            return (0d, 0d);
        }

        var durationSeconds = def.DurationTicks / (double)tickRate;
        var speed = def.ForwardDistanceUnits * tickRate / def.DurationTicks;
        return (speed, durationSeconds);
    }

    // COMBAT-TUNING: the attack-cooldown fallback used before the first replicated CombatTuningSnapshot arrives —
    // the historical 600 ms constant. Once a snapshot lands, the replicated value drives both the radial cooldown
    // sweep and (server-side) the actual gate, so this is only the pre-login default.
    private const double DefaultAttackCooldownMs = 600d;

    // COMBAT-TUNING (radial cooldown): the local estimate of the attack-cooldown sweep fraction in [0,1] — 1.0 right
    // after a swing, decaying linearly to 0.0 when the cooldown elapses, and 0.0 when no attack is in flight. The HUD
    // feeds this to the LMB autoattack slot's radial indicator. Pure read-out off the last-sent-attack bookkeeping;
    // never mutates state. Also returns the remaining seconds (for the countdown number) via `remainingSeconds`.
    public double AttackCooldownRemainingFraction(out double remainingSeconds)
    {
        return ComputeCooldownFraction(_lastAttackSentAt, _lastAttackCooldownMs, _currentTime, out remainingSeconds);
    }

    // Pure, testable cooldown math: given when the last attack was sent, the cooldown ms in effect then, and the
    // current clock, returns the remaining fraction in [0,1] and the remaining seconds. No attack / non-positive
    // cooldown / elapsed cooldown all read as 0 (ready). Extracted static so the fraction is unit-tested without a
    // live client/socket.
    internal static double ComputeCooldownFraction(TimeSpan? lastAttackSentAt, double cooldownMs, TimeSpan now, out double remainingSeconds)
    {
        remainingSeconds = 0d;
        if (lastAttackSentAt is not { } sentAt || cooldownMs <= 0d)
        {
            return 0d;
        }

        var elapsedMs = (now - sentAt).TotalMilliseconds;
        if (elapsedMs < 0d)
        {
            elapsedMs = 0d;
        }

        var remainingMs = cooldownMs - elapsedMs;
        if (remainingMs <= 0d)
        {
            return 0d;
        }

        remainingSeconds = remainingMs / 1000d;
        return Math.Clamp(remainingMs / cooldownMs, 0d, 1d);
    }

    // S60 admin live-tuning: ask the server to set a tuning key (e.g. "move.stepCooldownMs") to a value.
    // Reliable-ordered. The server admin-gates and clamps/validates; a non-admin send is silently ignored
    // server-side. No client-side prediction — the panel just shows the value it sent.
    public void SendAdminSetTuning(string key, double value)
    {
        Send(new AdminSetTuningMessage(key, value), DeliveryMethod.ReliableOrdered);
    }

    // COMBAT-S1: ask the server to set the LOCAL player's current vital (0=HP, 1=mana, 2=stamina) to value. The
    // F7 dev-set window drives this. Reliable-ordered; the server admin-gates + clamps, and the authoritative
    // result lands back via PlayerStatsMessage (no client-side prediction). A non-admin send is a server no-op.
    public void SendAdminSetStat(byte stat, int value)
    {
        Send(new AdminSetStatMessage(stat, value), DeliveryMethod.ReliableOrdered);
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
            // Decode synchronously (the reader/packet is recycled in the finally below). The DECODED message is
            // what gets buffered under S93 latency injection — never the raw reader.
            var message = ProtocolCodec.Decode(reader.GetRemainingBytes());
            if (_latency.Active)
            {
                // S93: hold the decoded inbound message for the one-way delay instead of handling it now; Poll
                // drains due items into HandleMessage in arrival order. At 0 ms this branch is skipped and the
                // message is handled inline exactly as before.
                _latency.EnqueueInbound(message, _currentTime);
            }
            else
            {
                HandleMessage(message);
            }
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
                // EntitySpawn.Tile stays a genuine TileCoord anchor (Phase 3 keeps spawns/login tile-typed); lift it
                // to the continuous Position the upsert now takes. Tile-centred, so behaviour is unchanged.
                UpsertEntity(spawn.NetworkId, spawn.CharacterId, spawn.Kind, spawn.DisplayName, WorldVector.FromTile(spawn.Tile), spawn.Facing, spawn.StepCooldownMs);
                break;
            case MovementSpeedChangedMessage speed:
                HandleMovementSpeedChanged(speed);
                break;
            case EntityDespawnMessage despawn:
                _entities.Remove(despawn.NetworkId);
                // LIVING-ENEMIES P3: spawner markers are keyed by spawner id (not monster network id) and are dropped by
                // an explicit SpawnerMarker(Active=false), so an entity despawn no longer clears a marker — a killed
                // monster despawns but its spawner's red tile persists until the spawner itself leaves AOI.
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
            case CorpseContentsMessage corpse:
                HandleCorpseContents(corpse);
                break;
            case PlayerStatsMessage stats:
                LocalStats = stats.Stats;
                break;
            case CombatTuningMessage tuning:
                // COMBAT-TUNING: adopt the replicated combat snapshot and bump the version so the Godot layer
                // rebuilds the wedge mesh / re-derives the cooldown duration. Pure mirror — no prediction here; the
                // predictor's swing-root reads the live RootMs at SendAttack time.
                CombatTuning = tuning.Tuning;
                CombatTuningVersion++;
                break;
            case MonsterTuningMessage monsterTuning:
                // LIVING-ENEMIES P2-POLISH: adopt the replicated per-type tuning and bump the version so the F1 Monster
                // tab re-seeds. Pure mirror — the client runs no monster simulation; this only feeds the admin panel.
                MonsterTuning = monsterTuning.Tuning;
                MonsterTuningVersion++;
                break;
            case SpawnerMarkerMessage spawnerMarker:
                // LIVING-ENEMIES P3: a persistent spawner's red-tile marker entered (Active=true) or left (Active=false)
                // AOI. Add/update or drop it, keyed by the stable spawner id so it survives the monster's death/respawn.
                if (spawnerMarker.Active)
                {
                    _spawnerMarkers[spawnerMarker.SpawnerId] = spawnerMarker.Tile;
                }
                else
                {
                    _spawnerMarkers.Remove(spawnerMarker.SpawnerId);
                }

                break;
            case DamageEventMessage damage:
                // COMBAT-QOL: queue a cosmetic damage event for the presentation layer to float a number. Drop the
                // OLDEST if the buffer is somehow full (renderer stalled / flood) so it can never grow unbounded.
                if (_damageEvents.Count >= MaxBufferedDamageEvents)
                {
                    _damageEvents.RemoveAt(0);
                }

                _damageEvents.Add(new DamageEvent(damage.NetworkId, damage.Amount, damage.Health));
                break;
        }
    }

    private void HandleInteractResult(InteractResultMessage interact)
    {
        var sequence = (LastInteractResult?.Sequence ?? 0) + 1;
        LastInteractResult = new InteractResultInfo(interact.Success, interact.Reason, sequence);
    }

    // LOOT P4c: adopt an OPEN corpse's replicated contents (or a close). Open=true sets/refreshes CorpseLoot to the new
    // rarity-coloured rows; Open=false (or empty) clears it so the Godot panel hides. Pure mirror — no client authority;
    // the server validates every take. Bumps CorpseLootVersion so the HUD rebuilds only when something changed.
    private void HandleCorpseContents(CorpseContentsMessage message)
    {
        if (message.Open && message.Items.Count > 0)
        {
            CorpseLoot = ClientCorpseLoot.From(message.CorpseNetworkId, message.Items);
        }
        else
        {
            // Close (Open=false) OR an open with zero items (an emptied corpse the server hasn't despawned yet — treat
            // as closed; the despawn/Close follows). Drop the window.
            CorpseLoot = null;
        }

        CorpseLootVersion++;
    }

    // LOOT P4c: send a take-ONE-stack request for the open corpse (the window's per-row take button). Reliable-ordered;
    // the server validates the open pairing + adjacency + eligibility and pushes the refreshed CorpseContents (or a
    // despawn-close if it emptied). No-op if no window is open. corpseNetworkId guards against a stale window.
    public void SendLootItem(uint corpseNetworkId, string templateKey)
    {
        Send(new LootActionMessage(corpseNetworkId, LootActionKind.TakeItem, templateKey), DeliveryMethod.ReliableOrdered);
    }

    // LOOT P4c: send a loot-ALL request for the open corpse (the window's "Loot all" button). Reliable-ordered.
    public void SendLootAll(uint corpseNetworkId)
    {
        Send(new LootActionMessage(corpseNetworkId, LootActionKind.LootAll, string.Empty), DeliveryMethod.ReliableOrdered);
    }

    // LOOT P4c: tell the server we closed the loot window (Escape / close button / out of range) so it forgets the
    // open-loot pairing. Also clears the local mirror immediately so the panel hides without waiting for a round-trip.
    public void SendCloseLoot(uint corpseNetworkId)
    {
        Send(new LootActionMessage(corpseNetworkId, LootActionKind.Close, string.Empty), DeliveryMethod.ReliableOrdered);
        CorpseLoot = null;
        CorpseLootVersion++;
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
        Server = new ServerInfo(hello.ServerName, hello.ProtocolVersion, hello.TickRate, hello.StepCooldownMs, hello.InterestRadiusTiles, hello.BodyRadiusUnits);
        RefreshInterpolatorCadence();
        // CONTINUOUS MIGRATION (Phase 4): ServerHello now carries the body radius the predictor needs. If the local
        // entity already exists (a re-hello after spawn), attach the predictor now that Server (radius) is known;
        // EnsurePredictor is idempotent and no-ops until the local entity + zone are also present.
        EnsurePredictor();
    }

    // DIAG1/NET5: records one applied-snapshot arrival timestamp into the trailing-window ring (the `recv/s`
    // read-out source). The rate itself is computed AT READ TIME by SnapshotsPerSecond so it reflects the CURRENT
    // arrival rate (and decays toward 0 the moment arrivals stop) rather than freezing on a stale tumbling window.
    // Uses the client wall clock (_currentTime, set each Poll). Pure read-out — it counts confirms but never alters
    // movement, prediction, or reconcile.
    private void NoteSnapshotReceived()
    {
        if (_snapshotRecvTimestampCount < SnapshotRecvTimestampCapacity)
        {
            var slot = (_snapshotRecvTimestampHead + _snapshotRecvTimestampCount) % SnapshotRecvTimestampCapacity;
            _snapshotRecvTimestamps[slot] = _currentTime;
            _snapshotRecvTimestampCount++;
        }
        else
        {
            _snapshotRecvTimestamps[_snapshotRecvTimestampHead] = _currentTime;
            _snapshotRecvTimestampHead = (_snapshotRecvTimestampHead + 1) % SnapshotRecvTimestampCapacity;
        }
    }

    // DIAG1/NET5: the true snapshot arrival rate — the count of applied-snapshot arrivals within the trailing one
    // second up to the current clock (_currentTime). Computed at read time so it reads the real ~20/s under healthy
    // delivery and falls toward 0 the instant arrivals stop (no stale tumbling-window freeze). Pure read-out.
    private double SnapshotsPerSecond
    {
        get
        {
            if (_snapshotRecvTimestampCount == 0)
            {
                return 0d;
            }

            var windowStart = _currentTime - TimeSpan.FromSeconds(1);
            var count = 0;
            for (var i = 0; i < _snapshotRecvTimestampCount; i++)
            {
                var slot = (_snapshotRecvTimestampHead + i) % SnapshotRecvTimestampCapacity;
                if (_snapshotRecvTimestamps[slot] > windowStart)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private void HandleSnapshot(WorldSnapshotMessage snapshot)
    {
        if (_lastAppliedSnapshotSequence.HasValue && snapshot.SnapshotSequence <= _lastAppliedSnapshotSequence.Value)
        {
            return;
        }

        // S76: stash the recipient-scoped step sequence off the header. Rides every snapshot (real-delta AND
        // keep-alive). Stash only — no reconcile change this stage (S77 consumes it).
        _lastRecipientStepSeq = snapshot.RecipientStepSeq;
        // CONTINUOUS MIGRATION (Phase 3, v36): stash the last integrated input seq too (rides every snapshot header).
        // Stored only — the Phase-4 predictor consumes it to trim/replay the unacked input buffer.
        _lastInputSeq = snapshot.LastInputSeq;

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
        // DIAG1: tally this fully-applied snapshot for the `recv/s` confirm-channel-rate read-out (once per applied
        // snapshot — a chunked snapshot is assembled before this is reached). Measurement only.
        NoteSnapshotReceived();

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
                    state.Position,
                    state.Facing);
            }

            var confirmation = entity.ApplySnapshot(state.Position, state.Facing, _currentTime, sequence, _lastRecipientStepSeq, serverTick, state.Depleted, state.Health, state.MaxHealth, state.VerticalOffset, state.Velocity);
            if (confirmation.TileChanged)
            {
                _movementTrace.TileConfirmed(
                    state.NetworkId,
                    state.Position.ToTileRounded(),
                    sequence,
                    DateTimeOffset.UtcNow,
                    confirmation.QueueDepth,
                    ResolveCadence(entity.StepCooldownMs),
                    confirmation.RenderPosition);
            }

            // CONTINUOUS MIGRATION (Phase 4): the LOCAL player reconciles the predictor against its freshly-confirmed
            // Position + the snapshot's LastInputSeq (in-snapshot path — local player present while moving).
            if (entity.IsLocal)
            {
                ReconcileLocalPredictor(entity);
            }
        }

        // S84 / CONTINUOUS MIGRATION (Phase 4): the local player must reconcile on EVERY snapshot, even when it is
        // delta'd out of the entity list. The server delta-compresses (re-sends an entity only while its StateRevision
        // changes), so an IDLE local player is absent from the payload — but the header still rides LastInputSeq for
        // exactly this. Without re-running reconcile here, any residual over-prediction latches at rest and never
        // closes. We re-apply the entity's CURRENT (last-known, unchanged) Position/Facing — NOT a fabricated move;
        // the confirmed position is genuinely unchanged (that's why it was delta'd out) — then ReconcileLocalPredictor
        // re-anchors the prediction to truth while idle (converging down to the confirmed position at rest). Only the
        // delta'd-out case is affected; while moving the local player is in every snapshot and takes the in-snapshot
        // path above unchanged.
        if (LocalNetworkId is { } localId
            && !_snapshotVisibleScratch.Contains(localId)
            && _entities.TryGetValue(localId, out var localEntity))
        {
            localEntity.ApplySnapshot(
                localEntity.Position,
                localEntity.Facing,
                _currentTime,
                sequence,
                _lastRecipientStepSeq,
                serverTick,
                localEntity.Depleted,
                localEntity.Health,
                localEntity.MaxHealth,
                // MOVEMENT-ACTIONS Phase B1: preserve the current replicated airborne height on a delta'd-out re-apply
                // (like Depleted/HP) so an idle re-apply never resets a non-zero Z. (While airborne the own-entity is
                // force-included every tick so it takes the in-snapshot path; this guards the unchanged-idle case.)
                localEntity.VerticalOffset,
                // REMOTE-WALK Phase 1 (v39): likewise preserve the current replicated Velocity on a delta'd-out
                // re-apply. The local player is only delta'd out while IDLE (Velocity Zero), so this is Zero in
                // practice; preserving it (rather than fabricating) keeps the re-apply a faithful no-op.
                localEntity.Velocity);

            // CONTINUOUS MIGRATION (Phase 4): an IDLE local player is delta'd out of the payload but still reconciles
            // on the header (LastInputSeq) so any residual over-prediction converges down to the confirmed position at
            // rest. Mirrors the in-snapshot reconcile above.
            ReconcileLocalPredictor(localEntity);
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
                // LIVING-ENEMIES P3: spawner markers are keyed by spawner id + dropped by an explicit Active=false, so a
                // snapshot prune of stale ENTITIES no longer touches them.
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

        // LIVING-ENEMIES P2-POLISH (HUD HP fix): keep LocalStats.Health in sync with the local player's AUTHORITATIVE
        // per-frame SNAPSHOT HP (the same value the overhead bar reads). PlayerStatsMessage only carries the local
        // vitals on login / a stat-change — it is NOT re-sent when a monster hits the player, so LocalStats.Health was
        // stale and the HUD bar didn't fall while the overhead bar did. The snapshot HP IS re-sent on every hit, so
        // mirroring it here makes the HUD's current HP track damage live; MaxHealth/mana/stamina still come from
        // PlayerStatsMessage. Done after the snapshot is fully applied so the local entity's Health is the latest.
        SyncLocalStatsHealthFromSnapshot();

        // CONTINUOUS MIGRATION (Phase 4): attach the local predictor once the local entity + zone + radius are all
        // known (idempotent). Placed at the end of ApplySnapshot so a respawn / AOI re-entry that re-created the local
        // entity this snapshot re-attaches a fresh predictor anchored to the confirmed Position. If it attaches THIS
        // snapshot, the freshly-anchored predictor already sits on truth, so missing this snapshot's reconcile is a
        // no-op; subsequent snapshots reconcile normally.
        EnsurePredictor();

        _lastAppliedSnapshotSequence = sequence;
    }

    // LIVING-ENEMIES P2-POLISH (HUD HP fix): mirror the local player's snapshot Health into LocalStats.Health so the
    // HUD bar reflects damage taken (the snapshot is the authoritative per-frame HP; PlayerStatsMessage is not re-sent
    // on a hit). No-op until both the vitals (PlayerStats, gives MaxHealth/mana/stamina) and the local entity exist.
    // Only the CURRENT Health is overwritten — Max/mana/stamina are preserved from PlayerStatsMessage. Clamped into
    // the known max so a transient out-of-range snapshot can't push current above max.
    private void SyncLocalStatsHealthFromSnapshot()
    {
        if (LocalStats is not { } stats
            || !LocalNetworkId.HasValue
            || !_entities.TryGetValue(LocalNetworkId.Value, out var local))
        {
            return;
        }

        // Guard: a local entity created from a bare snapshot before its real spawn may carry 0/0 HP; only adopt a
        // genuine replicated value (MaxHealth > 0) so we never zero the HUD from a placeholder.
        if (local.MaxHealth == 0)
        {
            return;
        }

        var snapshotHealth = local.Health;
        if (stats.Health != snapshotHealth)
        {
            LocalStats = stats.WithHealth(snapshotHealth);
        }
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
    // MIGRATION (Phase 3 Pass A): the upsert position is now a continuous WorldVector. EntitySpawn (genuine tile
    // anchor) wraps its TileCoord via WorldVector.FromTile; the snapshot path passes the decoded Position directly.
    // In Pass A every position is still tile-centred (the wire sends tiles), so behaviour is byte-identical.
    private ClientEntity UpsertEntity(
        uint networkId,
        Guid characterId,
        EntityKind kind,
        string displayName,
        WorldVector position,
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
                var existingCadence = ResolveCadence(effectiveCooldown);
                existing.SetStepCooldownMs(effectiveCooldown.Value, existingCadence, ResolveInterpolationDelay(existingCadence, existing.IsLocal));
            }

            // EntitySpawn carries no Depleted/HP/vertical/velocity bits (those ride the AOI snapshot), so preserve
            // whatever the last snapshot set rather than resetting a known-depleted node to available, zeroing known HP,
            // or zeroing a replicated airborne height / velocity (REMOTE-WALK Phase 1).
            existing.ApplySnapshot(position, facing, _currentTime, _lastAppliedSnapshotSequence ?? 0, _lastRecipientStepSeq, serverTick: null, existing.Depleted, existing.Health, existing.MaxHealth, existing.VerticalOffset, existing.Velocity);
            if (isLocal)
            {
                LocalNetworkId = networkId;
                // CONTINUOUS MIGRATION (Phase 4): attach the predictor on the spawn/upsert seam too (idempotent), so an
                // EntitySpawn that arrives before a snapshot anchors the predictor to the confirmed position.
                EnsurePredictor();
            }

            return existing;
        }

        // CONTINUOUS MIGRATION (Phase 5): every entity gets ONE continuous remote playout buffer, anchored on the
        // (continuous) spawn position. One driver smooths all remote kinds (the per-kind hop/interp split is gone);
        // the local player keeps the predictor and ignores this buffer in ToRenderState. The playout DELAY is the
        // resolved remote-buffer value (fixed floor + live knob); the local entity's delay is irrelevant (unused).
        var entity = new ClientEntity(
            networkId,
            characterId,
            kind,
            displayName,
            position,
            facing,
            isLocal,
            CreateInterpolator(position, isLocal, effectiveCooldown),
            effectiveCooldown);
        _entities[networkId] = entity;
        if (isLocal)
        {
            LocalNetworkId = networkId;
            // CONTINUOUS MIGRATION (Phase 4): attach the predictor as soon as the local entity exists (idempotent;
            // no-op until zone + radius are also known). The end-of-ApplySnapshot EnsurePredictor covers the rest.
            EnsurePredictor();
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
        var speedCadence = ResolveCadence(cooldown);
        entity.SetStepCooldownMs(cooldown, speedCadence, ResolveInterpolationDelay(speedCadence, entity.IsLocal));

        // CONTINUOUS MIGRATION (Phase 4): live-retune the LOCAL predictor's integrate speed to track the server's new
        // SpeedUnitsPerSecond (derived from the just-updated effective cadence). Mirrors the server adopting the new
        // speed on its next input; only future predicted frames/replays use it (no re-base).
        if (entity.NetworkId == LocalNetworkId && _predictor is { } predictor)
        {
            predictor.SetSpeed(DerivePredictorSpeed(entity));
        }
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

    // remote-interp-tighten Part A: the single source of truth for an interpolator's playout-buffer delay given its
    // cadence and whether it's the local player. LOCAL keeps its existing cadence-multiple buffer untouched. REMOTE
    // uses the LIVE override when set (>= 0), else the computed default max(multiplier*cadence, fixed floor) — so the
    // floor and the live knob both apply at every call site (CreateInterpolator / RefreshInterpolatorCadence /
    // SetStepCooldownMs go through here, except SetStepCooldownMs which lives on ClientEntity and is handled there).
    private double ResolveInterpolationDelay(double cadenceMs, bool isLocal)
    {
        if (isLocal)
        {
            return cadenceMs * LocalInterpolationCadenceMultiplier;
        }

        if (_remoteInterpolationBufferOverrideMs >= 0d)
        {
            return _remoteInterpolationBufferOverrideMs;
        }

        return Math.Max(cadenceMs * RemoteInterpolationCadenceMultiplier, RemoteInterpolationMinBufferMs);
    }

    // remote-interp-tighten Part A: the live remote jitter-buffer override in ms, or null when using the computed
    // default (< 0). Read-only; seeds the F1 Movement-tab "Remote interp buffer" field on panel open and the perf HUD.
    public double? RemoteInterpolationBufferOverrideMs =>
        _remoteInterpolationBufferOverrideMs >= 0d ? _remoteInterpolationBufferOverrideMs : null;

    // remote-interp-tighten Part A: the effective remote buffer (ms) at the DEFAULT cadence — what the F1 field shows
    // before the user pins an override, so the knob seeds with the value actually in effect (not a raw multiplier).
    public double EffectiveDefaultRemoteInterpolationBufferMs =>
        ResolveInterpolationDelay(ResolveCadence(null), isLocal: false);

    // remote-interp-tighten Part A: live-set the remote jitter buffer (ms), applied to every CURRENT remote
    // interpolator immediately (no restart) and to future ones. A NEGATIVE value clears the override (revert to the
    // computed default). Clamped to a sane debug range on the positive side. Local-player prediction is untouched.
    // Mirrors how camera smoothing applies live: set the field, then re-push cadence/delay to all entities.
    public void SetRemoteInterpolationBufferMs(double bufferMs)
    {
        _remoteInterpolationBufferOverrideMs = bufferMs < 0d ? -1d : Math.Min(bufferMs, 2000d);
        RefreshInterpolatorCadence();
    }

    // Recomputes every entity's tween cadence. Each entity keeps its OWN advertised cooldown if it has one
    // (per-entity speed, S51) and only falls back to the ServerHello global when it doesn't — so a global
    // refresh (e.g. ServerHello arriving) never clobbers a per-entity cadence.
    private void RefreshInterpolatorCadence()
    {
        foreach (var entity in _entities.Values)
        {
            var cadence = ResolveCadence(entity.StepCooldownMs);
            var delay = ResolveInterpolationDelay(cadence, entity.IsLocal);
            entity.UpdateInterpolationCadence(cadence, delay);
        }
    }

    // CONTINUOUS MIGRATION (Phase 5): build the continuous remote playout buffer anchored on the spawn position.
    // The playout DELAY is the resolved value (ResolveInterpolationDelay: remote = floor + live knob; local =
    // cadence-multiple, though the local entity's buffer is unused — the predictor renders it). The cadence itself
    // no longer quantizes the glide (the buffer lerps on real arrival clocks), so only the delay is passed.
    private RemotePositionInterpolator CreateInterpolator(WorldVector initialPosition, bool isLocal, ushort? stepCooldownMs)
    {
        var cadence = ResolveCadence(stepCooldownMs);
        var delay = ResolveInterpolationDelay(cadence, isLocal);
        return new RemotePositionInterpolator(initialPosition, delay);
    }

    private void Send(IProtocolMessage message, DeliveryMethod deliveryMethod)
    {
        if (OutboundSinkForTests is not null)
        {
            OutboundSinkForTests(message, deliveryMethod);
            return;
        }

        // S93: when artificial latency is active, hold the send for the one-way delay instead of dispatching
        // now; Poll flushes due items. At 0 ms the simulator is inactive and this branch is skipped entirely,
        // so the default path is unchanged.
        if (_latency.Active)
        {
            _latency.EnqueueOutbound(message, deliveryMethod, _currentTime);
            return;
        }

        SendNow(message, deliveryMethod);
    }

    // The actual wire send. Used directly when no artificial latency is active, and as the flush sink for the
    // S93 latency simulator's outbound queue.
    private void SendNow(IProtocolMessage message, DeliveryMethod deliveryMethod)
    {
        if (_serverPeer is null)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        _serverPeer.Send(ProtocolCodec.Encode(message), deliveryMethod);
    }

    // S93: live-sets the artificial one-way network latency (ms) added symmetrically to both directions, so
    // the felt round-trip ≈ 2× this value. 0 disables injection (default path, zero overhead). Live F5 — no
    // restart. Client-only; the injected delay flows through the EXISTING send/receive paths so the predictor
    // calibration, reconcile, and accept/deny confirms all naturally see the delayed traffic. Negative inputs
    // are clamped to 0 by the simulator.
    public void SetSimulatedLatencyMs(int oneWayMs)
    {
        _latency.SetLatencyMs(oneWayMs);
    }

    // S93: the active artificial one-way latency in ms (0 = injection off). Read-only; used to seed the F5
    // field on panel open and to show the value in the perf HUD.
    public int SimulatedLatencyMs => _latency.LatencyMs;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ClientEntity
    {
        // HOP-ARC (cosmetic, monster-only): the peak height in WORLD UNITS (tiles) a slime's render LIFTS at the
        // apex of its jump arc. ~half a tile reads as a clear bounce without looking like a launch. Phase-8
        // "Option B": the server hop stays a flat sparse Position jump (authoritative, unchanged); the client adds
        // a parabolic vertical lift SYNCED to the horizontal interp so the slime visibly arcs up-and-over and lands
        // exactly where/when the server hop lands. A named const (the user feel-tunes it); pure cosmetic.
        private const double MonsterHopPeakHeight = 0.5d;

        // CONTINUOUS MIGRATION (Phase 5): ONE remote render driver for EVERY kind (other players, tile-stepped
        // monsters, resources) — a fixed-delay continuous playout buffer that lerps between received positions so
        // all of them glide smoothly (the per-kind TileInterpolator + MonsterHopInterpolator split is gone). Only
        // the LOCAL player bypasses it (the predictor renders the local player; this still buffers the local
        // confirmed positions but ToRenderState ignores it for the local entity). Anchored on the spawn position.
        private readonly RemotePositionInterpolator _remoteInterp;

        public ClientEntity(
            uint networkId,
            Guid characterId,
            EntityKind kind,
            string displayName,
            WorldVector position,
            Direction8 facing,
            bool isLocal,
            RemotePositionInterpolator remoteInterp,
            ushort? stepCooldownMs)
        {
            NetworkId = networkId;
            CharacterId = characterId;
            Kind = kind;
            DisplayName = displayName;
            Position = position;
            Facing = facing;
            IsLocal = isLocal;
            _remoteInterp = remoteInterp;
            StepCooldownMs = stepCooldownMs;
        }

        public uint NetworkId { get; }

        public Guid CharacterId { get; private set; }

        public EntityKind Kind { get; private set; }

        public string DisplayName { get; private set; }

        // MIGRATION (Phase 3 Pass A): the entity now stores a continuous WorldVector Position (decoded off the
        // snapshot); Tile is DERIVED from it so HarvestTargeting/LocalTile/the predictor+interpolators (all of
        // which read .Tile) keep working unchanged. In Pass A the wire still delivers tile-centred positions, so
        // Tile is byte-identical to the old stored field. Pass B feeds it genuinely-fractional positions.
        public WorldVector Position { get; private set; }

        public TileCoord Tile => Position.ToTileRounded();

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

        // COMBAT-S2A: public HP replicated on the AOI snapshot, threaded to the overhead red bar. 0/0 for
        // entities without vitals (resources). Carried alongside Depleted (snapshot-driven, not interpolated).
        public ushort Health { get; private set; }

        public ushort MaxHealth { get; private set; }

        // MOVEMENT-ACTIONS Phase B1: the REPLICATED airborne height (world units) — server-authoritative, snapshot-
        // driven like Depleted/Health (NOT interpolated). 0 grounded, >0 mid-jump. ToRenderState threads it to the
        // render state so the visual lifts by the real arc; the local player's own server-confirmed jump uses this
        // same path (no prediction in B1).
        public double VerticalOffset { get; private set; }

        // REMOTE-WALK Phase 1 (v39): the REPLICATED continuous velocity (units/sec) — server-authoritative, snapshot-
        // driven like VerticalOffset. Zero at rest, non-zero while walking. Phase 1 BUFFERS it in the interpolator
        // (via Confirm) but does NOT extrapolate from it yet (Sample is unchanged) — that is Phase 2.
        public WorldVector Velocity { get; private set; } = WorldVector.Zero;

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
            // CONTINUOUS MIGRATION (Phase 5): a placeholder→Monster reveal no longer swaps render drivers — ONE
            // RemotePositionInterpolator drives every kind, so a Player placeholder revealed as a Monster keeps
            // gliding off the same buffer (no hand-off, no driver re-anchor). The hop arc is retired.
        }

        public EntityConfirmationDebug ApplySnapshot(WorldVector position, Direction8 facing, TimeSpan receivedAt, uint snapshotSequence, uint recipientStepSeq, uint? serverTick, bool depleted = false, ushort health = 0, ushort maxHealth = 0, double verticalOffset = 0d, WorldVector velocity = default)
        {
            var previousTile = Tile;
            // Position/Facing always track the SERVER-CONFIRMED state: LocalTile (harvest/click targeting) reads
            // the derived Tile, and the renderer's AuthoritativeTile uses it. Prediction/interpolation only affect
            // the rendered position, never this authoritative position.
            Position = position;
            var tile = Tile;
            Depleted = depleted;
            // COMBAT-S2A: adopt the replicated public HP (snapshot-driven, like Depleted). Preserving callers
            // (the delta'd-out local re-apply and EntitySpawn) pass the current values so HP isn't reset to 0.
            Health = health;
            MaxHealth = maxHealth;
            // MOVEMENT-ACTIONS Phase B1: adopt the replicated airborne height (snapshot-driven, like Depleted/HP). The
            // local player's own jump is server-confirmed in B1 (no prediction), so it too renders from this confirmed
            // value rather than a predicted Z.
            VerticalOffset = verticalOffset;
            // REMOTE-WALK Phase 1 (v39): adopt the replicated velocity (snapshot-driven, like VerticalOffset/HP). It is
            // BUFFERED into the playout interpolator below (Confirm) so Phase 2 can dead-reckon; nothing reads it for
            // extrapolation yet (Sample is unchanged this phase).
            Velocity = velocity;
            LastSeenSnapshotSequence = snapshotSequence;
            // CONTINUOUS MIGRATION (Phase 4): the LOCAL predictor lives on the OUTER MmoClient (continuous), not here.
            // ClientEntity always tracks the SERVER-CONFIRMED state — the outer class reconciles the predictor against
            // this confirmed Position and overrides only the RENDERED local position. recipientStepSeq/serverTick are
            // no longer consumed by a tile predictor here (kept on the signature for the trace/hop/interp callers).
            _ = recipientStepSeq;
            _ = serverTick;
            Facing = facing;
            // CONTINUOUS MIGRATION (Phase 5): feed the CONTINUOUS confirmed position into the one remote playout
            // buffer (every kind glides the same — the per-kind hop/interp split is retired). The local player also
            // feeds it (harmless — its render comes from the predictor, ToRenderState ignores this for IsLocal).
            // The debug carries the buffered-sample depth + the last render position for the trace's queue-depth /
            // render read-out (the effective cadence is resolved at the outer call site from the entity's cooldown).
            // REMOTE-WALK Phase 1 (v39): thread the replicated velocity into the playout buffer alongside the position +
            // height. The interpolator STORES it on the buffered sample but does NOT extrapolate from it yet (Sample is
            // unchanged) — Phase 1 is wire + buffering only; Phase 2 turns on dead-reckoning.
            _remoteInterp.Confirm(position, receivedAt, verticalOffset, velocity);
            return new EntityConfirmationDebug(
                tile != previousTile,
                _remoteInterp.BufferedSampleCount,
                _remoteInterp.RenderPosition);
        }

        public void UpdateInterpolationCadence(double stepDurationMs, double interpolationDelayMs)
        {
            // CONTINUOUS MIGRATION (Phase 5): only the playout-buffer DELAY drives the continuous interpolator
            // (the cadence/step-duration no longer quantizes the glide — it lerps on real arrival clocks). The
            // delay is the already-resolved value (ResolveInterpolationDelay: floor + live remote-buffer knob).
            _ = stepDurationMs;
            _remoteInterp.UpdateDelay(interpolationDelayMs);
        }

        // Applies a per-entity cadence (from EntitySpawn / MovementSpeedChanged). stepCooldownMs null clears
        // the override (the entity reverts to the global cadence the caller resolved). interpolationDelayMs is the
        // already-resolved playout buffer (ResolveInterpolationDelay: fixed floor + live remote-buffer override).
        public void SetStepCooldownMs(ushort? stepCooldownMs, double cadenceMs, double interpolationDelayMs)
        {
            StepCooldownMs = stepCooldownMs;
            _ = cadenceMs;
            _remoteInterp.UpdateDelay(interpolationDelayMs);
            // CONTINUOUS MIGRATION (Phase 4): the local predictor's speed retune lives on the OUTER MmoClient
            // (HandleMovementSpeedChanged calls _predictor.SetSpeed), not here — ClientEntity no longer owns a predictor.
        }

        public ReplicatedEntity ToSnapshot()
        {
            return new ReplicatedEntity(NetworkId, CharacterId, Kind, DisplayName, Tile, Facing, IsLocal, Depleted, Health, MaxHealth);
        }

        // CONTINUOUS MIGRATION (Phase 4 + 5): the LOCAL player renders the predictor's smooth RenderX/RenderY when a
        // predictor is attached (passed in by the outer MmoClient as localOverride, since the predictor lives there);
        // EVERY REMOTE entity (other players / monsters / resources) now renders the continuous playout buffer's
        // Sample(now) — a fixed-delay glide (Phase 5) instead of the raw confirmed position. The AuthoritativeTile
        // (Tile) ALWAYS stays the confirmed tile — targeting/harvest reads it and must NEVER see the
        // predicted/interpolated position (S53 invariant). Only the rendered Position moves. (The vertical hop arc
        // is retired — every remote kind, including the slime, glides flat between tiles.)
        public EntityRenderState ToRenderState(TimeSpan now, LocalRenderState? localOverride = null)
        {
            var position = IsLocal && localOverride.HasValue
                ? localOverride.Value.Position
                : _remoteInterp.Sample(now);

            // HOP-ARC (cosmetic, Phase-8 Option B): a slime hops server-authoritatively as a SPARSE Position jump
            // with Velocity=0; the remote interp lerps the horizontal old->new flat (reads as a slide). Add a
            // vertical parabola SYNCED to that SAME interp (HopArcFactor is the parabola of the bracket the interp
            // is lerping NOW) so the render arcs up-and-over and lands exactly when/where the server hop lands.
            // GATED to non-local MONSTERS (the Velocity=0 sparse-hopper) so players, the local player, and any
            // continuously-moving entity stay flat (HopHeight 0 == today's behaviour). Sample() must run first so
            // HopArcFactor reflects this frame's bracket; never read for the local entity (it renders the override).
            var hopHeight = !IsLocal && Kind == EntityKind.Monster
                ? MonsterHopPeakHeight * _remoteInterp.HopArcFactor
                : 0d;

            // MOVEMENT-ACTIONS (finding #1 fix): a REMOTE entity's replicated jump height rides the SAME playout
            // timeline as its horizontal — _remoteInterp.SampledVerticalOffset (set by the Sample(now) above) — so the
            // arc's apex sits over the XY midpoint instead of leading / stair-stepping vs the smooth glide.
            // MOVEMENT-ACTIONS Phase B2 (carry-forward #1): the LOCAL player now renders its PREDICTED airborne height
            // (the predictor's PredictedVerticalOffset, threaded in via localOverride) — NOT its replicated
            // VerticalOffset. The local avatar's own jump is client-predicted, so its Z is predicted too (one source,
            // no double-count); the replicated VerticalOffset on the local entity is only for OTHER clients' screens.
            // Falls back to the confirmed value only when no predictor/override exists (pre-spawn), where it is 0.
            var renderVerticalOffset = IsLocal
                ? (localOverride?.VerticalOffset ?? VerticalOffset)
                : _remoteInterp.SampledVerticalOffset;

            return new EntityRenderState(NetworkId, CharacterId, Kind, DisplayName, position, Tile, Facing, IsLocal, Depleted, Health, MaxHealth,
                AuthoritativePosition: RenderPosition.FromWorld(Position),
                HopHeight: hopHeight,
                // MOVEMENT-ACTIONS Phase B1 + finding #1: thread the REPLICATED airborne height to the render state. For
                // REMOTE entities it is interpolated on the playout timeline (renderVerticalOffset); for the LOCAL player
                // it is the confirmed value. 0 grounded, so the common case is the unchanged flat render. Distinct from
                // the cosmetic monster HopHeight above (which Phase C retires in favour of this).
                VerticalOffset: renderVerticalOffset);
        }
    }

    private readonly record struct EntityConfirmationDebug(
        bool TileChanged,
        int QueueDepth,
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
