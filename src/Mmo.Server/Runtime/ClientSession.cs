using LiteNetLib;
using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class ClientSession
{
    private readonly HashSet<uint> _lastSnapshotEntityIds = [];
    private readonly HashSet<uint> _knownEntityIds = [];

    // LIVING-ENEMIES P3: the set of SPAWNER ids whose red-tile marker this viewer currently knows (the marker is in
    // its AOI). Mirrors _knownEntityIds for the non-entity spawner markers — a SpawnerMarker(Active=true) is sent when
    // a spawner enters AOI (added here) and Active=false when it leaves (removed). Lets the per-tick spawner-AOI pass
    // diff cheaply against the live spawner set without re-sending a known marker each tick.
    private readonly HashSet<uint> _knownSpawnerIds = [];

    // TELEGRAPH T2: the set of TELEGRAPH ids this viewer has been sent (mirrors _knownSpawnerIds for the non-entity
    // telegraph announcements). An id is added when the AOI diff pass sends the TelegraphMessage and removed only when
    // the telegraph RESOLVES server-side (never on AOI-exit: there is no cancel message, the client renders to T
    // regardless, and keeping the id known means an exit-and-re-enter mid-windup can't trigger a duplicate send).
    private readonly HashSet<ulong> _knownTelegraphIds = [];

    // Acked baseline (S46): the entity revision the CLIENT has acknowledged receiving, per visible
    // entity. Snapshot selection sends an entity iff its current revision differs from this acked
    // revision — so a dropped (never-acked) snapshot's changes stay "unacked" and are re-sent next tick
    // (self-healing under loss). This replaces the old "last sent" baseline, which desynced on any drop
    // and needed the periodic full heartbeat to recover.
    private readonly Dictionary<uint, uint> _ackedEntityRevisions = [];

    // Per-snapshot-sequence record of what each outgoing snapshot CARRIED (entity -> revision), so an ack
    // of sequence S can advance the acked baseline for every entity that S delivered. Kept as a small ring
    // of reused PendingSnapshot buffers (no per-tick allocation): a snapshot reuses a cleared buffer, and
    // an ack drains+recycles every buffer with sequence <= acked. Acks are monotonic
    // (LastAcknowledgedSnapshotSequence only advances), so draining is one linear sweep.
    private readonly List<PendingSnapshotRecord> _pendingSnapshots = [];
    private readonly Stack<PendingSnapshotRecord> _pendingSnapshotPool = new();

    private uint _nextSnapshotSequence = 1;
    private uint? _lastInteractTick;

    // Tick of the last WorldSnapshot actually sent to this recipient (0 before the first). Drives the
    // low-rate EMPTY keep-alive: with the periodic full heartbeat gone, an idle-but-healthy viewer whose
    // per-recipient delta is empty (static AOI: nothing moved, nothing entered/left) would otherwise be
    // sent no snapshot at all, never ack, and its silence clock would grow until the disconnect bound
    // wrongly dropped it (~8 s on a perfect connection). The keep-alive sends a tiny empty snapshot once
    // the cadence elapses so every viewer keeps acking. Single uint reused per recipient — no per-tick GC.
    private uint _lastSnapshotSentTick;

    // Tick at which the oldest currently-unacked snapshot was sent. 0 when nothing is outstanding. Drives
    // the safety RE-BASELINE bound (cheap recovery) so a silent client cannot grow per-viewer pending
    // state without limit. Reset by ForceFullRebaseline, so it measures "ticks since pending grew", not
    // total silence — the disconnect bound below uses a separate, non-resetting clock.
    private uint _oldestUnackedSentTick;

    // Tick of the last time the client acknowledged ANY snapshot (or, until the first snapshot, the tick
    // the first snapshot went out). Drives the DISCONNECT bound. Unlike _oldestUnackedSentTick this is NOT
    // reset by a forced re-baseline, so a client that keeps getting re-baselined but never acks is still
    // disconnected once total silence passes the larger threshold.
    private uint _lastAckOrFirstSendTick;

    // CONTINUOUS MIGRATION (Phase 3, v36): the tick a fresh per-input MoveIntent last arrived — drives the
    // keepalive safety timeout (a "moving" session silent past the timeout is force-stopped). The integrate cursor
    // itself is _lastInputSeq (below).
    private uint _lastMoveIntentTick;

    // CONTINUOUS MIGRATION (Phase 3, v36): the per-INPUT continuous-move dedup cursor — the highest MoveIntent
    // InputSeq the server has INTEGRATED for this session. The server integrates each fresh input (InputSeq >
    // _lastInputSeq) by its dt on the receive path, then advances this; it rides every snapshot header (LastInputSeq)
    // so the Phase-4 predictor can trim/replay its unacked input buffer. Replaces the v35 held-intent + commit
    // cursors (deleted with the tile-step machinery).
    private uint _lastInputSeq;

    // CONTINUOUS MIGRATION (Phase 3, v36): the per-peer WALL-CLOCK dt BUDGET (the anti-speedhack core). The client
    // now controls dt, so the server caps the TOTAL sim-time a peer may integrate to REAL elapsed time (+ a small
    // burst allowance for jitter). _dtBudgetSeconds is the credit remaining: it accrues REAL elapsed seconds each
    // tick (CreditMoveDtBudget, capped at the burst allowance) and each integrated input DEBITS by the dt it actually
    // consumed (ConsumeMoveDtBudget). An input may consume at most the remaining budget — so over any window the
    // peer's integrated sim-time <= real elapsed + burst. Starts at the burst allowance (a fresh peer may move
    // immediately within the allowance).
    private double _dtBudgetSeconds;
    private bool _dtBudgetSeeded;

    // COMBAT-S2B: the ATTACK-stream dedup cursor (the highest AttackMessage seq accepted). A SEPARATE, INDEPENDENT
    // stream from movement — it shares NOTHING with the move input cursor. This is the #1 rule from the NET6
    // desync bug: two streams on one cursor strand each other. The client mints attack seqs off its OWN dedicated
    // _attackSeq counter, and HandleAttack gates on THIS cursor only. An attack seq never touches the move input
    // cursor and vice-versa, so a movement input can never pre-dedup an attack (or the reverse). Attacks are
    // reliable-ordered + low-rate, so a strict `seq > cursor` monotonic gate is all the dedup needed.
    private uint _lastAttackSeq;

    // MOVEMENT-ACTIONS Phase B1: the ACTION-stream dedup cursor (the highest accepted ActionIntentMessage seq). A
    // THIRD independent stream alongside movement AND attack — it shares NOTHING with either (_lastInputSeq /
    // _lastAttackSeq). This is the NET6 "two streams, one cursor" lesson applied to a third stream: a fresh cursor so
    // an action can never pre-dedup a move/attack (or the reverse). The client mints action seqs off its OWN dedicated
    // _nextActionSeq counter; HandleActionIntent gates on THIS cursor only. Reliable-ordered + low-rate, so a strict
    // monotonic `seq > cursor` gate is all the dedup needed (identical to the attack cursor).
    private uint _lastActionSeq;

    // DUO-SKILLSHOT (exp/duo-abilities): the FIRE-stream dedup cursor (highest accepted FireSkillshotMessage seq). A
    // FOURTH independent stream alongside move/attack/action — it shares NOTHING with any of them (the NET6 "one
    // cursor per stream" lesson). The client mints fire seqs off its OWN counter; HandleFireSkillshot gates on THIS
    // cursor only. Reliable-ordered + low-rate, so a strict monotonic `seq > cursor` gate is all the dedup needed.
    private uint _lastFireSeq;

    // Minimum ticks between accepted Interact requests from one client. Cheap flood guard so a client
    // cannot spam the interaction path within a single tick or hammer it across consecutive ticks;
    // depleting a node already gates legitimate re-harvest for far longer.
    private const uint InteractCooldownTicks = 4;

    public ClientSession(NetPeer peer)
    {
        Peer = peer;
    }

    public NetPeer Peer { get; }
    public bool IsAuthenticated { get; private set; }
    public bool LoginInProgress { get; set; }
    public ulong? EntityId { get; private set; }
    public uint NetworkId { get; private set; }
    public Guid CharacterId { get; private set; }
    public string DisplayName { get; private set; } = "unknown";
    public ClientRole Role { get; private set; } = ClientRole.Player;
    public string ZoneId { get; private set; } = "sandbox";
    public int LastLatencyMs { get; set; }
    public int BadPacketCount { get; private set; }
    public uint LastAcknowledgedSnapshotSequence { get; private set; }

    // CONTINUOUS MIGRATION (Phase 3, v36): per-input continuous-move state. IsMoving is true while the last
    // integrated input carried a non-zero direction (the keepalive/animation "currently walking" flag); a fresh
    // (0,0) input or the keepalive force-stop clears it. LastMoveIntentTick is the tick a fresh input last arrived
    // (drives the keepalive safety timeout). LastInputSeq is the integrate cursor (rides the snapshot header).
    public bool IsMoving { get; private set; }
    public uint LastMoveIntentTick => _lastMoveIntentTick;
    public uint LastInputSeq => _lastInputSeq;

    // COMBAT-S2B: the attack-stream dedup cursor (the highest accepted AttackMessage seq). Exposed for tests that
    // assert the attack cursor advances independently of the movement cursor.
    public uint LastAttackSeq => _lastAttackSeq;

    // MOVEMENT-ACTIONS Phase B1: the action-stream dedup cursor (the highest accepted ActionIntentMessage seq).
    // Exposed for tests that assert the action cursor advances INDEPENDENTLY of the movement and attack cursors.
    public uint LastActionSeq => _lastActionSeq;

    // LOOT P4c: the ENTITY id of the corpse this session currently has its loot window OPEN on, or null if no
    // window is open. Set when the player opens a corpse (InteractRequest on it, eligibility-passed); cleared on
    // Close, on the corpse despawning/decaying, or on the player walking out of range. The server routes a
    // LootActionMessage to THIS corpse and pushes CorpseContents refreshes here, so a stale window can't loot a
    // different corpse. Keyed by entity id (stable for the corpse's life), like the GameServer's _corpses map.
    public ulong? OpenCorpseEntityId { get; private set; }

    public void SetOpenCorpse(ulong? corpseEntityId)
    {
        OpenCorpseEntityId = corpseEntityId;
    }

    // DUO-SKILLSHOT (exp/duo-abilities): the PAIRING seam — the FOUNDATION abilities 2-4 also consume. A mutual pair
    // links two online sessions; PartnerSession is the other session (null when solo). The pair is symmetric — the
    // GameServer /pair command sets BOTH sides' PartnerSession to each other and /unpair (or a disconnect) clears
    // BOTH. Ability logic reads PartnerSession to answer "who is this player's partner?" (e.g. the SkillshotEngine's
    // fusion pairing gate, and the aim-preview relay's target). Kept as a plain session reference (not a copied
    // network id) so it can never go stale while the partner is alive; the network id is derived on demand.
    public ClientSession? PartnerSession { get; private set; }

    public bool HasPartner => PartnerSession is not null;

    public void SetPartner(ClientSession? partner) => PartnerSession = partner;

    // LIVING-ENEMIES P3: the player-death respawn guard. When the player's HP hits 0 it DIES: IsDead is set and
    // RespawnAtTick is the tick the server will teleport it back to spawn at full HP. While IsDead the player must not
    // take further hits, act, or die again (a simple "downed" window). Cleared on respawn. RespawnAtTick is null when
    // alive. Minimal — no corpse/loot/penalty/death-screen this phase.
    public bool IsDead { get; private set; }
    public uint? RespawnAtTick { get; private set; }

    // Marks the player dead at `serverTick`, scheduling the respawn `respawnDelayTicks` later. No-op (returns false) if
    // already dead — so a flurry of hits on the same tick can't re-trigger death or reset the timer.
    public bool MarkDead(uint serverTick, uint respawnDelayTicks)
    {
        if (IsDead)
        {
            return false;
        }

        IsDead = true;
        RespawnAtTick = serverTick + respawnDelayTicks;
        return true;
    }

    // True iff the player is dead and its respawn due tick has arrived (the server polls this each tick).
    public bool IsRespawnDue(uint serverTick) =>
        IsDead && RespawnAtTick.HasValue && serverTick >= RespawnAtTick.Value;

    // Clears the dead state on respawn (the caller has teleported + refilled the entity).
    public void MarkAlive()
    {
        IsDead = false;
        RespawnAtTick = null;
    }

    // LIVING-ENEMIES P3: spawner-marker AOI bookkeeping (mirrors the entity Knows/Remember/Forget trio).
    public bool KnowsSpawner(uint spawnerId) => _knownSpawnerIds.Contains(spawnerId);

    public void RememberKnownSpawner(uint spawnerId) => _knownSpawnerIds.Add(spawnerId);

    public bool ForgetKnownSpawner(uint spawnerId) => _knownSpawnerIds.Remove(spawnerId);

    public IReadOnlyCollection<uint> KnownSpawnerIds => _knownSpawnerIds;

    // TELEGRAPH T2: telegraph-announcement bookkeeping (mirrors the spawner-marker trio above; ulong ids — the
    // scheduler's monotonic id space, not a network id).
    public bool KnowsTelegraph(ulong telegraphId) => _knownTelegraphIds.Contains(telegraphId);

    public void RememberKnownTelegraph(ulong telegraphId) => _knownTelegraphIds.Add(telegraphId);

    public bool ForgetKnownTelegraph(ulong telegraphId) => _knownTelegraphIds.Remove(telegraphId);

    public IReadOnlyCollection<ulong> KnownTelegraphIds => _knownTelegraphIds;

    public void Authenticate(uint networkId, Guid characterId, string displayName, ClientRole role, string zoneId)
    {
        NetworkId = networkId;
        CharacterId = characterId;
        DisplayName = displayName;
        Role = role;
        ZoneId = zoneId;
        IsAuthenticated = true;
        LoginInProgress = false;
    }

    public void AttachEntity(WorldEntity entity)
    {
        EntityId = entity.Id;
        NetworkId = entity.NetworkId;
    }

    public int RecordBadPacket()
    {
        BadPacketCount++;
        return BadPacketCount;
    }

    // Returns true and arms the cooldown if an Interact request is allowed at this tick; false if the
    // client is still inside its interaction cooldown window.
    public bool TryConsumeInteract(uint serverTick)
    {
        if (_lastInteractTick.HasValue && serverTick - _lastInteractTick.Value < InteractCooldownTicks)
        {
            return false;
        }

        _lastInteractTick = serverTick;
        return true;
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): claims a fresh per-input MoveIntent on the receive path. Rejects stale/
    // duplicate inputs (InputSeq <= the cursor) and returns false WITHOUT mutating state; otherwise advances the
    // integrate cursor (LastInputSeq) + refreshes the keepalive tick and returns true (the caller may then integrate
    // by this input's dt). The cursor advances even for a fresh ROOTED/STOP input so the snapshot's LastInputSeq
    // still ACKs it (the client trims its buffer) even though that input produces no motion.
    public bool TryBeginMoveInput(uint inputSeq, uint serverTick)
    {
        if (inputSeq <= _lastInputSeq)
        {
            return false;
        }

        _lastInputSeq = inputSeq;
        _lastMoveIntentTick = serverTick;
        return true;
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): records whether the session is currently moving (the last integrated
    // input carried a non-zero direction). Drives the keepalive/animation "walking" flag, not the integration.
    public void SetMoving(bool moving)
    {
        IsMoving = moving;
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): the per-peer wall-clock dt BUDGET — the anti-speedhack core (see the
    // _dtBudgetSeconds field). CreditMoveDtBudget accrues REAL elapsed seconds each server tick, capped so the
    // credit never exceeds burstAllowanceSeconds (a fresh/idle peer can burst up to the allowance, not unboundedly).
    public void CreditMoveDtBudget(double realElapsedSeconds, double burstAllowanceSeconds)
    {
        if (!_dtBudgetSeeded)
        {
            // Seed a fresh peer at the full burst allowance so it can move immediately within the jitter window.
            _dtBudgetSeconds = burstAllowanceSeconds;
            _dtBudgetSeeded = true;
            return;
        }

        if (realElapsedSeconds > 0d)
        {
            _dtBudgetSeconds += realElapsedSeconds;
        }

        if (_dtBudgetSeconds > burstAllowanceSeconds)
        {
            _dtBudgetSeconds = burstAllowanceSeconds;
        }
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): debits the dt budget for one integrated input, returning the dt the
    // server is ALLOWED to integrate by — min(requestedDt, remaining budget). A peer flooding many max-dt inputs
    // drains the budget faster than CreditMoveDtBudget refills it (real time), so the excess is clamped to 0 and
    // those inputs advance nothing: over any window the peer's integrated sim-time <= real elapsed + the burst
    // allowance. requestedDt is assumed already per-input-sanity-clamped to [0, max] by the caller.
    public double ConsumeMoveDtBudget(double requestedDt)
    {
        if (requestedDt <= 0d)
        {
            return 0d;
        }

        var allowed = System.Math.Min(requestedDt, _dtBudgetSeconds);
        if (allowed <= 0d)
        {
            return 0d;
        }

        _dtBudgetSeconds -= allowed;
        return allowed;
    }

    // COMBAT-S2B: advances the ATTACK-sequence cursor (_lastAttackSeq) for an inbound AttackMessage. Rejects stale
    // sequences (seq <= the attack cursor) so a re-ordered/duplicate/replayed attack can't fire twice, and returns
    // false WITHOUT mutating anything. Crucially it does NOT consult or advance the move input cursor — the
    // attack stream is fully independent of movement (the NET6 lesson — never share a cursor with the move stream).
    // Returns true iff the seq was fresh on the attack cursor (the caller may then resolve the attack). The cursor advances even though the caller
    // may later reject the attack on cooldown, so a re-sent already-seen attack is deduped here and never resolves.
    public bool TryConsumeAttackSequence(uint sequence)
    {
        if (sequence <= _lastAttackSeq)
        {
            return false;
        }

        _lastAttackSeq = sequence;
        return true;
    }

    // MOVEMENT-ACTIONS Phase B1: advances the ACTION-sequence cursor (_lastActionSeq) for an inbound
    // ActionIntentMessage. Rejects stale/duplicate sequences (seq <= the action cursor) so a re-ordered/duplicate/
    // replayed action trigger can't start twice, returning false WITHOUT mutating anything. It does NOT consult or
    // advance the move OR attack cursor — the action stream is fully independent (the NET6 lesson applied to a third
    // stream). Returns true iff the seq was fresh on the action cursor (the caller may then validate + start the
    // action). The cursor advances even if the caller later rejects the trigger (can-act/cooldown), so a re-sent
    // already-seen trigger is deduped here and never starts.
    public bool TryConsumeActionSequence(uint sequence)
    {
        if (sequence <= _lastActionSeq)
        {
            return false;
        }

        _lastActionSeq = sequence;
        return true;
    }

    // DUO-SKILLSHOT: advances the FIRE-sequence cursor (_lastFireSeq) for an inbound FireSkillshotMessage. Rejects
    // stale/duplicate sequences (seq <= the fire cursor) so a re-ordered/duplicate/replayed fire can't spawn two
    // projectiles, returning false WITHOUT mutating anything. Fully independent of the move/attack/action cursors
    // (the NET6 lesson applied to a fourth stream). Returns true iff the seq was fresh (the caller may then fire).
    public bool TryConsumeFireSequence(uint sequence)
    {
        if (sequence <= _lastFireSeq)
        {
            return false;
        }

        _lastFireSeq = sequence;
        return true;
    }

    // Clears the moving flag to stopped (keepalive safety timeout, or any server-side halt). Does not
    // touch the input cursor, so a later genuine input still has to advance past LastInputSeq.
    public void ClearMoveIntent()
    {
        IsMoving = false;
    }

    public bool KnowsEntity(uint networkId)
    {
        return _knownEntityIds.Contains(networkId);
    }

    public void RememberKnownEntity(uint networkId)
    {
        _knownEntityIds.Add(networkId);
    }

    public void ForgetKnownEntity(uint networkId)
    {
        _knownEntityIds.Remove(networkId);
    }

    public bool WasInLastSnapshot(uint networkId)
    {
        return _lastSnapshotEntityIds.Contains(networkId);
    }

    public IEnumerable<uint> SnapshotEntitiesMissingFrom(IReadOnlySet<uint> currentNetworkIds)
    {
        foreach (var networkId in _lastSnapshotEntityIds)
        {
            if (!currentNetworkIds.Contains(networkId))
            {
                yield return networkId;
            }
        }
    }

    public void CollectSnapshotEntitiesMissingFrom(IReadOnlySet<uint> currentNetworkIds, ICollection<uint> destination)
    {
        foreach (var networkId in _lastSnapshotEntityIds)
        {
            if (!currentNetworkIds.Contains(networkId))
            {
                destination.Add(networkId);
            }
        }
    }

    public void RememberSnapshotEntities(IEnumerable<uint> networkIds)
    {
        _lastSnapshotEntityIds.Clear();
        foreach (var networkId in networkIds)
        {
            _lastSnapshotEntityIds.Add(networkId);
        }
    }

    public void RememberSnapshotEntities(IReadOnlyList<WorldEntity> entities)
    {
        _lastSnapshotEntityIds.Clear();
        foreach (var entity in entities)
        {
            _lastSnapshotEntityIds.Add(entity.NetworkId);
        }
    }

    // True when the entity's current revision already matches the revision the CLIENT acknowledged: it is
    // in sync, so this tick's snapshot can omit it. A never-seen entity (no acked revision) returns false
    // → it is sent (this is what re-baselines an AOI-entry entity). After a forced re-baseline
    // (ForceFullRebaseline clears the acked map) every visible entity returns false and is re-sent.
    public bool HasAckedCurrentRevision(WorldEntity entity)
    {
        return _ackedEntityRevisions.TryGetValue(entity.NetworkId, out var revision)
            && revision == entity.StateRevision;
    }

    // AOI exit / despawn: forget all baseline + pending state for the entity so a later re-entry
    // re-baselines cleanly. Removes the acked revision AND drops the entity from every still-unacked
    // per-seq carried record — otherwise a late ack for a snapshot that carried this entity would
    // re-insert a stale acked revision, and if the entity re-entered AOI at that SAME revision (e.g. a
    // static resource node that left and returned unchanged) selection would wrongly skip re-sending it,
    // desyncing the client (which deleted it on despawn). The pending ring is small (only snapshots in
    // flight), so this scan is cheap and bounded.
    public void ForgetEntityBaseline(uint networkId)
    {
        _ackedEntityRevisions.Remove(networkId);
        foreach (var record in _pendingSnapshots)
        {
            record.Remove(networkId);
        }
    }

    // Records that snapshot sequence `snapshotSequence`, sent at `serverTick`, carried these entities at
    // these revisions. Reuses a pooled buffer (no per-tick allocation). The carried set is the snapshot's
    // payload entities; only entities that were actually serialized are recorded.
    public PendingSnapshotRecord BeginPendingSnapshot(uint snapshotSequence, uint serverTick)
    {
        var record = _pendingSnapshotPool.Count > 0 ? _pendingSnapshotPool.Pop() : new PendingSnapshotRecord();
        record.Reset(snapshotSequence, serverTick);
        _pendingSnapshots.Add(record);
        if (_oldestUnackedSentTick == 0)
        {
            _oldestUnackedSentTick = serverTick;
        }

        if (_lastAckOrFirstSendTick == 0)
        {
            _lastAckOrFirstSendTick = serverTick;
        }

        return record;
    }

    public uint NextSnapshotSequence()
    {
        return _nextSnapshotSequence++;
    }

    // True when it has been at least `cadenceTicks` since a snapshot was last sent to this recipient (or it
    // has never been sent one). The caller uses this to emit a low-rate EMPTY keep-alive snapshot even when
    // the per-recipient delta is empty, so an idle-but-healthy viewer keeps acking and never trips the
    // disconnect bound. Returning true before the first send guarantees the keep-alive arms immediately for
    // a viewer that has no delta from its very first tick.
    public bool ShouldSendKeepAlive(uint serverTick, uint cadenceTicks)
    {
        return _lastSnapshotSentTick == 0 || serverTick - _lastSnapshotSentTick >= cadenceTicks;
    }

    // Records that a snapshot (real delta or empty keep-alive) was sent to this recipient at `serverTick`,
    // resetting the keep-alive cadence clock. Distinct from the pending/ack bookkeeping: a keep-alive carries
    // no entities, so it opens no pending record, but it still advances this clock. It also seeds the
    // disconnect clock if it has never been set — so a viewer that only ever receives keep-alives (empty
    // deltas) and never acks is still measured for silence and dropped by the disconnect bound; otherwise
    // _lastAckOrFirstSendTick (only set in BeginPendingSnapshot) would stay 0 and SilenceTicks would never
    // grow for a wedged idle client.
    public void RememberSnapshotSentTick(uint serverTick)
    {
        _lastSnapshotSentTick = serverTick;
        if (_lastAckOrFirstSendTick == 0)
        {
            _lastAckOrFirstSendTick = serverTick;
        }
    }

    // serverTick is the tick the ack was processed on; it refreshes the disconnect-bound clock. A stale or
    // duplicate ack (sequence already seen) still counts as the client being alive, so the clock advances
    // before the early return.
    public void AcknowledgeSnapshot(uint snapshotSequence, uint serverTick)
    {
        _lastAckOrFirstSendTick = serverTick;

        if (snapshotSequence <= LastAcknowledgedSnapshotSequence)
        {
            return;
        }

        LastAcknowledgedSnapshotSequence = snapshotSequence;

        // Advance the acked baseline for every entity carried by an acked-or-earlier snapshot, then recycle
        // those records. Because a client acks the highest sequence it has fully received, acking S implies
        // every sequence <= S that the client got; any earlier sequence it never received simply leaves its
        // entities unacked here, so they re-send next tick (self-healing). We use the max revision so an
        // out-of-order ack can't lower a baseline.
        var writeIndex = 0;
        for (var readIndex = 0; readIndex < _pendingSnapshots.Count; readIndex++)
        {
            var record = _pendingSnapshots[readIndex];
            if (record.Sequence <= snapshotSequence)
            {
                foreach (var carried in record.Carried)
                {
                    if (!_ackedEntityRevisions.TryGetValue(carried.NetworkId, out var existing)
                        || carried.Revision > existing)
                    {
                        _ackedEntityRevisions[carried.NetworkId] = carried.Revision;
                    }
                }

                _pendingSnapshotPool.Push(record);
            }
            else
            {
                _pendingSnapshots[writeIndex++] = record;
            }
        }

        _pendingSnapshots.RemoveRange(writeIndex, _pendingSnapshots.Count - writeIndex);
        _oldestUnackedSentTick = _pendingSnapshots.Count == 0 ? 0u : _pendingSnapshots[0].SentTick;
    }

    // Number of distinct snapshot sequences sent but not yet acked. The safety bound (GameServer) uses the
    // age of the oldest unacked snapshot, not this count, but it is exposed for diagnostics/tests.
    public int PendingSnapshotCount => _pendingSnapshots.Count;

    // Ticks since the oldest still-unacked snapshot was sent, or 0 when nothing is outstanding. The server
    // uses this for the cheap RE-BASELINE bound. Reset by ForceFullRebaseline.
    public uint UnackedAgeTicks(uint serverTick)
    {
        return _oldestUnackedSentTick == 0 ? 0u : serverTick - _oldestUnackedSentTick;
    }

    // Ticks since the client last acked anything (or since its first snapshot, if it has never acked), or 0
    // before the first snapshot was ever sent. Drives the DISCONNECT bound and, unlike UnackedAgeTicks, is
    // NOT reset by a forced re-baseline — so a client that is repeatedly re-baselined but never acks is
    // still dropped once total silence exceeds the larger threshold.
    public uint SilenceTicks(uint serverTick)
    {
        return _lastAckOrFirstSendTick == 0 ? 0u : serverTick - _lastAckOrFirstSendTick;
    }

    // Forces a fresh full re-baseline for this viewer: clears the acked baseline (so every visible entity
    // is re-sent as a complete snapshot) and drops all pending per-seq records (recycling their buffers).
    // Used by the safety bound when a client has gone silent past the re-baseline threshold.
    public void ForceFullRebaseline()
    {
        _ackedEntityRevisions.Clear();
        foreach (var record in _pendingSnapshots)
        {
            _pendingSnapshotPool.Push(record);
        }

        _pendingSnapshots.Clear();
        _oldestUnackedSentTick = 0;
    }

    // A per-seq carried-entities record: which entities a given outgoing snapshot delivered, at which
    // revision, plus the tick it was sent (for the safety-bound age). Reused via the session's pool.
    public sealed class PendingSnapshotRecord
    {
        public uint Sequence { get; private set; }
        public uint SentTick { get; private set; }
        public List<CarriedEntity> Carried { get; } = [];

        public void Reset(uint sequence, uint sentTick)
        {
            Sequence = sequence;
            SentTick = sentTick;
            Carried.Clear();
        }

        public void Add(uint networkId, uint revision)
        {
            Carried.Add(new CarriedEntity(networkId, revision));
        }

        // Drops an entity from this record's carried set (AOI exit), so a later ack of this snapshot will
        // not re-establish a stale acked revision for it. Swap-remove: order is irrelevant.
        public void Remove(uint networkId)
        {
            for (var i = Carried.Count - 1; i >= 0; i--)
            {
                if (Carried[i].NetworkId == networkId)
                {
                    Carried[i] = Carried[^1];
                    Carried.RemoveAt(Carried.Count - 1);
                }
            }
        }
    }

    public readonly record struct CarriedEntity(uint NetworkId, uint Revision);

}
