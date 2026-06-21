using LiteNetLib;
using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class ClientSession
{
    private readonly HashSet<uint> _lastSnapshotEntityIds = [];
    private readonly HashSet<uint> _knownEntityIds = [];

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

    // Held-direction movement intent (protocol v15). The client declares Moving + Direction; the tick
    // loop steps the entity at its own cooldown cadence while MoveIntentMoving is true. LastMoveSeq
    // rejects stale intents; LastMoveIntentTick drives the keepalive safety timeout. See
    // docs/movement-input-model.md.
    private uint _lastMoveSeq;
    private uint _lastMoveIntentTick;

    // NET6: StepCommit gets its OWN dedup cursor, separate from the MoveInput-intent cursor (_lastMoveSeq).
    // The client mints both commit seqs and intent seqs off one shared monotonic counter, so a keyup STOP
    // intent (seq N+1) carries a HIGHER seq than an unconfirmed tail commit (seq N). When both streams shared
    // ONE cursor, that stop intent burned the cursor past commit N, and every re-send of commit N was then
    // deduped away ("already seen") before it could reach the (healthy) authored-tick commit gate — stranding
    // the tail and forcing a ForceResync snap, and stranding mid-stream commits behind interleaved direction-
    // change intents (the runaway). Splitting the cursor lets a stranded commit re-send actually land: the
    // commit gate is `seq > _lastCommitSeq`, independent of how far intents have advanced _lastMoveSeq. Commit
    // seqs may have GAPS where intents took numbers — fine: the redundancy window applies entries oldest-first
    // and advances _lastCommitSeq incrementally, and a stale/duplicate commit is still rejected on `<=`.
    private uint _lastCommitSeq;

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

    // Current held movement intent. When MoveIntentMoving is true the tick loop attempts one cooldown-
    // gated tile step in MoveIntentDirection per tick. The last seq we accepted and the tick at which we
    // last heard a (fresh) intent are exposed for the keepalive timeout.
    public bool MoveIntentMoving { get; private set; }
    public Direction8 MoveIntentDirection { get; private set; }
    public uint LastMoveIntentTick => _lastMoveIntentTick;
    public uint LastMoveSeq => _lastMoveSeq;

    // NET6: the commit-stream dedup cursor (the highest StepCommit seq accepted). Separate from LastMoveSeq so
    // an intent never burns the commit cursor (and vice-versa). HandleStepCommitBatch/ExtractFreshStepCommits
    // gate the commit window on THIS, not LastMoveSeq.
    public uint LastCommitSeq => _lastCommitSeq;

    // UO1: when true this session drives its own movement UO-style — it predicts + banks tiles locally and sends
    // one StepCommitRequest per accepted step, so the tick loop must NOT auto-pace its entity from the held
    // MoveIntent (StepHeldMovementIntents skips it). Set false (the default) the session is server-paced exactly
    // as before. Toggled by the client's MovementModeMessage. The held MoveIntent is still recorded (for
    // stop/keepalive/facing) — it is just ignored for PACING while this is set. Default false keeps the
    // server-paced model byte-for-byte until a client opts in.
    public bool ClientDrivenMovement { get; private set; }

    // UO1: applies a MovementMode declaration from the client (true = client-driven, false = server-paced).
    public void SetClientDrivenMovement(bool clientDriven)
    {
        ClientDrivenMovement = clientDriven;
    }

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

    // Applies an inbound MoveIntent. Rejects stale sequences (seq <= lastSeq) and returns false without
    // mutating state; otherwise records the new intent + the tick it arrived (for the keepalive timeout)
    // and returns true. A fresh keepalive carrying the same Moving/Direction still refreshes the timeout.
    public bool TryUpdateMoveIntent(uint sequence, bool moving, Direction8 direction, uint serverTick)
    {
        if (sequence <= _lastMoveSeq)
        {
            return false;
        }

        _lastMoveSeq = sequence;
        _lastMoveIntentTick = serverTick;
        MoveIntentMoving = moving;
        MoveIntentDirection = direction;
        return true;
    }

    // S103 commit-step: advances the COMMIT-sequence cursor (_lastCommitSeq, NET6 — separate from the intent
    // cursor _lastMoveSeq) for a StepCommitRequest without touching the held Moving/Direction intent (a commit
    // is a one-shot "finish this step" request, not a new held intent — it must not, for example, set the
    // session back to Moving after a keyup). Rejects stale sequences (seq <= the COMMIT cursor) so a re-ordered/
    // duplicate commit can't fire twice, but does NOT consult or advance _lastMoveSeq — so a higher-numbered
    // STOP/direction-change intent can no longer burn an unconfirmed commit's seq and strand it (the NET6 bug).
    // Still refreshes the keepalive tick (preserving prior behavior: a fresh commit, like a fresh intent, proves
    // the client alive and pushes back the keepalive safety timeout). Returns true iff the sequence was fresh on
    // the commit cursor (the caller may then attempt the commit).
    public bool TryConsumeCommitSequence(uint sequence, uint serverTick)
    {
        if (sequence <= _lastCommitSeq)
        {
            return false;
        }

        _lastCommitSeq = sequence;
        _lastMoveIntentTick = serverTick;
        return true;
    }

    // Clears the held intent to stopped (keepalive safety timeout, or any server-side halt). Does not
    // touch the sequence cursor, so a later genuine intent still has to advance past the last accepted
    // seq.
    public void ClearMoveIntent()
    {
        MoveIntentMoving = false;
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
