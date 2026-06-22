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

    // COMBAT-S2B: the earliest server tick at which this entity's next ATTACK may fire. Null = never attacked, so
    // the first attack is always eligible. INDEPENDENT of the movement cooldown (_nextEligibleTick) — an attack and
    // a move pace on separate clocks, exactly as the task requires (a move never arms the attack cooldown and
    // vice-versa). An accepted attack sets it to serverTick + the attack cooldown; a rejected (on-cooldown) attack
    // leaves it untouched. Modelled the same way as _nextEligibleTick so the gate is underflow-safe near tick 0.
    private uint? _nextEligibleAttackTick;

    // SWING-SLOW: the swing-slow WINDOW and its factor, the replacement for the old hard movement root. A swing
    // opens a window [anchorTick, _swingSlowUntilTick) during which movement is SLOWED (not frozen) by
    // _swingMoveFactor in [0,1]: a step that lands inside the window costs a LONGER cooldown
    // (CombatTuning.SlowedStepCooldownTicks), so the entity moves at `factor` of its normal speed. factor == 0 is
    // the full-stop special case — a step inside the window is BLOCKED, reproducing the old root exactly. The
    // window end is anchored on the CLAMPED AUTHORED tick (same anchor the old root used) so the predictor mirrors
    // it byte-identically. _swingSlowUntilTick == 0 with no swing applied means "no active window" (a real
    // swing always ends at anchor + >= 1 tick). The predictor holds the SAME two fields and the same window/cooldown
    // math (LocalPlayerPredictor) — this is the swing-slow parity point.
    private uint _swingSlowUntilTick;
    private double _swingMoveFactor = CombatTuning.DefaultSwingMoveFactor;

    // SWING-SLOW: the BASE per-step cooldown the last accepted step was scheduled with (before any swing-slow). The
    // retroactive-slow fix in ApplySwingSlowWindow needs it to re-slow a step accepted at base just before the
    // server learned of the swing — so it computes the SAME slowed cooldown the predictor computed for that step.
    // Defaults to 1 (harmless until the first step sets it). Tracked on every accepted step alongside _lastStepTick.
    private uint _lastStepCooldownTicks = 1;

    // SWING-SLOW (commit-path parity fix): the AUTHORED tick of the last accepted step — the EXACT mirror of the
    // predictor's LocalPlayerPredictor._lastLiveStepTick (its gate/action tick). This is the value the retroactive
    // slow keys on, NOT _lastStepTick. The two FIELDS differ on the UoClientDriven authored-commit path when the
    // commit is PACED: TryCommitStepAuthored sets _lastStepTick = scheduledTick (the paced/clamped tick the step is
    // applied at), but the predictor records its pre-pacing gate tick. A recovered/bundled/reordered commit that the
    // server paces up to _lastAuthoredCommitTick + cooldown makes scheduledTick > the authored tick, so keying the
    // retroactive slow on _lastStepTick diverges from the predictor (server↔predictor next-eligible mismatch → the
    // slow-form rubberband). Keying on the AUTHORED tick — which both sides compute identically (the predictor's
    // actionTick == the clamped authoredTick the server applies at realistic latency) — makes the retroactive-slow
    // inputs (lastStepAuthoredTick, _lastStepCooldownTicks) byte-identical on both sides in the paced case.
    //
    // On the held-intent (TryStep) and S103 (TryCommitStep) paths the authored tick == _lastStepTick (TryStep stamps
    // serverTick == the predictor's actionTick; TryCommitStep is not driven by the predictor's live step loop), so
    // this is exactly _lastStepTick there and nothing changes for those paths. Null = never stepped, like _lastStepTick.
    private uint? _lastStepAuthoredTick;

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

    // COMBAT-S1: server-authoritative character vitals (HP / mana / stamina, each current + max). Defaults to
    // full 100/100 each on spawn. No damage/regen/death yet — this stage only models them existing, being
    // dev-set (clamped to [0, max]), and replicated to the owning client. Mirrors the SpeedMultiplier pattern:
    // private setter, a Try* mutator that clamps + reports whether the value actually changed so the caller only
    // re-replicates on a real change.
    public CharacterStats Stats { get; private set; } = CharacterStats.Default;

    // Sets the CURRENT value of one vital, clamping into [0, max] for that vital. Returns true if the stored
    // value actually changed (so the caller only re-replicates a real change), false otherwise. The dev-set
    // window drives this through the admin-gated server command; later damage/heal/regen will too.
    public bool TrySetStatCurrent(StatKind stat, int value)
    {
        var updated = stat switch
        {
            StatKind.Health => Stats.WithHealth(value),
            StatKind.Mana => Stats.WithMana(value),
            StatKind.Stamina => Stats.WithStamina(value),
            _ => Stats,
        };

        if (updated == Stats)
        {
            return false;
        }

        Stats = updated;
        return true;
    }

    // COMBAT-S2B: applies `amount` of damage to current Health, clamping the result into [0, MaxHealth]. Returns
    // true iff the stored Health actually changed (so the caller only bumps replication / logs on a real hit),
    // false otherwise (already at 0, or a non-positive amount). Distinct semantic from the dev-set
    // TrySetStatCurrent (which sets an absolute value): this SUBTRACTS. HP may reach 0 (the overhead bar empties) —
    // there is NO death/despawn this stage (Stage 6); a 0-HP entity simply sits at 0. The reduced Health rides the
    // existing 2a public-HP snapshot field, so the change re-replicates and the overhead bar drops automatically.
    public bool ApplyDamage(int amount)
    {
        if (amount <= 0)
        {
            return false;
        }

        // Subtract from current Health and clamp; WithHealth already floors at 0 and caps at MaxHealth.
        var updated = Stats.WithHealth(Stats.Health - amount);
        if (updated == Stats)
        {
            return false;
        }

        Stats = updated;
        StateRevision++;
        return true;
    }

    // COMBAT-S2B: the attack-cooldown gate, INDEPENDENT of the movement cooldown. Returns true and arms the attack
    // cooldown (next eligible = serverTick + attackCooldownTicks) iff this entity is off its attack cooldown at
    // serverTick; returns false WITHOUT mutating anything if it is still inside the window (the attack is rejected,
    // it cannot be bypassed by spamming). Mirrors the movement next-eligible-tick gate but on its own field, so a
    // move never lets an attack through early and an attack never blocks a move.
    public bool TryBeginAttack(uint serverTick, uint attackCooldownTicks)
    {
        if (_nextEligibleAttackTick.HasValue && serverTick < _nextEligibleAttackTick.Value)
        {
            return false;
        }

        _nextEligibleAttackTick = serverTick + attackCooldownTicks;
        return true;
    }

    // SWING-COMMIT: briefly ROOT this entity's MOVEMENT after a committed swing. Pushes the next-eligible MOVEMENT
    // tick forward to at least serverTick + rootTicks, so the attacker cannot start a new step until the root
    // window elapses. This is the SAME machinery as the normal step cooldown (it bumps the same _nextEligibleTick
    // gate TryStep gates on), which is exactly why the client predictor can mirror it for free.
    //
    // It is a FLOOR, never a shortener: max(existing, serverTick + rootTicks). If the entity is already on a
    // LONGER movement cooldown (e.g. it just stepped, so _nextEligibleTick is further out), the root must not pull
    // it earlier — only extend it. Symmetrically the client predictor applies the identical max-floor against its
    // own next-eligible tick. Distinct from the ATTACK cooldown (TryBeginAttack / _nextEligibleAttackTick), which
    // this never touches — the root is a movement gate only, so a swing delays the next STEP, not the next attack.
    public void ApplyAttackMovementRoot(uint serverTick, uint rootTicks)
    {
        var rootUntil = serverTick + rootTicks;
        if (!_nextEligibleTick.HasValue || _nextEligibleTick.Value < rootUntil)
        {
            _nextEligibleTick = rootUntil;
        }
    }

    // SWING-COMMIT-FIX: root the attacker's movement anchored on the CLIENT's AUTHORED tick (carried on the wire),
    // not on the server's RECEIVE tick. This is the parity fix: the predictor roots its own movement at the same
    // authored tick (LocalPlayerPredictor.ApplyAttackMovementRootAt), so under latency the two sides compute the
    // IDENTICAL root window (max(existing, authoredTick + rootTicks)) instead of the server's window ending ~d ticks
    // later (its receive-tick anchor) than the predictor's — which let the predictor step where the server rejected
    // (the swing-then-move rubberband).
    //
    // The authored tick is CLAMPED to [serverTick - pastWindow, serverTick + futureLead] before use, EXACTLY like
    // TryCommitStepAuthored clamps its authored commit tick: a hostile/buggy client cannot root itself far in the
    // FUTURE (an authored tick way ahead would freeze its own movement for a long time — self-harm, but we still
    // bound it so it can't interact badly with the schedule) or in the far PAST (an ancient authored tick would make
    // the root a no-op, dodging the committed-swing penalty). At realistic latency the authored tick is within the
    // window, so the clamp is a no-op and the server's root window equals the predictor's exactly. Still a FLOOR
    // (max(existing, ...)) — it never shortens a longer existing movement cooldown.
    public void ApplyAttackMovementRootAuthored(
        uint authoredTick,
        uint serverTick,
        uint rootTicks,
        uint pastWindowTicks,
        uint futureLeadTicks)
    {
        var windowFloor = serverTick > pastWindowTicks ? serverTick - pastWindowTicks : 0u;
        var windowCeil = serverTick + futureLeadTicks;
        var clampedAuthored = authoredTick < windowFloor
            ? windowFloor
            : (authoredTick > windowCeil ? windowCeil : authoredTick);

        var rootUntil = clampedAuthored + rootTicks;
        if (!_nextEligibleTick.HasValue || _nextEligibleTick.Value < rootUntil)
        {
            _nextEligibleTick = rootUntil;
        }
    }

    // SWING-SLOW (the live path; replaces the old hard root): open a swing-slow WINDOW anchored on the CLIENT's
    // AUTHORED tick (clamped exactly like ApplyAttackMovementRootAuthored / TryCommitStepAuthored). During
    // [clampedAuthored, clampedAuthored + slowDurationTicks) movement is SLOWED by `factor` rather than frozen: a
    // step inside the window costs CombatTuning.SlowedStepCooldownTicks(base, factor). factor == 0 reproduces the
    // old root (a step inside the window is BLOCKED). The window is a FLOOR like the old root — a NEW swing only
    // EXTENDS an existing active window's end (max), it never shortens it, and the latest swing's factor wins (the
    // typical case: a single swing at a time). The predictor calls the same-shaped method (ApplyAttackMovementSlowAt)
    // with the SAME clamped authored tick and the SAME factor from the replicated tuning — byte-identical windows.
    public void ApplyAttackMovementSlowAuthored(
        uint authoredTick,
        uint serverTick,
        uint slowDurationTicks,
        double factor,
        uint pastWindowTicks,
        uint futureLeadTicks)
    {
        var windowFloor = serverTick > pastWindowTicks ? serverTick - pastWindowTicks : 0u;
        var windowCeil = serverTick + futureLeadTicks;
        var clampedAuthored = authoredTick < windowFloor
            ? windowFloor
            : (authoredTick > windowCeil ? windowCeil : authoredTick);

        ApplySwingSlowWindow(clampedAuthored, slowDurationTicks, factor);
    }

    // SWING-SLOW shared window-open: set the swing-slow window end (a max-FLOOR — never shortens an existing active
    // window) and capture the factor. Used by the server's authored path AND (mirror) the predictor (which holds the
    // SAME field set + the SAME retroactive fix). The factor of the most recent swing wins for the merged window.
    //
    // RETROACTIVE SLOW (the latency-parity fix — see LocalPlayerPredictor.ApplyAttackMovementSlowAt for the mirror):
    // the server learns of the swing `latency` ticks AFTER it was authored, so a step it ALREADY accepted at a tick
    // INSIDE the window [A, A+D) was scheduled with the BASE cooldown (the server didn't yet know to slow it). Left
    // alone, that step's next-eligible tick would be `lastStepAuthored + base`, while the predictor (which knew
    // immediately) scheduled `lastStepAuthored + slowed` — a divergence (the swing-then-move rubberband, in slow
    // form). So at window-open we RE-SLOW that pending step: if the last accepted step's AUTHORED tick landed inside
    // the window, push its next-eligible tick out to `lastStepAuthored + slowedCooldown` (a max-FLOOR, never earlier).
    // This depends only on (A, D, factor, _lastStepAuthoredTick, _lastStepCooldownTicks) — all identical on both sides
    // for that step — so it is order-independent: whether a side slows the step at accept-time (predictor, knew early)
    // or at window-open-time (server, learned late), both land on the SAME next-eligible tick. Anchoring on the
    // AUTHORED tick (not the paced/clamped scheduledTick _lastStepTick) is the COMMIT-PATH parity fix — see the inner
    // comment. A last step BEFORE the window needs no fix — it was correctly base-paced, and the FIRST step that falls
    // inside the window is slowed when IT is accepted.
    private void ApplySwingSlowWindow(uint anchorTick, uint slowDurationTicks, double factor)
    {
        var slowUntil = anchorTick + slowDurationTicks;
        if (slowUntil > _swingSlowUntilTick)
        {
            _swingSlowUntilTick = slowUntil;
        }

        _swingMoveFactor = factor;

        // RETROACTIVE SLOW: if the last accepted step landed inside the (just-merged) window, re-slow its pending
        // next-eligible tick. Skip for the block factor (0) — the block gate already withholds every step inside the
        // window, so there is no slowed-cadence step to reschedule (the old root semantics need no reschedule).
        //
        // COMMIT-PATH PARITY FIX: key on the AUTHORED tick of the last step (_lastStepAuthoredTick), the value the
        // predictor records as its _lastLiveStepTick — NOT on _lastStepTick (the paced/clamped scheduled tick the
        // commit was applied at). On the UoClientDriven authored-commit path a PACED commit makes _lastStepTick
        // (scheduledTick) > the authored tick, so keying on _lastStepTick would compute a DIFFERENT slowedNext than
        // the predictor (which keyed on its gate tick) → server↔predictor divergence (the slow-form rubberband). The
        // authored tick is byte-identical on both sides (predictor actionTick == clamped authoredTick at realistic
        // latency), so (lastStepAuthoredTick, _lastStepCooldownTicks) — and thus slowedNext — match exactly. On the
        // held-intent / S103 paths _lastStepAuthoredTick == _lastStepTick, so this is unchanged there.
        if (!CombatTuning.IsSwingBlockFactor(factor)
            && _lastStepAuthoredTick.HasValue
            && _lastStepAuthoredTick.Value >= anchorTick
            && _lastStepAuthoredTick.Value < _swingSlowUntilTick)
        {
            var slowedNext = _lastStepAuthoredTick.Value + CombatTuning.SlowedStepCooldownTicks(_lastStepCooldownTicks, factor);
            if (!_nextEligibleTick.HasValue || _nextEligibleTick.Value < slowedNext)
            {
                _nextEligibleTick = slowedNext;
            }
        }
    }

    // SWING-SLOW: is `actionTick` inside the active swing-slow window? Both the block decision (factor 0) and the
    // slowed-cooldown decision key on this. A step at/after _swingSlowUntilTick is outside (the window is
    // half-open [anchor, until)).
    private bool IsInSwingSlowWindow(uint actionTick) => actionTick < _swingSlowUntilTick;

    // SWING-SLOW: the effective per-step cooldown for a step accepted at `actionTick`, given the base cooldown.
    // Inside the swing window it is the slowed cooldown (CombatTuning.SlowedStepCooldownTicks); outside it is the
    // base unchanged. Centralised so TryStep / TryCommitStep / TryCommitStepAuthored all slow identically, and so
    // the predictor mirrors the SAME call. (factor 0 returns the SwingBlockCooldownTicks sentinel, but the block
    // gate refuses the step before this is used as a cooldown, so the sentinel is never stored.)
    private uint EffectiveStepCooldownWithSwingSlow(uint actionTick, uint baseStepCooldownTicks)
    {
        if (!IsInSwingSlowWindow(actionTick))
        {
            return baseStepCooldownTicks;
        }

        return CombatTuning.SlowedStepCooldownTicks(baseStepCooldownTicks, _swingMoveFactor);
    }

    // SWING-SLOW: should a step at `actionTick` be BLOCKED by the swing window? Only the full-stop factor (0)
    // blocks — it reproduces the old hard root (no step inside the window). A non-zero factor never blocks; it
    // slows via the cooldown instead.
    private bool IsBlockedBySwingSlow(uint actionTick)
        => IsInSwingSlowWindow(actionTick) && CombatTuning.IsSwingBlockFactor(_swingMoveFactor);

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
        // SWING-SLOW: a full-stop swing window (factor 0) ALSO blocks the step here — the exact old root behaviour
        // (a step inside the window is refused with "cooldown"). A non-zero factor does NOT block here; it slows
        // the cooldown applied on accept below.
        if ((_nextEligibleTick.HasValue && serverTick < _nextEligibleTick.Value) || IsBlockedBySwingSlow(serverTick))
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
        // SWING-SLOW (commit-path parity): on the held-intent path the AUTHORED tick == serverTick (the predictor's
        // gate actionTick is exactly this tick), so the authored anchor and _lastStepTick coincide.
        _lastStepAuthoredTick = serverTick;
        // SWING-SLOW: remember the BASE cooldown of this step (not the slowed effective one) so a later
        // retroactive-slow at window-open can re-slow it from the same base the predictor used.
        _lastStepCooldownTicks = stepCooldownTicks;
        // SWING-SLOW: a step accepted INSIDE the swing window costs the SLOWED cooldown (longer = slower); outside,
        // the base cooldown. factor 0 never reaches here (blocked above). The predictor applies the identical rule.
        _nextEligibleTick = serverTick + EffectiveStepCooldownWithSwingSlow(serverTick, stepCooldownTicks);
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

        // SWING-SLOW: a full-stop swing window (factor 0) blocks a release-commit that lands inside it. Keyed on the
        // schedule base (the tick the step effectively lands at), the same tick the slowed cooldown below uses, so
        // block and slow agree. A non-zero factor slows the next cooldown instead of blocking.
        var nominalEnd = _lastStepTick.HasValue ? _lastStepTick.Value + stepCooldownTicks : serverTick;
        var scheduleBase = nominalEnd > serverTick ? nominalEnd : serverTick;
        if (IsBlockedBySwingSlow(scheduleBase))
        {
            RejectsCommitTooEarly++;
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

        var from = Tile;
        Tile = target;
        Facing = direction;
        // Schedule the committed step from its NOMINAL end so the early finish consumes the current step's remaining
        // cooldown rather than gaining time (the no-speedhack cap). nominalEnd = the tick the current step's cooldown
        // would have elapsed = _lastStepTick + cooldown (which is exactly the old _nextEligibleTick). If the commit
        // arrives at/after that nominal end (or the entity never stepped), it is just an on-time step scheduled from
        // serverTick.
        _lastStepTick = scheduleBase;
        // SWING-SLOW (commit-path parity): the S103 release-commit path is not driven by the predictor's live step
        // loop (it is a cosmetic-lead model-B completion), so its authored anchor is simply the schedule base it
        // applies at — the same value _lastStepTick takes here. No paced authored-tick divergence exists on this path.
        _lastStepAuthoredTick = scheduleBase;
        _lastStepCooldownTicks = stepCooldownTicks;
        // SWING-SLOW: slowed cooldown inside the window (keyed on the schedule base), base outside.
        _nextEligibleTick = scheduleBase + EffectiveStepCooldownWithSwingSlow(scheduleBase, stepCooldownTicks);
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

        // SWING-SLOW: a full-stop swing window (factor 0) blocks a client-driven commit that lands inside it, the
        // exact old root behaviour for the per-step commit path. Keyed on the SCHEDULED (paced) tick the step lands
        // on — the same tick the slowed cooldown below is keyed on — so block and slow agree. A non-zero factor does
        // not block; it slows the next-eligible cooldown after accept.
        if (IsBlockedBySwingSlow(scheduledTick))
        {
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
        // SWING-SLOW (commit-path parity fix — THE load-bearing line): the retroactive-slow anchor is the AUTHORED
        // tick the client banked the step on (clampedAuthored == the predictor's gate actionTick at realistic
        // latency), NOT the paced scheduledTick. When a recovered/bundled/reordered commit is PACED, scheduledTick
        // is pushed out to _lastAuthoredCommitTick + cooldown while clampedAuthored stays at the predictor's gate
        // tick — so anchoring on clampedAuthored keeps the retroactive-slow inputs (anchor + base cooldown)
        // byte-identical with the predictor's _lastLiveStepTick, killing the slow-form rubberband on the commit path.
        _lastStepAuthoredTick = clampedAuthored;
        _lastStepCooldownTicks = stepCooldownTicks;
        // SWING-SLOW: a commit accepted inside the swing window costs the slowed cooldown (keyed on the scheduled
        // tick the step lands on); outside, the base. Mirrors TryStep's slow on the same window/factor.
        _nextEligibleTick = scheduledTick + EffectiveStepCooldownWithSwingSlow(scheduledTick, stepCooldownTicks);
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
