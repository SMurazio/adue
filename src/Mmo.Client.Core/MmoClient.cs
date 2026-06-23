using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Client.Core;

public sealed class MmoClient : IDisposable
{
    // The local player is ALWAYS client-driven (Ultima-Online-style): it routes through the LocalPlayerPredictor
    // (instant prediction + tick-grid stepping + step-seq reconcile), declares the session client-driven to the
    // server (MovementModeMessage) so the server stops auto-pacing, and emits one StepCommitRequest per predicted
    // accepted step so the server FOLLOWS the client's per-step requests (accept/reject). The reject path is the
    // predictor's RecipientStepSeq reconcile (snap on divergence). This is the SOLE local-player render path —
    // the former model-B "cosmetic lead" driver and its F6 render-mode selector were removed (cleanup/remove-model-b).
    // See docs/movement-input-model.md. The only no-predictor case is pre-spawn / interpolation-only (predictor
    // not yet attached), handled by the null-predictor branches below exactly as before.

    public const double RemoteInterpolationCadenceMultiplier = 1.3d;

    // Local playout buffer so the local player's tween isn't starved by snapshot tick-boundary jitter
    // (server confirms tiles on ~50ms tick boundaries). delay=0 starved (q stuck at 1); 0.5x (~75ms)
    // still dipped to 1; 1.0x cadence (~one full step) keeps a spare tile buffered so q holds ~2.
    // This trades a little local input latency for smoothness — the latency-free answer is client
    // prediction, which is deferred by design. Tunable: raise toward RemoteInterpolationCadenceMultiplier
    // (1.3) if it still dips, lower if start-of-move feels laggy.
    public const double LocalInterpolationCadenceMultiplier = 1.0d;
    private const uint PlaceholderSnapshotTtl = 60;

    // UO1: the max accepted steps a single predictor Tick can resolve (mirrors LocalPlayerPredictor.MaxTicksPerCall
    // — the per-call action cap). The per-step commit buffer is sized to this so a laggy multi-step catch-up never
    // overflows it and never drops a commit.
    private const int UoCommitBurstCap = 8;

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

    private uint _moveSequence;

    // COMBAT-S2B: the attack stream's OWN monotonic sequence counter, entirely SEPARATE from _moveSequence (the
    // NET6 lesson — two streams must never share a cursor). Every SendAttack mints the next attack seq off THIS
    // counter only; it never touches _moveSequence, and _moveSequence never touches it. The server dedups attacks
    // on its matching independent _lastAttackSeq cursor.
    private uint _attackSeq;
    // NET1 Stage 1: ring of the last N held inputs (newest last). Each MoveInputMessage repeats the full
    // current state PLUS a window of these prior inputs (as deltas) so a dropped packet's state change is
    // recovered from a later, still-redundant packet. Sized to ~the loss-recovery depth we send (≈8).
    private const int MoveInputRingCapacity = 8;
    private readonly (uint Seq, bool Moving, Direction8 Direction)[] _moveInputRing
        = new (uint, bool, Direction8)[MoveInputRingCapacity];
    private int _moveInputRingCount;
    private int _moveInputRingHead; // index of the oldest entry; (_head + count - 1) % cap is the newest
    // NET2: ring of the last N committed steps (newest last). Each StepCommitBatch repeats the newest commit
    // (head) PLUS a window of these prior committed steps (as deltas) so a dropped commit packet is recovered
    // from a later, still-redundant packet's window instead of a reliable retransmit batch (which the server's
    // cooldown gate would reject all at once → the GodotB speed-up/desync). Sized to ~the loss-recovery depth.
    private const int StepCommitRingCapacity = 8;
    // NET3: each ring entry also carries the commit's AUTHORED server tick (the predictor gate tick the step was
    // banked on). Every batch repeats it as a tick delta off the head so the server applies each commit at its
    // authored time, not the receive tick.
    private readonly (uint Seq, uint Tick, Direction8 Direction)[] _stepCommitRing
        = new (uint, uint, Direction8)[StepCommitRingCapacity];
    private int _stepCommitRingCount;
    private int _stepCommitRingHead; // index of the oldest entry; (_head + count - 1) % cap is the newest

    // NET5: ack-driven re-send of unacked commits (tail-loss recovery). The redundant StepCommitBatch window rides
    // SUBSEQUENT packets, so a mid-stream loss recovers within ~1 packet — but the LAST commit of a movement burst
    // has no following packet to re-carry it. If it drops (and input has stopped) the server's accepted step-seq
    // (`conf`) stays permanently behind the prediction (`pred`): a stuck `lead = pred - conf`. The fix: while
    // lead > 0 AND the ack is OVERDUE (conf has not advanced for a grace > ~RTT + one cadence), re-ship the current
    // ring (the same SendStepCommitBatch — deduped + applied at the authored tick by the server) at ~1 batch /
    // cadence, INCLUDING after movement stops, until conf catches pred and lead drains to 0 with NO snap. In clean
    // play conf advances every RTT, so the grace never elapses and NOT ONE extra packet is sent — the re-send is
    // "the ack is overdue", never "there is something in flight". The bound (one batch per cadence, only while the
    // ack is stalled) keeps it cheap and false-trip-proof.
    //
    // The fallback (RESYNC1): if the re-send has fired ResendFallbackCount times and conf STILL has not advanced
    // (the commit is genuinely undeliverable — heavy/black loss), ForceResync converges the prediction onto the
    // server. The K/T are chosen so a clean <=3% tail drop heals via re-send long before this trips.
    private const double ResendStallGraceMs = 350d;        // ack overdue: > RTT(~200) + one cadence(~150)
    private const int ResendFallbackCount = 6;             // K: re-sent this many times, conf still stuck
    private const double ResendFallbackStuckMs = 1500d;    // T: conf stuck at least this long => ForceResync
    private uint _resendLastConf;                          // last conf seen (detect ack progress)
    private bool _hasResendLastConf;
    private TimeSpan _resendConfStalledSince;              // when conf last advanced (the stall clock)
    private TimeSpan _resendLastSentAt;                    // last re-send wall time (the cadence bound)
    private bool _hasResendLastSentAt;
    private int _resendsSinceConfAdvance;                  // re-sends since conf last moved (K counter)

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

    // UO4: "stop on reversal" (settle-then-go) lever for the predictor modes. When ON, a ~180° flip of the held
    // direction while moving inserts one clean settle beat before resuming the new direction (kills the left-right
    // bounce) instead of reversing mid-step. Held at the client level (like the other movement levers) so a value
    // set before the predictor attaches — or after a respawn re-creates it — is honoured: EnsurePredictor re-seeds
    // it onto the freshly-attached predictor. SetStopOnReversal routes it live (no restart). Default OFF so the
    // current behaviour is unchanged until opted in.
    private bool _stopOnReversal;

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

    // DIAG1: the live movement-debug read-out augmented with the local-player recovery-chain numbers. The base
    // snapshot (sent/confirmed tile, queue depth, cadence, latency, render) comes from the trace; we overlay the
    // predictor's live pred/conf/lead + reconcile-outcome counters and the snapshot `recv/s` rate so the F3 HUD
    // can show which of the three recovery links is stuck under loss. All overlay fields are read-outs — reading
    // them changes nothing. When no predictor is attached (pre-spawn / interpolation-only mode) the predictor
    // fields stay 0 and only recv/s is meaningful.
    public MovementDebugSnapshot MovementDebug
    {
        get
        {
            var snapshot = _movementTrace.Snapshot with { SnapshotsPerSecond = SnapshotsPerSecond };
            if (_predictor is { } predictor)
            {
                var pred = predictor.PredictedStepSeq;
                var conf = predictor.LastReconciledStepSeq;
                snapshot = snapshot with
                {
                    PredictedStepSeq = pred,
                    ConfirmedStepSeq = conf,
                    LeadSteps = pred > conf ? pred - conf : 0u,
                    ReconcileMatched = predictor.ReconcileMatched,
                    ReconcileCorrected = predictor.ReconcileCorrected,
                    ReconcileSnapped = predictor.ReconcileSnapped,
                };
            }

            return snapshot;
        }
    }

    // RENDER-VELOCITY DIAG: a per-frame snapshot of the local predictor's internals for the F5 frame-log, so a
    // live capture can correlate a render-velocity jump (renderX/frameDelta) to its TRIGGER — a predicted-tile
    // re-projection (PredictedX/Y jumping >1 tile in one frame) or a reconcile catch-up (ReconcileCorrected/Snapped
    // ticking up). Null when no predictor is attached (pre-spawn / interpolation-only). Measurement only — it reads
    // the predictor and mutates nothing.
    public readonly record struct LocalPredictorFrameDiag(
        int PredictedX,
        int PredictedY,
        uint PredictedStepSeq,
        uint ReconcileMatched,
        uint ReconcileCorrected,
        uint ReconcileSnapped,
        double CadenceMs);

    public LocalPredictorFrameDiag? LocalPredictorFrameDiagnostics =>
        _predictor is { } predictor
            ? new LocalPredictorFrameDiag(
                predictor.PredictedTile.X,
                predictor.PredictedTile.Y,
                predictor.PredictedStepSeq,
                predictor.ReconcileMatched,
                predictor.ReconcileCorrected,
                predictor.ReconcileSnapped,
                predictor.CadenceMs)
            : null;

    // S106: the local predictor's live cadence (ms), for tests asserting a live MovementSpeedChanged retunes the
    // predictor (not just the interpolator). Null when no predictor is attached.
    internal double? LocalPredictorCadenceMsForTests => _predictor?.CadenceMs;

    // DIAG1: zeroes the local predictor's reconcile-outcome tallies (Matched / Corrected / Snapped) so the human
    // can reset them just before a loss burst and read fresh counts in the F3 read-out. No-op (safe) when no
    // predictor is attached. Measurement only — touches no prediction/reconcile state.
    public void ResetReconcileCounters() => _predictor?.ResetReconcileCounters();

    // RESYNC1: manual Force Resync — snaps the local prediction (tile, step-seq, render) onto the last
    // server-confirmed position and clears any in-flight/banked-but-unconfirmed state so nothing stale replays
    // forward. The reusable resync primitive the auto-tiers (UO5 tier-2, NET4 tier-3) will call; here it is wired
    // to the F6 "Force Resync" button and the Alt+R hotkey. USER-TRIGGERED only — it changes nothing unless
    // called, so the normal Tick/Reconcile movement path is untouched. No-op (safe) when no predictor is attached
    // or the prediction is already in sync. Pass-through mirrors ResetReconcileCounters (DIAG1).
    public void ForceResync() => _predictor?.ForceResync();

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

        // Advance the local-player driver AFTER draining inbound messages, so a snapshot that arrived this poll
        // re-bases the prediction before we project the render to "now". The local player ALWAYS runs through the
        // predictor (client-driven UO mode is the sole render path) — tick the predictor AND emit the accepted steps
        // this call (the multi-step catch-up loop can resolve up to MaxTicksPerCall=8 steps on a laggy frame). The
        // server FOLLOWS these: it advances the entity only on accepted commits (the held-intent pacer is disabled
        // for this session by the MovementModeMessage). NET2: each accepted step mints a FRESH ++_moveSequence on the
        // SHARED move cursor (the same cursor MoveIntent/MoveInput use) and is recorded in the commit ring; then ONE
        // redundant-unreliable StepCommitBatch ships the newest step plus a window of prior committed steps, so a
        // dropped commit recovers from a later packet's window instead of a reliable retransmit batch. Pre-spawn
        // (no predictor attached yet) this is a no-op.
        if (_predictor is { } predictor)
        {
            Span<Direction8> accepted = stackalloc Direction8[UoCommitBurstCap];
            Span<long> acceptedTicks = stackalloc long[UoCommitBurstCap];
            predictor.Tick(now, accepted, acceptedTicks, out var acceptedCount);
            var emit = Math.Min(acceptedCount, accepted.Length);
            for (var i = 0; i < emit; i++)
            {
                // NET3: stamp the commit with the SAME gate tick the predictor banked the step on (acceptedTicks)
                // so the server replays it at its authored time. The tick is a non-negative server tick here (the
                // gate never fires before the calibrated frame); clamp at 0 defensively before the uint cast.
                RecordStepCommit(++_moveSequence, (uint)Math.Max(0, acceptedTicks[i]), accepted[i]);
            }

            if (emit > 0)
            {
                SendStepCommitBatch();
                // NET5: a fresh batch just covered this cadence — restart the re-send timer so the tail-recovery
                // re-send doesn't pile a second packet on top of it.
                _resendLastSentAt = now;
                _hasResendLastSentAt = true;
            }

            // NET5: drive the ack-driven tail-recovery re-send (no-op unless a commit is genuinely stranded).
            DriveAckDrivenResend(predictor, now, emittedFreshThisPoll: emit > 0);
        }
    }

    // NET5: ack-driven re-send of unacked commits (tail-loss recovery) + the bounded ForceResync fallback. Called
    // every Poll AFTER the fresh-commit emission. While the prediction LEADS the server's
    // learned ack (lead = pred - conf > 0) AND that ack is OVERDUE (conf has not advanced for ResendStallGraceMs,
    // i.e. longer than a normal RTT round-trip), re-ship the current commit ring at most once per cadence — the
    // existing redundant-unreliable SendStepCommitBatch, which the server dedups (cursor) and applies at the
    // authored tick, so a re-delivered tail commit just lands and `conf` catches `pred` with NO snap. The re-send
    // continues INCLUDING after movement stops (the stranded tail's defining case) until lead == 0.
    //
    // Clean play sends NOTHING extra: conf advances every RTT, so the stall grace never elapses and the re-send
    // never fires — it triggers only when an expected ack is genuinely overdue. The fallback: after
    // ResendFallbackCount re-sends with conf STILL stalled (>= ResendFallbackStuckMs), the commit is
    // undeliverable; ForceResync (RESYNC1) converges. K/T are tuned so a clean <=3% tail drop heals via re-send
    // first and never reaches the fallback.
    private void DriveAckDrivenResend(LocalPlayerPredictor predictor, TimeSpan now, bool emittedFreshThisPoll)
    {
        // NET5b: the decision rule lives in ONE pure place (AckDrivenResendPolicy.Decide) that the headless tests
        // also call. This wrapper just packs the carried _resend* state into the helper's state struct, asks for the
        // decision, writes the (possibly advanced) state back, and performs the side effects (SendStepCommitBatch /
        // predictor.ForceResync). Behaviour is identical to the previous inline implementation.
        var state = new AckResendState
        {
            LastConf = _resendLastConf,
            HasLastConf = _hasResendLastConf,
            ConfStalledSinceMs = _resendConfStalledSince.TotalMilliseconds,
            LastSentAtMs = _resendLastSentAt.TotalMilliseconds,
            HasLastSentAt = _hasResendLastSentAt,
            ResendsSinceConfAdvance = _resendsSinceConfAdvance,
        };

        var config = new AckResendConfig(
            StallGraceMs: ResendStallGraceMs,
            FallbackCount: ResendFallbackCount,
            FallbackStuckMs: ResendFallbackStuckMs,
            CadenceMs: predictor.CadenceMs);

        var decision = AckDrivenResendPolicy.Decide(
            now.TotalMilliseconds, predictor.PredictedStepSeq, predictor.LastReconciledStepSeq,
            emittedFreshThisPoll, state, config);

        var next = decision.State;
        _resendLastConf = next.LastConf;
        _hasResendLastConf = next.HasLastConf;
        _resendConfStalledSince = TimeSpan.FromMilliseconds(next.ConfStalledSinceMs);
        _resendLastSentAt = TimeSpan.FromMilliseconds(next.LastSentAtMs);
        _hasResendLastSentAt = next.HasLastSentAt;
        _resendsSinceConfAdvance = next.ResendsSinceConfAdvance;

        if (decision.SendBatch)
        {
            SendStepCommitBatch();
        }

        if (decision.ForceResync)
        {
            predictor.ForceResync();
        }
    }

    // Whether local-player movement prediction is active (S53). Disabling it (e.g. for an A/B feel check)
    // reverts the local player to the pre-S53 confirmed-tile interpolation path. Default on.
    public bool PredictionEnabled
    {
        get => _predictionEnabled;
        set => _predictionEnabled = value;
    }

    // UO1: declares this session's movement model to the server (true = client-driven UO mode, false = server-paced).
    // ReliableOrdered — a lost flag would desync the server's pacing decision (double-step or stall). No-op safe
    // before connect (Send routes to the test sink / queues). Re-sent on (re)login/respawn via EnsurePredictor.
    private void SendMovementModeSignal(bool clientDriven)
    {
        Send(new MovementModeMessage(clientDriven), DeliveryMethod.ReliableOrdered);
    }

    // UO4: whether the predictor settles-then-goes on a ~180° reversal (F6 "Stop on reversal"). Reflects the value
    // last set (seeded into the F6 toggle on panel open). Read by the predictor (the sole local-player render path).
    public bool StopOnReversal => _stopOnReversal;

    // UO4: live-toggles the predictor's stop-on-reversal (settle-then-go) behaviour. Routed to the active predictor
    // immediately (no restart); stored at the client level so a value set before attach / after a respawn is
    // re-applied by EnsurePredictor. No-op safe when no predictor yet.
    public void SetStopOnReversal(bool enabled)
    {
        _stopOnReversal = enabled;
        _predictor?.SetStopOnReversal(enabled);
    }

    // The predicted local-player tile (S53), or null when prediction is inactive. This is the snappy,
    // ahead-of-confirmation position used for MOVEMENT rendering only. Harvest/interact targeting must use
    // LocalTile (the server-confirmed tile) instead — prediction must never authorize an interaction the
    // server will reject from its authoritative position.
    public TileCoord? PredictedLocalTile => _predictor?.PredictedTile;

    // S79 diagnostic accessor: the local player's predicted tile when prediction is active, else the
    // confirmed/server tile. Unlike PredictedLocalTile (which is null whenever no predictor is attached),
    // this always yields a usable tile once the local entity exists, so the F5 "Prediction tiles" overlay
    // can paint the predicted marker without special-casing the pre-prediction path — when prediction is
    // off/not yet attached the predicted marker simply coincides with the confirmed one.
    public TileCoord? LocalPredictedTile => _predictor?.PredictedTile ?? LocalTile;

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

    // Sends a held-direction movement intent. NET1 Stage 1: the wire delivery is now an UNRELIABLE,
    // REDUNDANT MoveInputMessage instead of a reliable-ordered MoveIntentMessage — the caller drives it at a
    // fixed ~20 Hz while moving plus a short Moving=false tail after stop (see MmoClientRoot.SendHeldMovement).
    // Each call mints a fresh sequence, records it in the ring, and ships the FULL current state plus a window
    // of the last few prior inputs (deltas) so a dropped packet is superseded by the next and a dropped state
    // change is recovered from a later packet's window. The server dedupes by sequence; reliability comes from
    // redundancy, not retransmission, so a lost packet no longer head-of-line-stalls (freeze-then-jump gone).
    // Direction is ignored by the server when moving is false. See docs/movement-input-model.md.
    public uint SendMoveIntent(bool moving, Direction8 direction)
    {
        var sequence = ++_moveSequence;
        RecordMoveInput(sequence, moving, direction);
        Send(new MoveInputMessage(sequence, moving, direction, BuildMoveInputWindow(sequence)), DeliveryMethod.Unreliable);
        _movementTrace.MoveSent(sequence, moving, direction);
        // S53: the held intent we just sent the server is exactly what the local predictor mirrors. Feed it
        // so the first step on keydown / the stop on keyup is predicted with no round-trip wait. The
        // predictor steps forward on each Poll(now); SetIntent only records the held state + arms the first
        // step. Created lazily here once the zone + local entity + cadence are all available.
        EnsurePredictor();
        // The local player ALWAYS routes through the predictor (client-driven UO mode is the sole render path):
        // feed it the held intent so the first step on keydown / the stop on keyup is predicted with no round-trip
        // wait. No-op pre-spawn (predictor not yet attached).
        _predictor?.SetIntent(moving, direction, _currentTime);

        return sequence;
    }

    // NET1 Stage 1: push the just-sent input into the redundancy ring (newest last, dropping the oldest once
    // full). The ring feeds the window of every subsequent MoveInputMessage.
    private void RecordMoveInput(uint sequence, bool moving, Direction8 direction)
    {
        if (_moveInputRingCount < MoveInputRingCapacity)
        {
            var slot = (_moveInputRingHead + _moveInputRingCount) % MoveInputRingCapacity;
            _moveInputRing[slot] = (sequence, moving, direction);
            _moveInputRingCount++;
        }
        else
        {
            _moveInputRing[_moveInputRingHead] = (sequence, moving, direction);
            _moveInputRingHead = (_moveInputRingHead + 1) % MoveInputRingCapacity;
        }
    }

    // NET1 Stage 1: build the redundancy window for a packet whose head is headSeq — the prior inputs still in
    // the ring (every entry except the head itself), encoded as deltas (headSeq - entrySeq). Newest-first so a
    // truncated read still recovers the most recent changes. Returns empty when the ring holds only the head.
    private IReadOnlyList<MoveInputWindowEntry> BuildMoveInputWindow(uint headSeq)
    {
        if (_moveInputRingCount <= 1)
        {
            return Array.Empty<MoveInputWindowEntry>();
        }

        var window = new List<MoveInputWindowEntry>(_moveInputRingCount - 1);
        // Walk newest-to-oldest, skipping the head entry.
        for (var i = _moveInputRingCount - 1; i >= 0; i--)
        {
            var slot = (_moveInputRingHead + i) % MoveInputRingCapacity;
            var entry = _moveInputRing[slot];
            if (entry.Seq == headSeq)
            {
                continue;
            }

            var delta = headSeq - entry.Seq;
            if (delta == 0 || delta > byte.MaxValue)
            {
                continue;
            }

            window.Add(new MoveInputWindowEntry((byte)delta, entry.Moving, entry.Direction));
        }

        return window;
    }

    // NET2/NET3: push a just-committed step (seq + AUTHORED tick + direction) into the redundancy ring (newest
    // last, dropping the oldest once full). The ring feeds the window of every subsequent StepCommitBatch.
    private void RecordStepCommit(uint sequence, uint authoredTick, Direction8 direction)
    {
        if (_stepCommitRingCount < StepCommitRingCapacity)
        {
            var slot = (_stepCommitRingHead + _stepCommitRingCount) % StepCommitRingCapacity;
            _stepCommitRing[slot] = (sequence, authoredTick, direction);
            _stepCommitRingCount++;
        }
        else
        {
            _stepCommitRing[_stepCommitRingHead] = (sequence, authoredTick, direction);
            _stepCommitRingHead = (_stepCommitRingHead + 1) % StepCommitRingCapacity;
        }
    }

    // NET2: ship the current commit ring as ONE redundant-unreliable StepCommitBatch. The head is the NEWEST
    // committed step in the ring; the window carries the prior committed steps as deltas (headSeq - entrySeq),
    // newest-first. The server dedupes by sequence and applies each fresh commit through the EXISTING
    // TryCommitStep. A dropped batch is recovered from the next batch's window (no reliable retransmit batch
    // that the cooldown gate would reject all at once). No-op if nothing has been committed yet.
    private void SendStepCommitBatch()
    {
        if (_stepCommitRingCount == 0)
        {
            return;
        }

        var headSlot = (_stepCommitRingHead + _stepCommitRingCount - 1) % StepCommitRingCapacity;
        var head = _stepCommitRing[headSlot];
        Send(
            new StepCommitBatchMessage(head.Seq, head.Tick, head.Direction, BuildStepCommitWindow(head.Seq, head.Tick)),
            DeliveryMethod.Unreliable);
    }

    // NET2/NET3: build the redundancy window for a batch whose head is (headSeq, headTick) — the prior committed
    // steps still in the ring (every entry except the head), encoded as a seq delta (headSeq - entrySeq) AND a tick
    // delta (headTick - entryTick) off the head. Newest-first so a truncated read still recovers the most recent
    // commits. An entry whose authored tick is NOT strictly older than the head's (tickDelta <= 0 — should never
    // happen since seq and authored tick both increase monotonically, but a clock nudge could tie them) is dropped
    // so the server never reads a non-positive tick delta. Returns empty when the ring holds only the head.
    private IReadOnlyList<StepCommitWindowEntry> BuildStepCommitWindow(uint headSeq, uint headTick)
    {
        if (_stepCommitRingCount <= 1)
        {
            return Array.Empty<StepCommitWindowEntry>();
        }

        var window = new List<StepCommitWindowEntry>(_stepCommitRingCount - 1);
        // Walk newest-to-oldest, skipping the head entry.
        for (var i = _stepCommitRingCount - 1; i >= 0; i--)
        {
            var slot = (_stepCommitRingHead + i) % StepCommitRingCapacity;
            var entry = _stepCommitRing[slot];
            if (entry.Seq == headSeq)
            {
                continue;
            }

            var delta = headSeq - entry.Seq;
            if (delta == 0 || delta > byte.MaxValue)
            {
                continue;
            }

            // The authored tick must be strictly older than the head's (the head is the newest step). A tie or an
            // inversion (a calibration nudge could in theory equalise two ticks) would make a 0/underflowing tick
            // delta — drop that entry rather than ship an ambiguous authored tick.
            if (entry.Tick >= headTick)
            {
                continue;
            }

            window.Add(new StepCommitWindowEntry((byte)delta, headTick - entry.Tick, entry.Direction));
        }

        return window;
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

        _predictor = local.AttachPredictor(ResolveCadence(local.StepCooldownMs), IsWalkableForPrediction, ResolveTickMs());
        // The local player is ALWAYS client-driven (the sole render path). A freshly-attached predictor defaults to
        // server-paced, so re-declare the client-driven flag so its release/at-rest reconcile holds for banked
        // commits, in step with the MovementModeMessage re-sent below.
        _predictor.SetClientDriven(true);
        // UO4: re-seed the stop-on-reversal lever onto the freshly-attached (or respawn-recreated) predictor so a
        // value set before attach / after a respawn is honoured.
        _predictor.SetStopOnReversal(_stopOnReversal);
        // UO1: the local entity just (re)attached — a fresh login / respawn / AOI re-entry. (Re-)declare the
        // client-driven mode to the server so a flag lost across that lifecycle event can't leave the server
        // auto-pacing AND the client committing (double-stepping). ReliableOrdered; harmless if redundant.
        SendMovementModeSignal(clientDriven: true);
    }

    // S81: resolves the server tick interval in ms (1000 / TickRate) — the unit of the predictor's tick-grid
    // gate. Falls back to 50 ms (20 Hz, the ServerOptions default) until ServerHello lands. The predictor maps
    // wall-clock to serverTick at this granularity and derives its integer step/turn tick counts from it.
    private double ResolveTickMs()
    {
        var tickRate = Server?.TickRate ?? 20;
        return tickRate > 0 ? 1000d / tickRate : 50d;
    }

    // Drops the local entity reference and its predictor (local despawn / AOI exit / logout). Nulling the
    // predictor lets EnsurePredictor re-attach a fresh one (anchored to the new confirmed tile) when the
    // local entity respawns, so a stale predictor never drives a removed entity's interpolator (S47b guard).
    private void ClearLocalEntity()
    {
        LocalNetworkId = null;
        _predictor = null;
        // COMBAT-S1: drop the cached vitals so a stale prior-session value can't briefly feed the HUD on reconnect
        // (the next login always re-sends PlayerStats). Reset alongside the other local-entity state.
        LocalStats = null;
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

        // SWING-COMMIT-FIX: stamp the swing with an AUTHORED TICK — the predictor's monotonic-clamped estimate of the
        // current server tick (the SAME EstimateServerTick the NET3 step-commit path uses). This is the tick the
        // predictor will root its OWN movement on, and the server will root the attacker's movement at this SAME
        // authored tick (clamped to a window around its receive tick) instead of its receive tick — so under latency
        // the two root windows are identical and the predictor never steps where the server rejects (the
        // swing-then-move rubberband the receive-tick anchor caused). No predictor (pre-spawn) => authored tick 0;
        // the server still roots authoritatively (clamped up into its past window) and there is no prediction to
        // disagree with it.
        var authoredTick = _predictor is { } p ? (uint)Math.Max(0, p.EstimateServerTick(_currentTime)) : 0u;
        Send(new AttackMessage(sequence, AttackKind.MeleeCone, aimAngle, authoredTick), DeliveryMethod.ReliableOrdered);

        // COMBAT-TUNING (radial cooldown): record this swing's send time + the cooldown duration in effect now (the
        // replicated attackCooldownMs, falling back to the shared default before the first snapshot) so the HUD's
        // radial cooldown indicator on the LMB slot can sweep from now to now+cooldown. Local HUD estimate only —
        // the server stays authoritative for whether the attack actually resolved.
        _lastAttackSentAt = _currentTime;
        _lastAttackCooldownMs = CombatTuning?.AttackCooldownMs ?? DefaultAttackCooldownMs;

        // SWING-COMMIT (predictor mirror): the local player just committed a swing, so root the predicted movement
        // IDENTICALLY to the server's WorldEntity.ApplyAttackMovementRoot (driven from GameServer.HandleAttack). We
        // compute rootTicks from the SAME Mmo.Shared.Domain.CombatTuning source the server uses, off the predictor's
        // tick interval AND the LIVE replicated rootMs (combat.rootMs) — so steady-state both sides root for the
        // identical window. Falls back to the shared default rootMs before the first snapshot. Anchored on the SAME
        // authoredTick we put on the wire (not a re-estimate). No predictor => nothing to root; the server still roots.
        if (_predictor is { } predictor)
        {
            var rootMs = CombatTuning?.RootMs ?? Mmo.Shared.Domain.CombatTuning.MovementRootMs;
            predictor.ApplyAttackMovementRootAt(
                authoredTick,
                Mmo.Shared.Domain.CombatTuning.RootTicksFromTickMs(predictor.TickMs, rootMs));
        }

        return sequence;
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
        Server = new ServerInfo(hello.ServerName, hello.ProtocolVersion, hello.TickRate, hello.StepCooldownMs, hello.InterestRadiusTiles);
        RefreshInterpolatorCadence();
        // S81: adopt the advertised tick interval if the predictor is already attached (ServerHello can arrive
        // after the local entity spawned in a re-hello). New predictors seed it via EnsurePredictor.
        _entities.TryGetValue(LocalNetworkId ?? 0, out var local);
        local?.SetPredictorTickMs(ResolveTickMs());
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
                    state.Tile,
                    state.Facing);
            }

            var confirmation = entity.ApplySnapshot(state.Tile, state.Facing, _currentTime, sequence, _lastRecipientStepSeq, serverTick, state.Depleted, state.Health, state.MaxHealth);
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

        // S84: the local player must reconcile on EVERY snapshot, even when it is delta'd out of the entity
        // list. The server delta-compresses (re-sends an entity only while its StateRevision changes), so an
        // IDLE local player is absent from the payload — but the header still rides RecipientStepSeq + ServerTick
        // (S76) for exactly this. Without re-running calibrate+reconcile here, any over-prediction left by a turn
        // spam latches at rest and never closes (the "static, gap won't close" symptom). We re-apply the entity's
        // CURRENT (last-known, unchanged) Tile/Facing — NOT a fabricated move; the confirmed position is genuinely
        // unchanged (that's why it was delta'd out) — so CalibrateToServerTick keeps tracking the server clock and
        // Reconcile re-anchors the prediction to truth while idle (converging down to the confirmed tile at rest).
        // Only the delta'd-out case is affected; while moving the local player is in every snapshot and takes the
        // in-snapshot path above unchanged.
        if (LocalNetworkId is { } localId
            && !_snapshotVisibleScratch.Contains(localId)
            && _entities.TryGetValue(localId, out var localEntity))
        {
            localEntity.ApplySnapshot(
                localEntity.Tile,
                localEntity.Facing,
                _currentTime,
                sequence,
                _lastRecipientStepSeq,
                serverTick,
                localEntity.Depleted,
                localEntity.Health,
                localEntity.MaxHealth);
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

            // EntitySpawn carries no Depleted/HP bits (those ride the AOI snapshot), so preserve whatever the
            // last snapshot set rather than resetting a known-depleted node to available or zeroing known HP.
            existing.ApplySnapshot(tile, facing, _currentTime, _lastAppliedSnapshotSequence ?? 0, _lastRecipientStepSeq, serverTick: null, existing.Depleted, existing.Health, existing.MaxHealth);
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

        // COMBAT-S2A: public HP replicated on the AOI snapshot, threaded to the overhead red bar. 0/0 for
        // entities without vitals (resources). Carried alongside Depleted (snapshot-driven, not interpolated).
        public ushort Health { get; private set; }

        public ushort MaxHealth { get; private set; }

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

        public EntityConfirmationDebug ApplySnapshot(TileCoord tile, Direction8 facing, TimeSpan receivedAt, uint snapshotSequence, uint recipientStepSeq, uint? serverTick, bool depleted = false, ushort health = 0, ushort maxHealth = 0)
        {
            var previousTile = Tile;
            // Tile/Facing always track the SERVER-CONFIRMED state: LocalTile (harvest/click targeting) reads
            // it, and the renderer's AuthoritativeTile uses it. Prediction only affects the interpolated
            // render position, never this authoritative tile.
            Tile = tile;
            Depleted = depleted;
            // COMBAT-S2A: adopt the replicated public HP (snapshot-driven, like Depleted). Preserving callers
            // (the delta'd-out local re-apply and EntitySpawn) pass the current values so HP isn't reset to 0.
            Health = health;
            MaxHealth = maxHealth;
            LastSeenSnapshotSequence = snapshotSequence;
            if (_predictor is not null)
            {
                // S81: re-anchor the predictor's wall-clock -> serverTick calibration to this snapshot's
                // authoritative tick (smoothed/clamped internally so jitter can't jump the grid) BEFORE
                // reconciling, so the gate runs on the server's true tick phase. Only when a real snapshot tick
                // is available (the EntitySpawn path passes null).
                if (serverTick.HasValue)
                {
                    _predictor.CalibrateToServerTick(serverTick.Value, receivedAt);
                }

                // Local predicted entity: re-base the prediction off the confirmed tile, matched by the
                // recipient-scoped step sequence (S77) so a benign trailing/old-direction confirm is recognised
                // by the step it confirms instead of being yanked backward. The predictor owns its own
                // present-time render tween (the buffered interpolator is bypassed here). Facing follows the
                // prediction while moving, else the confirmed facing.
                _predictor.Reconcile(tile, recipientStepSeq, receivedAt);
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
        public LocalPlayerPredictor AttachPredictor(double cadenceMs, Func<TileCoord, bool> isWalkable, double tickMs)
        {
            _predictor ??= new LocalPlayerPredictor(Tile, Facing, cadenceMs, isWalkable, tickMs);
            return _predictor;
        }

        // S81: live-sets the predictor's server tick interval (ServerHello TickRate). No-op if no predictor.
        public void SetPredictorTickMs(double tickMs)
        {
            _predictor?.SetTickMs(tickMs);
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
            return new ReplicatedEntity(NetworkId, CharacterId, Kind, DisplayName, Tile, Facing, IsLocal, Depleted, Health, MaxHealth);
        }

        public EntityRenderState ToRenderState(TimeSpan now)
        {
            // S53: the local predicted player renders from the predictor's OWN present-time tween (snappy, no
            // playout delay). Everything else (remote players, resources) keeps the buffered interpolator.
            // Render-source selection only — Tile stays confirmed.
            RenderPosition position;
            Direction8 facing;
            if (_predictor is not null)
            {
                position = _predictor.Sample(now);
                // S59: render the predictor's LIVE facing for the local entity, so a predicted turn (no tile
                // move) rotates the avatar immediately instead of waiting for the next snapshot to sync Facing.
                facing = _predictor.Facing;
            }
            else
            {
                position = _interpolator.Sample(now);
                facing = Facing;
            }
            return new EntityRenderState(NetworkId, CharacterId, Kind, DisplayName, position, Tile, facing, IsLocal, Depleted, Health, MaxHealth);
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
