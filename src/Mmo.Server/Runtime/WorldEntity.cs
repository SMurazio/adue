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
    // FRACTIONAL values — IntegrateMovement advances it by Velocity x dt off-grid. For MONSTERS it holds the
    // continuous hop landing (HopLocomotion applies a collision-resolved position via ApplyResolvedMove, often
    // sub-tile). The wire/persistence/grid still speak TileCoord; they read the derived TileCoord accessor below,
    // which rounds to the nearest tile.
    public WorldVector Position { get; private set; }

    // The entity's current world-space velocity (units/sec). Phase 1: LIVE for PLAYERS — IntegrateMovement sets it
    // to unitDir x SpeedUnitsPerSecond each tick (and StopMovement zeroes it on release). Stays Zero for MONSTERS
    // (the hop applies position directly via ApplyResolvedMove and never routes through the velocity integrator).
    public WorldVector Velocity { get; private set; } = WorldVector.Zero;

    // The entity's speed stat (tiles/sec), set by the server from base move speed x SpeedMultiplier. Phase 1: LIVE
    // for PLAYERS — IntegrateMovement scales the unit direction by this. Monsters still pace off
    // SpeedMultiplier / EffectiveStepCooldownTicks (the tile-step cadence), so this is dormant for them.
    public double SpeedUnitsPerSecond { get; private set; }

    // MOVEMENT-ACTIONS (Phase A): the AUTHORITATIVE vertical position (world units ABOVE the ground plane; 0 = on the
    // ground, >0 = airborne). Design §1.4.1: a SEPARATE scalar, NOT a third component on the WorldVector Position —
    // so ALL existing XY collision (ContinuousCollision/TileWalls), AOI, distance/range and snapshot-XY code is
    // unchanged (they were never Z-aware and don't need to be; the Z "rides alongside"). Default 0, non-zero only
    // while airborne (a tiny fraction of the time), so it never touches the hot XY path. Driven by the
    // ServerActionExecutor's ballistic jump (SetVerticalOffset each airborne tick, SnapToGround on landing).
    // Distinct from and supersedes the cosmetic client-only HopHeight arc (which Phase C removes). NOT replicated in
    // Phase A — the wire/codec addition is Phase B; this is the server-authoritative source the predictor will
    // mirror. No StateRevision bump here: the entity is moving while airborne (Velocity may be 0 for a jump, but the
    // executor advances Position each tick), so it is already force-included; Phase B owns the snapshot encoding.
    public double VerticalOffset { get; private set; }

    // MOVEMENT-ACTIONS (Phase A): set the airborne height for the current tick (the ballistic arc value the executor
    // computed from BallisticArc). Clamps non-finite/negative to 0 (the body never goes below the ground plane).
    // Pure state-write, no replication bump (see VerticalOffset note). Distinct from SnapToGround, which is the
    // explicit landing seam.
    public void SetVerticalOffset(double offset)
    {
        VerticalOffset = double.IsFinite(offset) && offset > 0d ? offset : 0d;
    }

    // MOVEMENT-ACTIONS (Phase A): land — snap VerticalOffset to an EXACT ground value (design §1.4.2 "no float drift
    // at the seam"). The executor passes GroundHeight.GroundHeightAt(landingXY) (0 today); this sets it verbatim so
    // the body sits exactly on the ground, never a float-rounded hair off it. Called on the final airborne tick and
    // on an interrupt that lands an airborne entity.
    //
    // B1 FIX (the Z stop-edge — mirrors StopMovement's XY stop-edge): bump StateRevision on the airborne→ground
    // TRANSITION. On the landing tick the action ENDS (IsActive→false) the same tick Z snaps, and a jump's Velocity
    // is 0, so the entity is no longer force-included; without this bump the grounded VerticalOffset would never
    // replicate and the client would keep the last airborne height — a residual float, worst when jumping from a
    // standstill (no later move to heal it). The guard fires ONLY on a real change, so a re-land at the same ground
    // value (or a no-op call) does not bump.
    public void SnapToGround(double groundHeight)
    {
        var previous = VerticalOffset;
        VerticalOffset = double.IsFinite(groundHeight) && groundHeight > 0d ? groundHeight : 0d;
        if (VerticalOffset != previous)
        {
            StateRevision++;
        }
    }

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

    // The PLAYER continuous integrator — a direct port of the proven exp:ContinuousMover.Step (Z->Y, on WorldVector),
    // the GRID-AGNOSTIC open-field path (NO wall collision at THIS layer — Phase 2's swept-circle collision lives in
    // Zone.IntegrateMovement, which interposes ContinuousCollision.Resolve between the velocity/facing step and the
    // position apply). This overload advances straight, so a bare WorldEntity (no grid in scope) walks unobstructed —
    // it is retained for the integrator unit tests and any caller wanting the un-collided advance. Per server tick:
    //   Velocity = unitDir x SpeedUnitsPerSecond            // unit direction (Direction8.ToUnitVector) x server speed
    //   Position += Velocity x dtSeconds                    // dt is FIXED = 1/TickRate (the caller owns it)
    // The client's MoveIntent carries ONLY a Direction8 (no magnitude, no timing), so the server owns speed AND dt —
    // anti-speedhack is intrinsic. Faces the entity from the direction (the unit vector points the same way as the
    // tile delta, so the discrete Facing follows the continuous heading). Returns true iff the entity's ROUNDED tile
    // crossed a boundary this tick — only THEN do we bump StateRevision/StepSequence and (via Zone) migrate the grid
    // bucket, so the tile-keyed wire/grid stay at exactly today's cadence and the snapshot bandwidth is unchanged
    // (R1: do NOT bump every sub-tile tick). A zero unitDir is treated as a stop (Velocity = Zero, no move).
    //
    // Distinct from the monster hop (HopLocomotion): a player uses IntegrateMovement (Velocity goes non-zero); a
    // monster applies a discrete collision-resolved hop via ApplyResolvedMove (Velocity stays Zero). NEVER both on
    // one entity (R3).
    public bool IntegrateMovement(WorldVector unitDir, double dtSeconds)
    {
        // Phase 2: the NO-WALLS integrator, now expressed as the two-step split the collided Zone path uses
        // (ComputeMoveDelta sets Velocity + Facing and returns the raw delta; ApplyResolvedMove applies it + runs the
        // tile-crossing bookkeeping). Composing them here keeps ONE source of truth for the velocity/facing rule and
        // the tile bump, and is byte-for-byte the former inline body (Position += Velocity·dt with no wall clamp). The
        // Zone path interposes ContinuousCollision.Resolve between the two halves; this open-field overload does not.
        // Retained for the Phase-1 integrator unit tests and any caller that wants the un-collided advance.
        var delta = ComputeMoveDelta(unitDir, dtSeconds);
        return ApplyResolvedMove(Position + delta);
    }

    // CONTINUOUS MIGRATION (Phase 8): the MONSTER HOP movement-cadence gate, mirroring TryStep's _nextEligibleTick
    // rule exactly but WITHOUT a tile-snap or walkability — the hop primitive (HopLocomotion) owns the geometry; this
    // owns only the pacing. Returns true and ARMS the cooldown (next eligible = serverTick + cooldownTicks) iff this
    // entity is off its movement cooldown at serverTick; returns false WITHOUT mutating anything while still inside the
    // window. This is the SAME _nextEligibleTick field the old monster TryStep gated+armed on an accepted step (and the
    // same field the player attack-movement-ROOT freezes), so a hop on cooldown is dropped and an accepted hop arms the
    // next window — replicating TryStep's cadence for the continuous hop with no behaviour change. A FULLY-BLOCKED hop
    // (no progress) must NOT arm: the caller checks readiness here only once it has decided to commit a moving hop, so
    // a stuck hop leaves the gate where it is and re-tests next tick (TryStep's blocked-step rule).
    public bool TryBeginHop(uint serverTick, uint cooldownTicks)
    {
        if (_nextEligibleTick.HasValue && serverTick < _nextEligibleTick.Value)
        {
            return false;
        }

        _nextEligibleTick = serverTick + cooldownTicks;
        return true;
    }

    // CONTINUOUS MIGRATION (Phase 8): true iff this entity's movement cooldown has elapsed at serverTick (the hop
    // cadence gate, READ-ONLY — does not arm). HopLocomotion checks this BEFORE doing the resolve/fan work so a
    // fully-blocked hop can leave the gate un-armed (re-try next tick) while an accepted hop arms it via TryBeginHop.
    // Same field as TryBeginHop / the attack-root freeze, so the player swing-root still gates a (hypothetical) hop.
    public bool IsHopReady(uint serverTick)
    {
        return !_nextEligibleTick.HasValue || serverTick >= _nextEligibleTick.Value;
    }

    // CONTINUOUS MIGRATION (Phase 8): face this entity from a continuous unit heading (the same 8-way table the player
    // integrator uses via ComputeMoveDelta). Used by the monster hop so the sprite still faces its movement/target
    // heading even though Velocity stays Zero (the hop never routes through ComputeMoveDelta). A zero vector leaves
    // Facing untouched. Bumps StateRevision (via TrySetFacing) only on a real change so it does not spam deltas.
    public void SetFacingFromUnit(WorldVector unitDir)
    {
        if (FacingFromUnit(unitDir) is { } facing)
        {
            TrySetFacing(facing);
        }
    }

    // CONTINUOUS MIGRATION (Phase 2): apply a COLLIDED end position the caller already computed (via the shared
    // ContinuousCollision.Resolve against the nearby walls) and run the SAME tile-crossing bookkeeping
    // IntegrateMovement does. WorldEntity stays grid-agnostic — it does NOT query walls or run the resolver itself
    // (Zone owns the grid + the resolve); this is the seam that keeps the entity unaware of collision geometry while
    // still owning its replication state. Velocity/Facing are set by IntegrateMovement (the velocity/heading step,
    // unchanged); this only writes the resolved Position and bumps StateRevision/StepSequence iff the ROUNDED tile
    // crossed (R1 — sub-tile moves do NOT bump, so the tile-keyed snapshot cadence/bandwidth are unchanged). Returns
    // true iff the rounded tile crossed (the signal Zone uses to migrate the spatial bucket).
    public bool ApplyResolvedMove(WorldVector newPosition)
    {
        var previousTile = TileCoord;
        Position = newPosition;
        var crossedTile = TileCoord != previousTile;
        if (crossedTile)
        {
            StateRevision++;
            StepSequence++;
        }

        return crossedTile;
    }

    // CONTINUOUS MIGRATION (Phase 2): the velocity/heading half of a continuous tick, SEPARATED from position
    // integration so the caller (Zone) can interpose swept-circle collision between them: set Velocity + Facing here,
    // then resolve (Position += Velocity·dt) against walls, then ApplyResolvedMove(collided). Mirrors the front of the
    // Phase-1 IntegrateMovement exactly (same velocity rule, same facing-from-direction, same stop semantics) but
    // returns the RAW (un-collided) delta for this tick instead of mutating Position. A zero unitDir / zero dt yields
    // a zero delta (an instant stop with no glide — Velocity goes Zero). Phase 4's predictor mirrors this same split.
    public WorldVector ComputeMoveDelta(WorldVector unitDir, double dtSeconds)
    {
        Velocity = unitDir.LengthSquared > 0d ? unitDir * SpeedUnitsPerSecond : WorldVector.Zero;

        var facing = FacingFromUnit(unitDir);
        if (facing.HasValue)
        {
            Facing = facing.Value;
        }

        if (dtSeconds <= 0d || Velocity.LengthSquared <= 0d)
        {
            return WorldVector.Zero;
        }

        return Velocity * dtSeconds;
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

    // CONTINUOUS MIGRATION (Phase 10): restore a freshly-spawned durable player's persisted CONTINUOUS position
    // (the off-grid WorldVector loaded from pos_x/pos_y), overriding the tile-centre the constructor seeded from
    // the resolved spawn tile. Caller guarantees the rounded tile is unchanged from the resolved spawn tile (so
    // the entity lands in the same walkable cell, just at its true sub-tile offset). No StateRevision bump: the
    // entity has not been replicated yet (this runs during login, before the first snapshot), so the initial
    // EntitySpawn/snapshot simply carries this position. Distinct from TeleportTo (which snaps to a tile centre
    // and bumps revision for an in-world jump).
    public void RestorePosition(WorldVector position)
    {
        Position = position;
    }

    // Phase 1: instant stop — zero the velocity so the entity does not glide. Called on release / dead / keepalive
    // timeout (R6: without this the entity keeps its last Velocity and integrates forever). Position is untouched
    // (the entity stays exactly where it is — fractional tile position is fine; the wire rounds it).
    //
    // STOP-EDGE RE-PUBLISH (stop-edge fix): bump StateRevision ONCE on the moving→stopped TRANSITION (Velocity was
    // non-zero, now Zero). Movement snapshots are Unreliable (UDP); the sub-tile force-include (1133c7e) re-includes
    // the own entity every tick only while Velocity != 0, so the instant velocity zeroes the precise stop Position
    // would be delta'd OUT and, since a stop crosses no tile (ApplyResolvedMove never bumps), never re-published at
    // rest. If the final moving snapshot drops, the client's confirmed base stays frozen at the stale last-moving
    // position and the predictor settles BACKWARD onto it on release. Bumping the revision once on the transition
    // re-enters the precise stop position into the standard "unacked entities re-include next tick under loss"
    // self-healing path (and is correct for remote viewers — they want the final stop position too). This fires ONLY
    // on the transition: a second StopMovement() on an already-rest entity (Velocity already Zero) is a no-op, so a
    // player at steady rest does NOT keep bumping (no bandwidth at rest).
    public void StopMovement()
    {
        var wasMoving = Velocity.LengthSquared > 0d;
        Velocity = WorldVector.Zero;
        if (wasMoving)
        {
            StateRevision++;
        }
    }

    // MONSTER-SEPARATION (todo/N-monster-monster-collision-separation.md): re-publish a position the separation pass
    // nudged WITHOUT a tile cross. ApplyResolvedMove bumps StateRevision/StepSequence only on a rounded-tile crossing
    // (R1), so a sub-tile separation nudge on an IDLE monster (Velocity 0, no tile cross) would be delta'd OUT of the
    // snapshot (the recipient already acked the unchanged revision) and the corrected position would never replicate.
    // Bump StateRevision so the nudged position re-includes next snapshot — the SAME stop-edge (StopMovement) /
    // SnapToGround re-publish mechanism. Velocity is NOT touched (the pass is pure de-penetration, no physics), and
    // StepSequence is NOT bumped (it counts the OWNING client predictor's steps — monsters have no local predictor).
    public void MarkRepositioned()
    {
        StateRevision++;
    }

    // MONSTER-BEHAVIOR P2: set the replicated Velocity to the ACTUAL resolved per-tick velocity (not the pre-collision
    // desired one). A continuous mover (GlideLocomotion) sets the desired Velocity = dir x speed via ComputeMoveDelta,
    // resolves it against walls (slide/stop), then calls this with (resolvedDelta / dt) so the wire carries the velocity
    // that MATCHES the real motion — the replication guardrail. Without it a glider sliding along / wedged at a wall
    // would replicate a velocity pointing INTO the wall, and the client (which extrapolates along velocity) would drift
    // into the wall and correct each tick. Does NOT bump StateRevision (the apply-landing already governs the tile-cross
    // re-publish); a Zero here is the velocity-coherent "wedged, not moving" state (prefer StopMovement at a real stop
    // so the stop-edge bump fires).
    public void SetVelocity(WorldVector velocity) => Velocity = velocity;

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
}
