using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class WorldEntity
{
    // The earliest server tick at which this entity's next movement step may fire. Null = never acted, so the
    // first step is always eligible. An ACCEPTED step sets it to serverTick + the full step cooldown; a step
    // BLOCKED at a wall advances it by one tick (the cooldown is not consumed) so a held-into-a-wall intent
    // re-tests next tick. This single field replaces the old _lastStepTick gate: storing the next-eligible tick
    // directly (rather than backdating _lastStepTick by the cooldown) is underflow-safe near tick 0 and keeps
    // the predictor mirror trivial. (S98: turn-then-move removed — a direction change now steps immediately,
    // facing set on the step; there is no separate turn beat or turn delay.)
    private uint? _nextEligibleTick;

    // S103 commit-step: the server tick of this entity's last ACCEPTED tile move (set in TryStep/TryCommitStep on
    // accept). Null = never stepped. The commit-step anti-cheat floor measures "elapsed into the current step" as
    // serverTick - _lastStepTick and accepts a commit only once that elapsed is at least CommitAcceptFraction of
    // the cooldown — so a scripted client cannot use early commits to step faster than the normal cadence. Stored
    // directly (rather than re-derived from _nextEligibleTick - cooldown) so a mid-step cadence change can't skew
    // the elapsed measurement.
    private uint? _lastStepTick;

    // NET3 authored-tick commit scheduling: the AUTHORED tick of this entity's last ACCEPTED UoClientDriven commit
    // (TryCommitStepAuthored). Null = no authored commit accepted yet. The anti-speedhack SPACING gate is keyed on
    // authored ticks: a new commit's authored tick must be >= this prior accepted authored tick + the step cooldown,
    // so a client cannot claim steps closer together than cadence regardless of when the packets arrive (the bundled
    // [C2,C3] recovery is in-order and a cadence apart, so it passes; a spam burst at the same authored tick does
    // not). Distinct from _lastStepTick (which is a RECEIVE-time field the S103 floor uses) so the two paths don't
    // skew each other.
    private uint? _lastAuthoredCommitTick;

    public WorldEntity(
        ulong id,
        uint networkId,
        EntityKind kind,
        TileCoord tile,
        Direction8 facing,
        string displayName,
        Guid? characterId,
        ClientSession? ownerSession,
        bool isDurable,
        Inventory? inventory = null,
        ResourceNode? resource = null)
    {
        Id = id;
        NetworkId = networkId;
        Kind = kind;
        Tile = tile;
        Facing = facing;
        DisplayName = displayName;
        CharacterId = characterId;
        OwnerSession = ownerSession;
        IsDurable = isDurable;
        Inventory = inventory;
        Resource = resource;
    }

    public ulong Id { get; }
    public uint NetworkId { get; }
    public EntityKind Kind { get; }
    public TileCoord Tile { get; private set; }
    public Direction8 Facing { get; private set; }
    public string DisplayName { get; }
    public Guid? CharacterId { get; }
    public ClientSession? OwnerSession { get; }
    public bool IsDurable { get; }

    // Per-entity movement-speed stat. 1.0 (the default) means "move at the server's base step cadence",
    // identical to pre-S51 behaviour. >1 = faster (shorter effective cooldown), <1 = slower. The
    // effective per-step cooldown is derived from this in EffectiveStepCooldownTicks/Ms and clamped, so a
    // silly multiplier can never break the tick loop. Speed buffs/slows/mounts later just set this.
    public double SpeedMultiplier { get; private set; } = 1.0;

    // Sets the speed multiplier, guarding against non-positive / non-finite values (which would otherwise
    // produce a zero or NaN cooldown). Returns true if the value actually changed, so the caller only
    // re-replicates the cadence when it really moved.
    public bool TrySetSpeedMultiplier(double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier <= 0)
        {
            return false;
        }

        if (multiplier == SpeedMultiplier)
        {
            return false;
        }

        SpeedMultiplier = multiplier;
        return true;
    }

    // Derives this entity's effective per-step cooldown in TICKS from the server's base cooldown and the
    // speed multiplier, clamped to the configured [minTicks, maxTicks] tick bounds (mirrors the ms clamp).
    // Default multiplier 1.0 returns baseStepCooldownTicks unchanged ⇒ behaviour parity with pre-S51. The
    // clamp guarantees the tick loop always advances (>= 1 tick) regardless of how extreme the multiplier is.
    public uint EffectiveStepCooldownTicks(uint baseStepCooldownTicks, uint minTicks, uint maxTicks)
    {
        var scaled = baseStepCooldownTicks / SpeedMultiplier;
        // Round to the nearest tick; never below 1 (a zero cooldown would let an entity step every tick and
        // is meaningless on a tick-quantised loop).
        var ticks = (long)Math.Max(1, Math.Round(scaled, MidpointRounding.AwayFromZero));
        return (uint)Math.Clamp(ticks, (long)minTicks, (long)maxTicks);
    }

    // Durable per-character inventory (server-memory truth, write-behind persisted). Present only on
    // durable player entities; null for transient/world entities.
    public Inventory? Inventory { get; }

    // Transient resource-node state (available/depleted + respawn timer). Present only on
    // EntityKind.Resource entities that are harvestable; null for players and the legacy placeholder.
    public ResourceNode? Resource { get; }

    // Replicated availability bit that rides EntityStateSnapshot. False (the default) for everything
    // that is not a harvestable resource node, so the snapshot path stays uniform.
    public bool IsDepleted => Resource is { IsAvailable: false };

    // The tick this node is scheduled to respawn at. 0 for non-resource entities. Lets the server queue
    // a depleted node by its respawn time rather than rescanning all nodes each tick.
    public uint ResourceRespawnAtTick => Resource?.RespawnAtTick ?? 0;

    public uint StateRevision { get; private set; } = 1;

    // S76: a per-entity counter of ACCEPTED tile moves only — it bumps exactly when Tile actually advances
    // (the accepted-step branch in TryStep), and NOT on a turn or a blocked/cooldown step. This is distinct
    // from StateRevision (which also bumps on turns and resource state changes): StepSequence counts the same
    // events the client predictor's _predictedTile advances on, so Stage 2 (S77) can match a snapshot confirm
    // to the predicted step it corresponds to. Emitted on the wire as the recipient-scoped RecipientStepSeq;
    // this stage only puts it on the wire (no reconcile change).
    public uint StepSequence { get; private set; }

    // DIAG1 (measurement only): per-entity commit-path counters so the server side of the 3-link recovery chain is
    // observable. RecvCommits = commit attempts that reached this entity's gate (a RECOVERED lost commit counts —
    // it was never consumed; a true duplicate already deduped upstream does NOT) — climbing while StepSequence
    // (srvSeq) stalls means the server is REJECTING delivered commits (link 2), not failing to receive them (link 1). RejectsCommitTooEarly = commits refused by the authored-tick future-cap /
    // receive-time cooldown floor ("commit_too_early") — the anti-speedhack gate the recovery hypothesis suspects.
    // RejectsBlocked = commits refused because the target tile was a wall / out of bounds. Bumped inside the commit
    // methods below; pure tallies that change NO movement decision. (StepSequence already exposes srvSeq.)
    public uint RecvCommits { get; private set; }
    public uint RejectsCommitTooEarly { get; private set; }
    public uint RejectsBlocked { get; private set; }

    // Harvests the node: marks it depleted, schedules respawn, and bumps StateRevision so the change
    // re-replicates through the AOI snapshot delta path. Caller must have validated availability.
    public void DepleteResource(uint serverTick)
    {
        if (Resource is null)
        {
            return;
        }

        Resource.Deplete(serverTick);
        StateRevision++;
    }

    // Returns this node to Available if its respawn time has arrived, bumping StateRevision so the
    // refreshed availability re-replicates. No-op (returns false) for non-resource or still-depleted
    // entities.
    public bool TryRespawnResource(uint serverTick)
    {
        if (Resource is null || !Resource.TryRespawn(serverTick))
        {
            return false;
        }

        StateRevision++;
        return true;
    }

    public bool TryStep(Direction8 direction, uint serverTick, uint stepCooldownTicks, TileGrid grid)
    {
        return TryStep(direction, serverTick, stepCooldownTicks, grid, out _);
    }

    public bool TryStep(
        Direction8 direction,
        uint serverTick,
        uint stepCooldownTicks,
        TileGrid grid,
        out MovementStepResult result)
    {
        // Gate on the next-eligible tick (set by the previous step). A step that arrives early — before its
        // cooldown has elapsed — is dropped, unchanged from the pre-S63 cooldown behaviour for accepted steps.
        if (_nextEligibleTick.HasValue && serverTick < _nextEligibleTick.Value)
        {
            var cooldownDelta = direction.Delta();
            var cooldownTarget = Tile.Offset(cooldownDelta.X, cooldownDelta.Y);
            result = new MovementStepResult(
                direction,
                Tile,
                cooldownTarget,
                CooldownElapsed: false,
                grid.IsWalkable(cooldownTarget),
                Accepted: false,
                "cooldown",
                Tile);
            return false;
        }

        // S98: a direction change steps IMMEDIATELY in the new direction — there is no separate turn beat. The
        // step itself faces you, so set Facing unconditionally up front. Capture whether the facing actually
        // changed so a blocked-into-a-wall step (no tile move) can STILL bump StateRevision and replicate the
        // new facing (the Cato sprite flip depends on it).
        var facingChanged = Facing != direction;
        Facing = direction;

        var delta = direction.Delta();
        var target = Tile.Offset(delta.X, delta.Y);
        // S75: reject diagonal corner-cutting. A diagonal step (both axes non-zero) also slices between the two
        // orthogonally-adjacent tiles it passes; if EITHER of those side tiles is blocked, the move would slip
        // diagonally THROUGH a wall corner. So a diagonal is walkable only when the destination AND both side
        // tiles ((Tile.X+dx, Tile.Y) and (Tile.X, Tile.Y+dy)) are walkable. Cardinal steps (one axis zero) are
        // unchanged: only the destination matters. The client predictor (LocalPlayerPredictor.Tick) applies the
        // IDENTICAL rule via its walkability oracle so prediction still mirrors the server exactly.
        if (!IsStepWalkable(delta, target, grid))
        {
            // Blocked at a wall: HOLD in place (no tile move). The cooldown is NOT consumed (_nextEligibleTick
            // is left where it is — already <= serverTick — so the held intent re-tests next tick), unchanged
            // from the pre-S98 blocked behaviour. But if this blocked step also CHANGED facing (a direction
            // change into a wall), bump StateRevision so the new facing replicates even though the tile didn't
            // move (S98 — the Cato sprite flip on a press-into-a-wall depends on this). A repeated press into the
            // same wall (no facing change) bumps nothing, so it does not spam snapshot deltas.
            if (facingChanged)
            {
                StateRevision++;
            }

            result = new MovementStepResult(
                direction,
                Tile,
                target,
                CooldownElapsed: true,
                TargetWalkable: false,
                Accepted: false,
                grid.IsInBounds(target) ? "blocked" : "out_of_bounds",
                Tile);
            return false;
        }

        var from = Tile;
        Tile = target;
        _lastStepTick = serverTick;
        _nextEligibleTick = serverTick + stepCooldownTicks;
        StateRevision++;
        // S76: count this accepted tile move. ONLY here — blocked/cooldown steps above return early without
        // touching StepSequence, so it advances in lockstep with the actual tile.
        StepSequence++;
        result = new MovementStepResult(
            direction,
            from,
            target,
            CooldownElapsed: true,
            TargetWalkable: true,
            Accepted: true,
            "accepted",
            Tile);
        return true;
    }

    // S103 commit-step on release. A client whose model-B cosmetic render has glided past its commit threshold onto
    // the next tile at key-release asks the server to finish that ONE step early instead of snapping back. This is a
    // server-validated single step in `direction` with the SAME walkability gate as TryStep PLUS an anti-cheat
    // floor, and it is the ONLY way an entity can step before its cooldown fully elapses:
    //
    //   * Walkable-gate identically to TryStep (S75 corner-cut rule) — a commit into a wall / out of bounds is
    //     rejected and changes nothing.
    //   * Anti-cheat floor: accept ONLY IF the entity is at least `acceptFraction` of its cooldown into the current
    //     step, i.e. elapsed = serverTick - _lastStepTick >= acceptFraction * stepCooldownTicks. A never-stepped
    //     entity (no _lastStepTick) is treated as fully elapsed (first move is always eligible, like TryStep).
    //   * No-speedhack borrow: on accept the early finish CONSUMES the current step's remaining cooldown — it does
    //     NOT gain time. The committed step is scheduled as if it had landed at its NOMINAL end (the tick the
    //     current step's cooldown would have elapsed = _lastStepTick + cooldown), so the next step's clock starts
    //     from there: _lastStepTick = nominalEnd and _nextEligibleTick = nominalEnd + cooldown. Thus the average
    //     step rate can NEVER exceed the normal cadence (you finished one step a little early on screen, but the
    //     NEXT step is no earlier than it would have been). A commit that arrives at/after the nominal end is just a
    //     normal on-time step (scheduled from serverTick). This is what makes the held-intent model anti-speedhack:
    //     spamming release-commits cannot raise the long-run step rate above one per cooldown.
    //
    // NOTE (deviation from the literal task text): the task wrote `_nextEligibleTick = commitTick + cooldown`, but
    // that formula does NOT cap the rate — chaining commits at the acceptFraction floor would yield ~1/(fraction)×
    // cadence (e.g. 2× at 0.5). The task's STATED guarantee ("average step rate can never exceed the normal
    // cadence") and its required cadence-cap test win, so the borrow is scheduled from the nominal step end (above)
    // which honours that guarantee exactly. Flagged in the S103 review request.
    //
    // Accept advances the tile + StepSequence + StateRevision exactly like a normal accepted step (so it replicates
    // and the recipient-scoped RecipientStepSeq bumps, which the client reconciles against). There is no dedicated
    // reply — the next snapshot showing the advanced tile is the accept signal; staying put is the reject signal.
    public bool TryCommitStep(
        Direction8 direction,
        uint serverTick,
        uint stepCooldownTicks,
        double acceptFraction,
        TileGrid grid,
        out MovementStepResult result)
    {
        // DIAG1: count every received commit attempt before any gate (see TryCommitStepAuthored). Measurement only.
        RecvCommits++;

        var delta = direction.Delta();
        var target = Tile.Offset(delta.X, delta.Y);

        // Walkability gate first (same rule as TryStep). A commit into a wall / out of bounds changes nothing —
        // not even facing (a commit is a render-completion request, not a fresh direction input; the held-intent
        // path already owns facing). The reject leaves the entity on its current tile, which the snapshot shows.
        if (!IsStepWalkable(delta, target, grid))
        {
            RejectsBlocked++; // DIAG1.
            result = new MovementStepResult(
                direction,
                Tile,
                target,
                CooldownElapsed: true,
                TargetWalkable: false,
                Accepted: false,
                grid.IsInBounds(target) ? "blocked" : "out_of_bounds",
                Tile);
            return false;
        }

        // Anti-cheat floor: the entity must be at least acceptFraction of its cooldown into the current step. A
        // never-stepped entity has no last step, so its first commit is always eligible (elapsed treated as
        // infinite). A commit that arrives too early — a scripted spam below the floor — is rejected so commits
        // can't raise the step rate above cadence.
        if (_lastStepTick.HasValue)
        {
            // _lastStepTick can be a FUTURE tick after a borrowed commit (it is scheduled from the nominal step
            // end). A commit arriving at or before that base has zero/negative elapsed — reject (and avoid the
            // uint underflow that serverTick - _lastStepTick would otherwise produce). Otherwise compare the
            // elapsed-since-base against the accept fraction.
            var floor = acceptFraction * stepCooldownTicks;
            var elapsedEnough = serverTick > _lastStepTick.Value
                && (serverTick - _lastStepTick.Value) >= floor;
            if (!elapsedEnough)
            {
                RejectsCommitTooEarly++; // DIAG1.
                result = new MovementStepResult(
                    direction,
                    Tile,
                    target,
                    CooldownElapsed: false,
                    TargetWalkable: true,
                    Accepted: false,
                    "commit_too_early",
                    Tile);
                return false;
            }
        }

        var from = Tile;
        Tile = target;
        Facing = direction;
        // Schedule the committed step from its NOMINAL end so the early finish consumes the current step's remaining
        // cooldown rather than gaining time (the no-speedhack cap). nominalEnd = the tick the current step's cooldown
        // would have elapsed = _lastStepTick + cooldown (which is exactly the old _nextEligibleTick). If the commit
        // arrives at/after that nominal end (or the entity never stepped), it is just an on-time step scheduled from
        // serverTick.
        var nominalEnd = _lastStepTick.HasValue ? _lastStepTick.Value + stepCooldownTicks : serverTick;
        var scheduleBase = nominalEnd > serverTick ? nominalEnd : serverTick;
        _lastStepTick = scheduleBase;
        _nextEligibleTick = scheduleBase + stepCooldownTicks;
        StateRevision++;
        StepSequence++;
        result = new MovementStepResult(
            direction,
            from,
            target,
            CooldownElapsed: true,
            TargetWalkable: true,
            Accepted: true,
            "committed",
            Tile);
        return true;
    }

    // NET3 — apply a UoClientDriven commit at its AUTHORED tick (the references' "process the command at its own
    // timing", not at receive time). This is the loss-desync fix: NET2's redundant window recovers a dropped commit,
    // but it arrives BUNDLED with the next ([C2,C3] in one packet). The old TryCommitStep gated the cooldown on the
    // RECEIVE tick, so both landed at the same tick → C2 accepted, C3 rejected "too early" → never confirmed →
    // prediction stays ahead → desync. Here the cooldown SCHEDULE is keyed on the AUTHORED tick instead:
    //
    //   * authoredTick is the integer server tick the CLIENT's predictor banked the step on (carried on the wire,
    //     NET3). It is clamped UP to a recent window floor [serverTick - pastWindow] so a far-past tick (a very stale
    //     recovered commit, or tamper) can't rewind the schedule arbitrarily into the past.
    //   * PACING (anti-speedhack): the commit is scheduled at scheduled = max(clampedAuthored, prior + cooldown) —
    //     never closer than a full cooldown after the prior accepted commit. So a same-tick spam BURST is SERIALISED
    //     to cadence (each clamped up to the prior's nominal end) rather than all landing at once; a client cannot
    //     claim steps closer than cadence. The in-order redundant window delivers recovered commits a cadence apart,
    //     so a bundle is already spaced and each lands at its own authored tick — exactly the [C2,C3] the receive-
    //     time gate rejected. A reorder/dup (authored < prior) is paced up to the next slot too — no rollback (full
    //     reorder rollback is Stage 4); the window normally prevents reorders anyway.
    //   * REAL-TIME CAP: the scheduled tick must not exceed serverTick + futureLead — the schedule can NEVER run
    //     ahead of real time by more than the small in-flight lead the predictor legitimately has. A serialised spam
    //     burst whose paced slot would land in the future is REJECTED ("too early") and re-tries on a later tick once
    //     real time catches up. This is what bounds the rate to cadence in real time (a 100-deep same-tick burst
    //     cannot teleport the entity 100 tiles in one tick — only futureLead-worth land, the rest wait).
    //   * On accept the schedule anchor advances to the scheduled tick; tile/StepSequence advance through the SAME
    //     body as a normal step.
    //
    // Walkability is the identical S75 corner-cut gate as TryStep/TryCommitStep. A blocked authored commit holds in
    // place WITHOUT consuming the authored schedule (mirroring TryStep's wall-hold) so a later commit re-tests.
    public bool TryCommitStepAuthored(
        Direction8 direction,
        uint authoredTick,
        uint serverTick,
        uint stepCooldownTicks,
        uint pastWindowTicks,
        uint futureLeadTicks,
        TileGrid grid,
        out MovementStepResult result)
    {
        // DIAG1: count every received commit attempt (incl. redundant re-sends) before any gate, so a climbing
        // RecvCommits with a stalled StepSequence cleanly separates link-2 (server rejecting) from link-1 (not
        // receiving). Measurement only.
        RecvCommits++;

        // Clamp the authored tick up to a recent window floor so a far-past (stale / tamper) tick can't rewind the
        // schedule arbitrarily. A far-future tick needs no separate clamp here — the real-time cap below rejects a
        // scheduled tick beyond serverTick + futureLead.
        var windowFloor = serverTick > pastWindowTicks ? serverTick - pastWindowTicks : 0u;
        var clampedAuthored = authoredTick < windowFloor ? windowFloor : authoredTick;

        // PACE: never schedule closer than a full cooldown after the prior accepted commit. A bundle that is already
        // a cadence apart keeps its own authored ticks; a too-close / same-tick / reorder commit is paced up to the
        // prior's nominal next slot (serialising a spam burst to cadence rather than dropping it).
        var scheduledTick = clampedAuthored;
        if (_lastAuthoredCommitTick.HasValue)
        {
            var nominalNext = _lastAuthoredCommitTick.Value + stepCooldownTicks;
            if (scheduledTick < nominalNext)
            {
                scheduledTick = nominalNext;
            }
        }

        // REAL-TIME CAP: the schedule can never run ahead of real time by more than the small in-flight lead. If the
        // paced slot is beyond serverTick + futureLead, reject "too early" — the client is trying to claim steps
        // faster than real time allows (a serialised spam burst, or a far-future authored tick). It re-tries on a
        // later tick once real time advances, so the long-run rate is bounded to cadence and a burst can't teleport.
        if (scheduledTick > serverTick + futureLeadTicks)
        {
            // DIAG1: this is the link-2 reject the recovery hypothesis suspects (the future-cap refusing a
            // delivered commit). Count it so the trace shows rejects climbing while StepSequence stalls.
            RejectsCommitTooEarly++;
            result = new MovementStepResult(
                direction,
                Tile,
                Tile,
                CooldownElapsed: false,
                TargetWalkable: true,
                Accepted: false,
                "commit_too_early",
                Tile);
            return false;
        }

        var delta = direction.Delta();
        var target = Tile.Offset(delta.X, delta.Y);

        // Walkability gate (same S75 corner-cut rule as TryStep). A commit into a wall / out of bounds changes
        // nothing and does NOT advance the authored schedule, so a later commit in an open direction still applies.
        if (!IsStepWalkable(delta, target, grid))
        {
            RejectsBlocked++; // DIAG1.
            result = new MovementStepResult(
                direction,
                Tile,
                target,
                CooldownElapsed: true,
                TargetWalkable: false,
                Accepted: false,
                grid.IsInBounds(target) ? "blocked" : "out_of_bounds",
                Tile);
            return false;
        }

        var from = Tile;
        Tile = target;
        Facing = direction;
        // Advance the authored-schedule anchor to the scheduled (paced) tick. The next commit must be scheduled at
        // least a cooldown later. _lastStepTick / _nextEligibleTick are kept coherent off the SAME anchor so a later
        // S103-style receive-time commit or a switch back to server-paced reads a sane base rather than a stale one.
        _lastAuthoredCommitTick = scheduledTick;
        _lastStepTick = scheduledTick;
        _nextEligibleTick = scheduledTick + stepCooldownTicks;
        StateRevision++;
        StepSequence++;
        result = new MovementStepResult(
            direction,
            from,
            target,
            CooldownElapsed: true,
            TargetWalkable: true,
            Accepted: true,
            "committed",
            Tile);
        return true;
    }

    // S75: walkability of a step from the current tile, with diagonal corner-cutting rejected. The destination
    // must always be walkable. For a DIAGONAL step (both delta axes non-zero) the two orthogonally-adjacent
    // tiles it cuts between must ALSO be walkable, so the entity can't slip diagonally through a wall corner.
    // Cardinal steps (one axis zero) check the destination only. The client predictor mirrors this rule exactly
    // (LocalPlayerPredictor) so server and prediction reject the identical set of diagonal steps.
    private bool IsStepWalkable(TileCoord delta, TileCoord target, TileGrid grid)
    {
        if (!grid.IsWalkable(target))
        {
            return false;
        }

        if (delta.X != 0 && delta.Y != 0)
        {
            return grid.IsWalkable(Tile.Offset(delta.X, 0)) && grid.IsWalkable(Tile.Offset(0, delta.Y));
        }

        return true;
    }
}
