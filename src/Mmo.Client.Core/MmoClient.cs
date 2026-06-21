using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Client.Core;

// RENDER1: which local-player render model drives the avatar (live F6). Trimmed to the two keepers:
// UoClientDriven is model A (LocalPlayerPredictor) declared client-driven — instant prediction with the server
// FOLLOWING the client's per-step commits — and is the DEFAULT. CosmeticLead is model B (LocalPlayerCosmetic with
// the forward lead ON): a smooth glide with no banked tile, best at low latency. The earlier standalone Predicted
// (predictor, server-paced) and AcceptDeny (cosmetic driver, lead off) modes were dropped — Predicted was a worse
// UO (snaps at latency) and AcceptDeny was the rejected no-prediction mode. The predictor + cosmetic drivers are
// kept; only those two modes + their UI/routing went away. See MmoClient.RenderMode and docs/movement-input-model.md.
public enum MovementRenderMode
{
    // CosmeticLead is model B (LocalPlayerCosmetic, forward lead ON): smooth glide, no banked tile.
    CosmeticLead = 0,
    // UoClientDriven (DEFAULT): client-driven (Ultima-Online-style). Routes the local player through the
    // LocalPlayerPredictor (instant prediction + tick-grid stepping + step-seq reconcile), AND declares the session
    // client-driven to the server (MovementModeMessage) so the server stops auto-pacing, and emits one
    // StepCommitRequest per predicted accepted step so the server FOLLOWS the client's per-step requests
    // (accept/reject). The reject path is the predictor's existing RecipientStepSeq reconcile (snap on divergence).
    UoClientDriven = 1,
}

public sealed class MmoClient : IDisposable
{
    // RENDER1: which render modes drive the local player through the LocalPlayerPredictor (model A). Only
    // UoClientDriven does now; CosmeticLead (model B) rides the cosmetic driver. The four predictor routing sites
    // (Poll Tick, SendMoveIntent SetIntent, EnsurePredictor/ReanchorLocalDriver, and the ApplySnapshot/ToRenderState
    // render-source selection) all gate on this predicate, so the trim to two modes needed no routing-branch
    // rewiring — only narrowing the predicate. UoClientDriven layers the per-step commit emission + the mode-signal
    // on TOP of the predictor path.
    internal static bool UsesPredictor(MovementRenderMode mode)
        => mode is MovementRenderMode.UoClientDriven;

    public const double RemoteInterpolationCadenceMultiplier = 1.3d;

    // Local playout buffer so the local player's tween isn't starved by snapshot tick-boundary jitter
    // (server confirms tiles on ~50ms tick boundaries). delay=0 starved (q stuck at 1); 0.5x (~75ms)
    // still dipped to 1; 1.0x cadence (~one full step) keeps a spare tile buffered so q holds ~2.
    // This trades a little local input latency for smoothness — the latency-free answer is client
    // prediction, which is deferred by design. Tunable: raise toward RemoteInterpolationCadenceMultiplier
    // (1.3) if it still dips, lower if start-of-move feels laggy.
    public const double LocalInterpolationCadenceMultiplier = 1.0d;
    private const uint PlaceholderSnapshotTtl = 60;

    // S103: how many snapshots to wait after a commit send before declaring a reject when the server has shown NO
    // step activity (RecipientStepSeq never advanced — a pure reject where the server changed nothing). The normal
    // reject is detected the instant the server processes a step that isn't ours (RecipientStepSeq advances); this
    // bound only covers the do-nothing reject. ~8 snapshots ≈ a few hundred ms at 20 Hz — long enough that a
    // legitimate accept (which advances the tile + RecipientStepSeq) is never misread as a reject under LAN jitter.
    private const int CommitRejectGraceSnapshots = 8;

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
    private uint _moveSequence;
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
    // banked on, or — for a model-B release commit — the present estimated server tick). Every batch repeats it as
    // a tick delta off the head so the server applies each commit at its authored time, not the receive tick.
    private readonly (uint Seq, uint Tick, Direction8 Direction)[] _stepCommitRing
        = new (uint, uint, Direction8)[StepCommitRingCapacity];
    private int _stepCommitRingCount;
    private int _stepCommitRingHead; // index of the oldest entry; (_head + count - 1) % cap is the newest
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
    // RENDER1: the active local-player render model. UoClientDriven (model A, declared client-driven) is now the
    // DEFAULT — the client boots into UO mode; CosmeticLead (model B) is the F6 alternative. See RenderMode /
    // SetMovementRenderMode.
    private MovementRenderMode _renderMode = MovementRenderMode.UoClientDriven;

    // S94: the live-tunable cosmetic lead distance (tiles) for model B, [0.0, 1.0], default 1.0 (= current model B
    // byte-for-byte). Held at the client level so a value set before the local entity attaches — or after a
    // respawn re-creates the cosmetic driver — is honoured: AttachCosmetic seeds the freshly-attached driver from
    // this. SetCosmeticLeadTiles routes it live to the active driver (no restart). Clamped on set.
    private double _cosmeticLeadTiles = 1.0d;

    // S102: model B's release SNAP-to-confirmed (S91) toggle, default true (= current behavior). Held at the client
    // level like _cosmeticLeadTiles so a value set before the local entity attaches — or after a respawn re-creates
    // the cosmetic driver — is honoured (AttachCosmetic seeds the fresh driver from this). SetSnapOnRelease routes it
    // live to the active driver (no restart). Only model B's release reads it.
    private bool _snapOnRelease = true;

    // S103 commit-step on release. Client-level levers (re-seeded onto the cosmetic driver on attach, like the
    // other lead settings): whether a release past the threshold commits the near-done step (default ON) and the
    // progress threshold (default 0.7).
    private bool _commitStepOnRelease = true;
    private double _commitStepThreshold = 0.55d;

    // UO4: "stop on reversal" (settle-then-go) lever for the predictor modes. When ON, a ~180° flip of the held
    // direction while moving inserts one clean settle beat before resuming the new direction (kills the left-right
    // bounce) instead of reversing mid-step. Held at the client level (like the other movement levers) so a value
    // set before the predictor attaches — or after a respawn re-creates it — is honoured: EnsurePredictor re-seeds
    // it onto the freshly-attached predictor. SetStopOnReversal routes it live (no restart). Default OFF so the
    // current behaviour is unchanged until opted in.
    private bool _stopOnReversal;

    // S103: the in-flight commit's reconciliation state, or null when none is pending. Tracks the committed target
    // tile, the RecipientStepSeq at send time (the server's accepted-step count then), and a bounded snapshot grace
    // counter. The cosmetic driver handles the accept render (confirmed tile reaches target) and a diverging confirm
    // itself; THIS owns the reject-with-no-tile-change case: once the server demonstrably processed the commit
    // (RecipientStepSeq advanced past the base, OR the grace elapsed) without the tile reaching the target, snap
    // back. Getting this grace/ordering right is what stops a not-yet-arrived accept being misread as a reject.
    private PendingCommit? _pendingCommit;

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

    // S76: the recipient-scoped step sequence from the latest snapshot header (server's count of our own
    // accepted tile moves). Exposed read-only for diagnostics / S77's reconcile; not yet consumed by the
    // predictor this stage.
    public uint LastRecipientStepSeq => _lastRecipientStepSeq;

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
        // re-bases the prediction (A) / advances the confirmed tile (B) before we project the render to "now".
        if (!UsesPredictor(_renderMode))
        {
            // RENDER1: CosmeticLead (model B) — tick the cosmetic driver. With the forward lead it glides the render
            // early toward the held direction.
            if (LocalNetworkId is { } id && _entities.TryGetValue(id, out var local))
            {
                local.TickCosmetic(now);
            }
        }
        else
        {
            // RENDER1: UoClientDriven is the only predictor mode now — tick the predictor AND emit the accepted
            // steps this call (the multi-step catch-up loop can resolve up to MaxTicksPerCall=8 steps on a laggy
            // frame). The server FOLLOWS these: it advances the entity only on accepted commits (the held-intent
            // pacer is disabled for this session by the MovementModeMessage). NET2: each accepted step mints a FRESH
            // ++_moveSequence on the SHARED move
            // cursor (the same cursor MoveIntent/MoveInput use) and is recorded in the commit ring; then ONE
            // redundant-unreliable StepCommitBatch ships the newest step plus a window of prior committed steps,
            // so a dropped commit recovers from a later packet's window instead of a reliable retransmit batch.
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
                }
            }
        }
    }

    // Whether local-player movement prediction is active (S53). Disabling it (e.g. for an A/B feel check)
    // reverts the local player to the pre-S53 confirmed-tile interpolation path. Default on.
    public bool PredictionEnabled
    {
        get => _predictionEnabled;
        set => _predictionEnabled = value;
    }

    // RENDER1: which local-player render model drives the avatar. UoClientDriven (model A, the default) =
    // LocalPlayerPredictor (PredictedTile ahead + Reconcile) declared client-driven. CosmeticLead (model B, F6
    // alternative) = LocalPlayerCosmetic (no banked tile; the render glides early on input and cuts to the confirmed
    // tile on a disagreeing ack). The mode routes the local-player driver at SendMoveIntent, the per-Poll Tick,
    // ApplySnapshot, and the ToRenderState render-source selection. See docs/movement-input-model.md.
    public MovementRenderMode RenderMode
    {
        get => _renderMode;
        set => SetMovementRenderMode(value);
    }

    // RENDER1: flips the local-player render model LIVE (F6 — no restart). Re-anchors the newly-active driver from
    // the local entity's current confirmed tile + current render position so the avatar does NOT pop on the
    // switch, then routes all four touch points to the new mode. UO->Cosmetic seeds the cosmetic driver where the
    // predictor was showing; Cosmetic->UO re-anchors the predictor (its PredictedTile re-seeds onto the confirmed
    // tile, its render tween onto the current render position) so there is no jump either way.
    public void SetMovementRenderMode(MovementRenderMode mode)
    {
        if (mode == _renderMode)
        {
            return;
        }

        // UO1: entering or leaving UoClientDriven flips the server-side client-driven flag. Send the one-bit signal
        // BEFORE re-anchoring so the server stops/starts auto-pacing in step with the client owning/releasing the
        // per-step commit stream. Only emitted on an actual transition into/out of the mode (no-op for B<->A etc.).
        var wasUo = _renderMode == MovementRenderMode.UoClientDriven;
        var nowUo = mode == MovementRenderMode.UoClientDriven;
        _renderMode = mode;
        if (nowUo && !wasUo)
        {
            SendMovementModeSignal(clientDriven: true);
        }
        else if (wasUo && !nowUo)
        {
            SendMovementModeSignal(clientDriven: false);
        }

        // UO3: tell the predictor whether it is now in the client-driven (per-step-commit) mode BEFORE re-anchoring,
        // so its release/at-rest reconcile holds for banked commits in UO mode and converges-down in the others.
        _predictor?.SetClientDriven(nowUo);

        if (LocalNetworkId is { } id && _entities.TryGetValue(id, out var local))
        {
            local.ReanchorLocalDriver(mode, _currentTime, IsWalkableForPrediction, ResolveCadence(local.StepCooldownMs), ResolveTickMs());
        }
    }

    // UO1: declares this session's movement model to the server (true = client-driven UO mode, false = server-paced).
    // ReliableOrdered — a lost flag would desync the server's pacing decision (double-step or stall). No-op safe
    // before connect (Send routes to the test sink / queues). Re-sent on (re)login/respawn via EnsurePredictor.
    private void SendMovementModeSignal(bool clientDriven)
    {
        Send(new MovementModeMessage(clientDriven), DeliveryMethod.ReliableOrdered);
    }

    // S94: the live cosmetic lead distance (tiles) — how far model B glides ahead of the confirmed tile before
    // holding. [0.0, 1.0]; default 1.0 = current model B. Reflects the value last set (seeded into the F5 field
    // on panel open). Only model B's render reads it; the value is inert in UoClientDriven.
    public double CosmeticLeadTiles => _cosmeticLeadTiles;

    // S94: live-tunes the cosmetic lead distance (F5 "Cosmetic lead (tiles)"). Clamped to [0.0, 1.0] (0 ≈ no
    // visible lead, 1.0 = one full tile / current model B). Routed to the active cosmetic driver immediately (no
    // restart); stored at the client level too so a value set before the local entity attaches, or after a
    // respawn re-creates the driver, is re-applied by AttachCosmetic. No-op safe when no cosmetic driver yet.
    public void SetCosmeticLeadTiles(double tiles)
    {
        _cosmeticLeadTiles = Math.Clamp(tiles, 0.0d, 1.0d);
        if (LocalNetworkId is { } id && _entities.TryGetValue(id, out var local))
        {
            local.SetCosmeticLeadTiles(_cosmeticLeadTiles);
        }
    }

    // S102: whether model B snaps the render to the confirmed tile on release (S91). Reflects the value last set
    // (seeded into the F6 toggle on panel open). Inert in UoClientDriven.
    public bool SnapOnRelease => _snapOnRelease;

    // S102: live-toggles model B's release snap (F6 "Snap on release"). Routed to the active cosmetic driver
    // immediately (no restart); stored at the client level too so a value set before the local entity attaches, or
    // after a respawn re-creates the driver, is re-applied by AttachCosmetic. No-op safe when no cosmetic driver yet.
    public void SetSnapOnRelease(bool snap)
    {
        _snapOnRelease = snap;
        if (LocalNetworkId is { } id && _entities.TryGetValue(id, out var local))
        {
            local.SetSnapOnRelease(snap);
        }
    }

    // S103: whether model B commits a near-done step on release (vs snapping back). Reflects the value last set
    // (seeded into the F6 toggle on panel open). Inert in UoClientDriven.
    public bool CommitStepOnRelease => _commitStepOnRelease;

    // S103: live-toggles model B's commit-step-on-release (F6). Routed to the active cosmetic driver immediately
    // (no restart); stored at the client level so a value set before attach / after a respawn is re-applied by
    // AttachCosmetic. No-op safe when no cosmetic driver yet.
    public void SetCommitStepOnRelease(bool enabled)
    {
        _commitStepOnRelease = enabled;
        if (LocalNetworkId is { } id && _entities.TryGetValue(id, out var local))
        {
            local.SetCommitStepOnRelease(enabled);
        }
    }

    // S103: the commit threshold (0..1) — how far the cosmetic lead must have glided onto the next tile at release
    // for a commit to fire. Reflects the value last set (seeded into the F6 field on panel open). Clamped [0,1].
    public double CommitStepThreshold => _commitStepThreshold;

    // S103: live-tunes the commit threshold (F6 "Commit threshold (0..1)"). Clamped [0,1]; routed to the active
    // cosmetic driver immediately (no restart); stored at the client level so it survives attach/respawn.
    public void SetCommitStepThreshold(double threshold)
    {
        _commitStepThreshold = Math.Clamp(threshold, 0.0d, 1.0d);
        if (LocalNetworkId is { } id && _entities.TryGetValue(id, out var local))
        {
            local.SetCommitStepThreshold(_commitStepThreshold);
        }
    }

    // UO4: whether the predictor settles-then-goes on a ~180° reversal (F6 "Stop on reversal"). Reflects the value
    // last set (seeded into the F6 toggle on panel open). Only the predictor mode (UoClientDriven) reads it; inert
    // in the cosmetic mode.
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
        if (!UsesPredictor(_renderMode))
        {
            // RENDER1: CosmeticLead (model B) — feed the held intent to the cosmetic driver (no tile is banked; it
            // only records the held direction). Tick glides the render early toward the held direction.
            if (LocalNetworkId is { } id && _entities.TryGetValue(id, out var local))
            {
                // S103: a fresh keydown supersedes any in-flight commit reconciliation — the player resumed moving,
                // so the cosmetic re-arms a lead and the old pending tracker is moot. (SetCosmeticIntent on a
                // moving intent leaves the cosmetic's own HasPendingCommit alone, but Tick re-arming the lead makes
                // it irrelevant; clear our tracker so a later snapshot doesn't snap-back mid-walk.)
                if (moving)
                {
                    _pendingCommit = null;
                }

                var release = local.SetCosmeticIntent(moving, direction, _currentTime);
                // S103: a release past the commit threshold returns ShouldCommit — the render is already gliding to
                // the committed tile (no snap). Send the server-validated commit and start tracking it so the
                // accept (confirmed tile reaches the target) or reject (tile never advances) reconciles. The commit
                // rides a FRESH sequence strictly greater than the stop intent's, so the server's shared move-seq
                // cursor accepts it after the keyup (and a re-ordered duplicate can't fire twice). NET2: it rides
                // the same redundant-unreliable StepCommitBatch channel as the UO stream (recorded in the ring,
                // shipped as a batch) so a dropped release-commit is recovered from a later packet's window.
                if (release.ShouldCommit)
                {
                    // NET3: a model-B release commit is a "finish this step NOW" request — it is not banked on a
                    // predictor gate boundary, so its authored tick is the PRESENT estimated server tick (the
                    // predictor's calibrated clock). The server applies it at that authored tick like any other
                    // commit. _predictor is created by EnsurePredictor above in every mode, so it is available here.
                    var authoredTick = _predictor is { } p ? (uint)Math.Max(0, p.EstimateServerTick(_currentTime)) : 0u;
                    RecordStepCommit(++_moveSequence, authoredTick, release.Direction);
                    SendStepCommitBatch();
                    _pendingCommit = new PendingCommit
                    {
                        Target = release.CommitTarget,
                        BaseStepSeq = _lastRecipientStepSeq,
                        SnapshotsWaited = 0,
                    };
                }
            }
        }
        else
        {
            _predictor?.SetIntent(moving, direction, _currentTime);
        }

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
        // UO3: a freshly-attached predictor defaults to server-paced; if the active mode is UoClientDriven (e.g. a
        // respawn / AOI re-entry while already in UO mode) re-declare the client-driven flag so its release/at-rest
        // reconcile holds for banked commits, in step with the MovementModeMessage re-sent below.
        _predictor.SetClientDriven(_renderMode == MovementRenderMode.UoClientDriven);
        // UO4: re-seed the stop-on-reversal lever onto the freshly-attached (or respawn-recreated) predictor so a
        // value set before attach / after a respawn is honoured (mirrors the cosmetic-lever seeding above).
        _predictor.SetStopOnReversal(_stopOnReversal);
        // RENDER1: attach the parallel cosmetic driver too (idempotent), anchored to the same confirmed tile. It
        // DRIVES the render in CosmeticLead; in UoClientDriven it is dormant and the predictor owns the render.
        local.AttachCosmetic(ResolveCadence(local.StepCooldownMs), IsWalkableForPrediction);
        // S94: seed the freshly-attached (or respawn-recreated) cosmetic driver with the current lead-distance
        // lever value, so a value set before attach / before respawn is honoured (mirrors how cadence is
        // threaded). Default 1.0 keeps model B byte-for-byte.
        local.SetCosmeticLeadTiles(_cosmeticLeadTiles);
        // S102: seed the freshly-attached (or respawn-recreated) cosmetic driver with the current snap-on-release
        // lever value, mirroring how the lead distance is threaded. Default true keeps model B byte-for-byte.
        local.SetSnapOnRelease(_snapOnRelease);
        // S103: seed the commit-step-on-release enable + threshold the same way.
        local.SetCommitStepOnRelease(_commitStepOnRelease);
        local.SetCommitStepThreshold(_commitStepThreshold);
        // If the active mode uses the cosmetic driver (CosmeticLead) and the local entity only just attached (or
        // respawned), activate + anchor the freshly-attached cosmetic driver so the live mode is honoured without
        // needing an F6 toggle. ReanchorLocalDriver also sets LeadEnabled from the mode.
        if (!UsesPredictor(_renderMode))
        {
            local.ReanchorLocalDriver(_renderMode, _currentTime, IsWalkableForPrediction, ResolveCadence(local.StepCooldownMs), ResolveTickMs());
        }
        else if (_renderMode == MovementRenderMode.UoClientDriven)
        {
            // UO1: the local entity just (re)attached — a fresh login / respawn / AOI re-entry. Re-declare the
            // client-driven mode to the server so a flag lost across that lifecycle event can't leave the server
            // auto-pacing AND the client committing (double-stepping). ReliableOrdered; harmless if redundant.
            SendMovementModeSignal(clientDriven: true);
        }
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
        // S103: drop any in-flight commit tracking — the entity it belonged to is gone (despawn / AOI exit / logout).
        _pendingCommit = null;
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

            var confirmation = entity.ApplySnapshot(state.Tile, state.Facing, _currentTime, sequence, _lastRecipientStepSeq, serverTick, state.Depleted);
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
                localEntity.Depleted);
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

        // S103: resolve an in-flight commit-step against this snapshot. The cosmetic driver's Confirm already moved
        // the render (accept = flowed onto the committed tile; a diverging confirm = cut to wherever the server put
        // us), clearing ITS pending flag in those cases. The remaining case THIS owns is the reject-with-no-tile-
        // change: the server rejected the commit, so the confirmed tile never reaches the target and the render
        // would otherwise keep gliding there forever. Detect it via the grace below and snap back.
        ReconcilePendingCommit(serverTick);

        _lastAppliedSnapshotSequence = sequence;
    }

    // S103: drive the commit-step reconciliation after a snapshot was applied. Ordering is the load-bearing part:
    //   * If the cosmetic driver already cleared its pending flag, the commit RESOLVED this snapshot (accept: the
    //     confirmed tile reached the target; or a diverging confirm cut to the server's tile). Clear our tracker.
    //   * Otherwise the commit is still unresolved on the render side. We must NOT snap back just because the tile
    //     hasn't advanced yet — a not-yet-arrived accept looks identical. So we only declare REJECT once the server
    //     has demonstrably processed steps since the send (RecipientStepSeq advanced past the base) without the
    //     tile reaching the target, OR a bounded snapshot grace has elapsed (covers a pure reject where the server
    //     did nothing, so RecipientStepSeq never moves). On reject, snap the render back to the confirmed tile.
    private void ReconcilePendingCommit(uint serverTick)
    {
        if (_pendingCommit is not { } pending)
        {
            return;
        }

        if (LocalNetworkId is not { } localId || !_entities.TryGetValue(localId, out var local))
        {
            _pendingCommit = null;
            return;
        }

        var cosmetic = local.Cosmetic;
        if (cosmetic is null || !cosmetic.HasPendingCommit)
        {
            // The cosmetic resolved it (accept, or a diverging confirm). Nothing to snap.
            _pendingCommit = null;
            return;
        }

        // Accept also when the confirmed tile reached the target but the cosmetic missed clearing (belt-and-braces;
        // normally Confirm clears it). Treat tile==target as accepted regardless.
        if (local.Tile == pending.Target)
        {
            cosmetic.ClearPendingCommit();
            _pendingCommit = null;
            return;
        }

        pending.SnapshotsWaited++;
        var serverProcessedAStep = _lastRecipientStepSeq != pending.BaseStepSeq;
        if (serverProcessedAStep || pending.SnapshotsWaited >= CommitRejectGraceSnapshots)
        {
            // Reject: the server stepped (or enough grace elapsed) without honouring the commit. Snap the render
            // back to the confirmed tile (exactly the pre-S103 disagreeing-release behaviour).
            cosmetic.SnapTo(local.Tile, _currentTime);
            _pendingCommit = null;
            return;
        }

        _pendingCommit = pending;
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
            existing.ApplySnapshot(tile, facing, _currentTime, _lastAppliedSnapshotSequence ?? 0, _lastRecipientStepSeq, serverTick: null, existing.Depleted);
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
        // S89 model B: the parallel cosmetic-lead driver (non-null only for the local player once attached). It
        // DRIVES the render only while _cosmeticActive (RenderMode == CosmeticLead); otherwise it is dormant and
        // the predictor owns the render exactly as today. Reverting S89 removes this field and restores A.
        private LocalPlayerCosmetic? _cosmetic;
        private bool _cosmeticActive;

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

        public EntityConfirmationDebug ApplySnapshot(TileCoord tile, Direction8 facing, TimeSpan receivedAt, uint snapshotSequence, uint recipientStepSeq, uint? serverTick, bool depleted = false)
        {
            var previousTile = Tile;
            // Tile/Facing always track the SERVER-CONFIRMED state: LocalTile (harvest/click targeting) reads
            // it, and the renderer's AuthoritativeTile uses it. Prediction only affects the interpolated
            // render position, never this authoritative tile.
            Tile = tile;
            Depleted = depleted;
            LastSeenSnapshotSequence = snapshotSequence;
            // S89 model B: the cosmetic driver owns the render. The confirmed tile advances ONLY here (the server
            // ack). No step-seq / reconcile / replay — Confirm cuts/snaps the render to the confirmed tile. The
            // predictor is left dormant (re-seeded on a live A<->B switch), so model A is byte-for-byte unchanged
            // when this branch is not taken.
            if (_cosmetic is not null && _cosmeticActive)
            {
                _cosmetic.Confirm(tile, facing, receivedAt);
                Facing = _cosmetic.Facing;
                return new EntityConfirmationDebug(
                    tile != previousTile,
                    0,
                    _cosmetic.CadenceMs,
                    _cosmetic.RenderPosition);
            }

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

        // S89: attaches the parallel model-B cosmetic driver (idempotent), anchored to the current confirmed tile
        // + facing. It is dormant until RenderMode flips to CosmeticLead (ReanchorLocalDriver activates it). The
        // isWalkable oracle is the SAME one model A uses (cosmetic glide-direction gate; no tile banked).
        public LocalPlayerCosmetic AttachCosmetic(double cadenceMs, Func<TileCoord, bool> isWalkable)
        {
            _cosmetic ??= new LocalPlayerCosmetic(Tile, Facing, cadenceMs, isWalkable);
            return _cosmetic;
        }

        // S89: feeds held intent to the cosmetic driver (no tile banked — records the held direction so
        // TickCosmetic glides the render early). No-op if the cosmetic driver isn't attached yet.
        // S103: returns the release decision (whether a commit-step should be sent + its target/direction); default
        // (ShouldCommit=false) when no driver / on a moving intent / on a sub-threshold release.
        public CosmeticReleaseDecision SetCosmeticIntent(bool moving, Direction8 direction, TimeSpan now)
        {
            return _cosmetic?.SetIntent(moving, direction, now) ?? default;
        }

        // S103: the cosmetic driver, or null if not attached/active. Exposed so MmoClient can drive commit-step
        // reconciliation (pending state, accept-clear, reject snap-back) against the active driver.
        public LocalPlayerCosmetic? Cosmetic => _cosmeticActive ? _cosmetic : null;

        // S89: advances the cosmetic render to now (the early-lead glide). No-op if not attached.
        public void TickCosmetic(TimeSpan now)
        {
            _cosmetic?.Tick(now);
        }

        // S94: live-sets the cosmetic lead distance (tiles) on the cosmetic driver. No-op if the driver isn't
        // attached yet; MmoClient.EnsurePredictor re-seeds the current value when AttachCosmetic creates it.
        public void SetCosmeticLeadTiles(double tiles)
        {
            if (_cosmetic is not null)
            {
                _cosmetic.MaxLeadTiles = tiles;
            }
        }

        // S102: live-sets model B's release-snap flag on the cosmetic driver. No-op if the driver isn't attached
        // yet; MmoClient.EnsurePredictor re-seeds the current value when AttachCosmetic creates it.
        public void SetSnapOnRelease(bool snap)
        {
            if (_cosmetic is not null)
            {
                _cosmetic.SnapOnRelease = snap;
            }
        }

        // S103: live-sets model B's commit-step-on-release enable + threshold on the cosmetic driver. No-op if the
        // driver isn't attached yet; MmoClient.EnsurePredictor re-seeds the current values when AttachCosmetic
        // creates it.
        public void SetCommitStepOnRelease(bool enabled)
        {
            if (_cosmetic is not null)
            {
                _cosmetic.CommitStepEnabled = enabled;
            }
        }

        public void SetCommitStepThreshold(double threshold)
        {
            if (_cosmetic is not null)
            {
                _cosmetic.CommitThreshold = threshold;
            }
        }

        // S89: switches the active local-player render model LIVE, re-anchoring the newly-active driver from the
        // CURRENT render position so the avatar doesn't pop. A->B seeds the cosmetic driver where the predictor
        // is showing; B->A re-seeds the predictor (its PredictedTile onto the confirmed tile, its render tween
        // onto the current render position). The freshly-attached drivers are created if missing.
        public void ReanchorLocalDriver(MovementRenderMode mode, TimeSpan now, Func<TileCoord, bool> isWalkable, double cadenceMs, double tickMs)
        {
            if (!UsesPredictor(mode))
            {
                // RENDER1: CosmeticLead (model B) is the only cosmetic-driver mode now; LeadEnabled is always ON
                // here (B leads + snaps on release).
                var renderSource = _cosmetic is not null && _cosmeticActive
                    ? _cosmetic.RenderPosition
                    : _predictor is not null ? _predictor.RenderPosition : _interpolator.RenderPosition;
                AttachCosmetic(cadenceMs, isWalkable);
                _cosmetic!.LeadEnabled = mode == MovementRenderMode.CosmeticLead;
                _cosmetic!.ReanchorTo(Tile, Facing, renderSource, now);
                _cosmeticActive = true;
            }
            else
            {
                var currentRender = _cosmetic is not null ? _cosmetic.RenderPosition : _interpolator.RenderPosition;
                _cosmeticActive = false;
                if (_predictor is not null)
                {
                    // Re-anchor model A onto the confirmed tile + current render position so resuming prediction
                    // doesn't jump (Reconcile re-bases the predicted tile; the snap-distance is 0 so the render
                    // tween settles onto the current position).
                    _predictor.Reconcile(Tile, _predictor.PredictedStepSeq, now);
                    _predictor.Sample(now);
                    _ = currentRender;
                }
            }
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
            return new ReplicatedEntity(NetworkId, CharacterId, Kind, DisplayName, Tile, Facing, IsLocal, Depleted);
        }

        public EntityRenderState ToRenderState(TimeSpan now)
        {
            // S53: the local predicted player renders from the predictor's OWN present-time tween (snappy, no
            // playout delay). Everything else (remote players, resources) keeps the buffered interpolator.
            // S89: in CosmeticLead mode the local player renders from the cosmetic driver instead (no banked
            // tile; glides early on input). Render-source selection only — Tile stays confirmed in both modes.
            RenderPosition position;
            Direction8 facing;
            if (_cosmetic is not null && _cosmeticActive)
            {
                position = _cosmetic.Sample(now);
                facing = _cosmetic.Facing;
            }
            else if (_predictor is not null)
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
            return new EntityRenderState(NetworkId, CharacterId, Kind, DisplayName, position, Tile, facing, IsLocal, Depleted);
        }
    }

    private readonly record struct EntityConfirmationDebug(
        bool TileChanged,
        int QueueDepth,
        double EffectiveCadenceMs,
        RenderPosition RenderPosition);

    // S103: a commit-step in flight. Target = the committed tile; BaseStepSeq = RecipientStepSeq at send time;
    // SnapshotsWaited = how many snapshots have been applied since the send (the bounded grace). Mutable struct held
    // as a nullable field (single in-flight commit at a time — one keyup commits at most one step).
    private struct PendingCommit
    {
        public TileCoord Target;
        public uint BaseStepSeq;
        public int SnapshotsWaited;
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
