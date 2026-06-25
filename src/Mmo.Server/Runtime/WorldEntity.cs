using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class WorldEntity
{
    // The earliest server tick at which this entity's next movement step may fire. Null = never acted, so the
    // first step is always eligible. An ACCEPTED step sets it to serverTick + the full step cooldown; a step
    // BLOCKED at a wall advances it by one tick (the cooldown is not consumed) so a held-into-a-wall intent
    // re-tests next tick. (S98: turn-then-move removed — a direction change now steps immediately, facing set on the
    // step; there is no separate turn beat or turn delay.) Phase 1: still the MONSTER tile-step pacing gate AND the
    // player attack-movement-ROOT freeze (IsMovementFrozen) — the continuous PLAYER integrator does not gate ordinary
    // pacing on it.
    private uint? _nextEligibleTick;

    // COMBAT-S2B: the earliest server tick at which this entity's next ATTACK may fire. Null = never attacked, so
    // the first attack is always eligible. INDEPENDENT of the movement cooldown (_nextEligibleTick) — an attack and
    // a move pace on separate clocks, exactly as the task requires (a move never arms the attack cooldown and
    // vice-versa). An accepted attack sets it to serverTick + the attack cooldown; a rejected (on-cooldown) attack
    // leaves it untouched. Modelled the same way as _nextEligibleTick so the gate is underflow-safe near tick 0.
    private uint? _nextEligibleAttackTick;

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
        // Phase 0: position is the continuous WorldVector at the spawn tile's CENTRE. Movement stays tile-stepped,
        // so Position only ever holds exact tile-centre values here; TileCoord (below) rounds it back to the tile.
        Position = WorldVector.FromTile(tile);
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

    // The entity's CONTINUOUS world position (WorldVector, tile units). Phase 1: for PLAYERS this now holds
    // FRACTIONAL values — IntegrateMovement advances it by Velocity x dt off-grid. For MONSTERS it stays an exact
    // tile centre (every TryStep write goes through WorldVector.FromTile(targetTile)). The wire/persistence/grid
    // still speak TileCoord; they read the derived TileCoord accessor below, which rounds to the nearest tile.
    public WorldVector Position { get; private set; }

    // The entity's current world-space velocity (units/sec). Phase 1: LIVE for PLAYERS — IntegrateMovement sets it
    // to unitDir x SpeedUnitsPerSecond each tick (and StopMovement zeroes it on release). Stays Zero for MONSTERS
    // (they tile-step via TryStep, which never touches Velocity).
    public WorldVector Velocity { get; private set; } = WorldVector.Zero;

    // The entity's speed stat (tiles/sec), set by the server from base move speed x SpeedMultiplier. Phase 1: LIVE
    // for PLAYERS — IntegrateMovement scales the unit direction by this. Monsters still pace off
    // SpeedMultiplier / EffectiveStepCooldownTicks (the tile-step cadence), so this is dormant for them.
    public double SpeedUnitsPerSecond { get; private set; }

    // The entity's tile (nearest tile centre to Position). The single read accessor for every tile-needing
    // server site (grid/AOI/wire build/traces): while Position is a tile centre (Phase 0) this is exact and
    // lossless. Replaces the former stored Tile field; the many `.Tile` read sites became `.TileCoord`.
    public TileCoord TileCoord => Position.ToTileRounded();

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

    // Store the tiles/sec speed stat. Set by the server from BaseMoveSpeedUnitsPerSecond × SpeedMultiplier on
    // spawn / speed change. Phase 1: consumed LIVE by the PLAYER integrator (IntegrateMovement). Guards against
    // non-finite values.
    public void SetSpeedUnitsPerSecond(double unitsPerSecond)
    {
        if (!double.IsFinite(unitsPerSecond) || unitsPerSecond < 0)
        {
            return;
        }

        SpeedUnitsPerSecond = unitsPerSecond;
    }

    // COMBAT-S1: server-authoritative character vitals (HP / mana / stamina, each current + max). Defaults to
    // full 100/100 each on spawn. No damage/regen/death yet — this stage only models them existing, being
    // dev-set (clamped to [0, max]), and replicated to the owning client. Mirrors the SpeedMultiplier pattern:
    // private setter, a Try* mutator that clamps + reports whether the value actually changed so the caller only
    // re-replicates on a real change.
    public CharacterStats Stats { get; private set; } = CharacterStats.Default;

    // LIVING-ENEMIES P2-POLISH: set this entity's MAX health AND fill current to it (spawn-at-full), bumping
    // StateRevision so the new HP rides the snapshot. Used to give a monster its per-TYPE MaxHealth at spawn (the
    // default is 100/100). A non-positive max is ignored. Distinct from TrySetStatCurrent (which only moves current
    // within the existing max) — this moves the MAX. Mana/stamina are left at the CharacterStats default.
    public void SetMaxHealthFull(int maxHealth)
    {
        if (maxHealth <= 0)
        {
            return;
        }

        Stats = Stats with { MaxHealth = maxHealth, Health = maxHealth };
        StateRevision++;
    }

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

    // COMBAT-QOL: regenerates `amount` of Health toward MaxHealth, clamping the result into [0, MaxHealth]. Returns
    // true iff the stored Health actually changed (so the caller only bumps replication on a real change), false
    // otherwise (already at full, or a non-positive amount). The inverse of ApplyDamage: it ADDS, and WithHealth caps
    // at MaxHealth so a heavy regen can never overshoot. The increased Health rides the same public-HP snapshot field,
    // so the overhead bar REFILLS automatically with no dedicated message — and crucially NO DamageEventMessage is
    // emitted for regen (only real damage floats a number). Drives the dummy heal-back loop (RegenDummies).
    public bool TryRegenHealth(int amount)
    {
        if (amount <= 0)
        {
            return false;
        }

        var updated = Stats.WithHealth(Stats.Health + amount);
        if (updated == Stats)
        {
            return false;
        }

        Stats = updated;
        StateRevision++;
        return true;
    }

    // LIVING-ENEMIES P3: TELEPORT this entity to `tile` WITHOUT a movement step — sets Tile directly (the caller
    // migrates the spatial-grid bucket via Zone.Teleport), faces it S, and resets the movement cooldown clocks so the
    // respawned entity can act immediately and its predictor re-bases cleanly. Bumps StateRevision so the new position
    // rides the next snapshot (the client sees the entity jump). Used for player death->respawn (no death corpse/penalty
    // this phase); distinct from a TryStep (which validates walkability + cooldown). The caller is responsible for the
    // tile being walkable (the spawn tile always is). Returns nothing — a teleport is unconditional.
    public void TeleportTo(TileCoord tile)
    {
        // Phase 0: teleport assigns the tile-centre position with the unchanged target tile (no behaviour change).
        Position = WorldVector.FromTile(tile);
        Facing = Direction8.S;
        _nextEligibleTick = null;
        StateRevision++;
        StepSequence++;
    }

    // LIVING-ENEMIES P3: restore current Health to MaxHealth (respawn at full). Returns true iff Health actually
    // changed (so the caller only re-replicates on a real change), bumping StateRevision so the refilled bar rides the
    // snapshot. Mana/stamina are left untouched (this phase only models HP-driven death). A no-op at full HP.
    public bool RestoreFullHealth()
    {
        if (Stats.Health == Stats.MaxHealth)
        {
            return false;
        }

        Stats = Stats with { Health = Stats.MaxHealth };
        StateRevision++;
        return true;
    }

    // LIVING-ENEMIES P2: turn this entity to face `direction` WITHOUT moving (used when a monster attacks a target
    // in place — it should look at its victim). Returns true iff Facing actually changed, bumping StateRevision so
    // the new facing rides the snapshot delta and the client flips the sprite. A no-op (already facing that way)
    // changes nothing and does not spam a delta. Distinct from a step (which also faces, but moves a tile).
    public bool TrySetFacing(Direction8 direction)
    {
        if (Facing == direction)
        {
            return false;
        }

        Facing = direction;
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
            var cooldownTarget = TileCoord.Offset(cooldownDelta.X, cooldownDelta.Y);
            result = new MovementStepResult(
                direction,
                TileCoord,
                cooldownTarget,
                CooldownElapsed: false,
                grid.IsWalkable(cooldownTarget),
                Accepted: false,
                "cooldown",
                TileCoord);
            return false;
        }

        // S98: a direction change steps IMMEDIATELY in the new direction — there is no separate turn beat. The
        // step itself faces you, so set Facing unconditionally up front. Capture whether the facing actually
        // changed so a blocked-into-a-wall step (no tile move) can STILL bump StateRevision and replicate the
        // new facing (the Cato sprite flip depends on it).
        var facingChanged = Facing != direction;
        Facing = direction;

        var delta = direction.Delta();
        var target = TileCoord.Offset(delta.X, delta.Y);
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
                TileCoord,
                target,
                CooldownElapsed: true,
                TargetWalkable: false,
                Accepted: false,
                grid.IsInBounds(target) ? "blocked" : "out_of_bounds",
                TileCoord);
            return false;
        }

        var from = TileCoord;
        // Phase 0: accepted step assigns the tile-centre position with the UNCHANGED integer target tile.
        Position = WorldVector.FromTile(target);
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
            TileCoord);
        return true;
    }

    // Phase 1 (continuous migration): the PLAYER continuous integrator — a direct port of the proven
    // exp:ContinuousMover.Step (Z->Y, on WorldVector), NO-WALLS path (real swept-circle collision is Phase 2, so a
    // player walks through walls here, which is the expected Phase-1 behaviour). Per server tick:
    //   Velocity = unitDir x SpeedUnitsPerSecond            // unit direction (Direction8.ToUnitVector) x server speed
    //   Position += Velocity x dtSeconds                    // dt is FIXED = 1/TickRate (the caller owns it)
    // The client's MoveIntent carries ONLY a Direction8 (no magnitude, no timing), so the server owns speed AND dt —
    // anti-speedhack is intrinsic. Faces the entity from the direction (the unit vector points the same way as the
    // tile delta, so the discrete Facing follows the continuous heading). Returns true iff the entity's ROUNDED tile
    // crossed a boundary this tick — only THEN do we bump StateRevision/StepSequence and (via Zone) migrate the grid
    // bucket, so the tile-keyed wire/grid stay at exactly today's cadence and the snapshot bandwidth is unchanged
    // (R1: do NOT bump every sub-tile tick). A zero unitDir is treated as a stop (Velocity = Zero, no move).
    //
    // Distinct from TryStep (the tile-step path monsters still use): a player uses IntegrateMovement (Velocity goes
    // non-zero); a monster uses TryStep (Velocity stays Zero). NEVER both on one entity (R3).
    public bool IntegrateMovement(WorldVector unitDir, double dtSeconds)
    {
        // Set velocity from the (already unit) direction scaled by the server speed stat. A zero direction means
        // "not moving" — zero velocity, an instant stop with no inertia (matches ContinuousMover's no-input branch).
        Velocity = unitDir.LengthSquared > 0d ? unitDir * SpeedUnitsPerSecond : WorldVector.Zero;

        // Face from the held direction even on a zero-dt / zero-distance tick so the sprite heading is correct (the
        // unit vector points the same way as the tile delta). A zero direction leaves Facing unchanged.
        var facing = FacingFromUnit(unitDir);
        if (facing.HasValue)
        {
            Facing = facing.Value;
        }

        if (dtSeconds <= 0d || Velocity.LengthSquared <= 0d)
        {
            return false;
        }

        var previousTile = TileCoord;
        Position += Velocity * dtSeconds;
        var crossedTile = TileCoord != previousTile;
        if (crossedTile)
        {
            // R1: only a rounded-tile crossing bumps replication state — the tile-keyed snapshot then carries the
            // advance to the client at exactly the discrete cadence (StepSequence still counts tile crossings).
            StateRevision++;
            StepSequence++;
        }

        return crossedTile;
    }

    // Phase 1 (continuous migration): the attack-movement-ROOT freeze gate for the PLAYER integrator (R2). A
    // committed swing pushes _nextEligibleTick forward (ApplyAttackMovementRoot[Authored]) to root the attacker's
    // movement — a combat invariant the combat tests assert. The continuous integrator no longer gates on the step
    // cooldown for ordinary pacing (that machinery is the tile-step path monsters keep), but it MUST still honour
    // this root: while serverTick is before the next-eligible tick the player is frozen and does not integrate. The
    // caller skips IntegrateMovement (and instead StopMovement()s) for a frozen player so a rooted entity neither
    // glides nor advances. A never-rooted entity (no _nextEligibleTick) is never frozen.
    public bool IsMovementFrozen(uint serverTick)
    {
        return _nextEligibleTick.HasValue && serverTick < _nextEligibleTick.Value;
    }

    // Phase 1: instant stop — zero the velocity so the entity does not glide. Called on release / dead / keepalive
    // timeout (R6: without this the entity keeps its last Velocity and integrates forever). Position is untouched
    // (the entity stays exactly where it is — fractional tile position is fine; the wire rounds it). No StateRevision
    // bump: a stop changes no tile and no facing.
    public void StopMovement()
    {
        Velocity = WorldVector.Zero;
    }

    // The Direction8 a unit vector points toward (same table as Direction8.ToUnitVector), or null for a zero vector
    // (no heading -> leave Facing as-is). Used by the integrator to keep the discrete Facing tracking the continuous
    // heading without re-deriving the 8-way table at the call site.
    private static Direction8? FacingFromUnit(WorldVector unitDir)
    {
        if (unitDir.LengthSquared <= 0d)
        {
            return null;
        }

        var dx = Math.Sign(unitDir.X);
        var dy = Math.Sign(unitDir.Y);
        return (dx, dy) switch
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
            return grid.IsWalkable(TileCoord.Offset(delta.X, 0)) && grid.IsWalkable(TileCoord.Offset(0, delta.Y));
        }

        return true;
    }
}
