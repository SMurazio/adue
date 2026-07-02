using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;

namespace Mmo.Server.Runtime;

// MOVEMENT-ACTIONS (Phase A): the server-side action executor (design §3 / §5). It is the SINGLE SOURCE OF TRUTH for
// a running movement action: it holds the per-entity active action instance, validates a trigger (one-at-a-time +
// cooldown), and ADVANCES the instance once per server tick — for a Jump it advances the XY through the SHARED
// collision resolver AND the ballistic Z (driving WorldEntity.VerticalOffset), and on the final tick snaps to ground,
// ends the action, and arms the cooldown. It is ENTITY-AGNOSTIC (player or monster — the trigger source differs, the
// executor does not) and ACTION-AGNOSTIC (it calls def.Trajectory + BallisticArc; no per-action branches).
//
// PHASE A SCOPE. NO netcode: no wire, no prediction, no trigger source. Players trigger via the wire (Phase B),
// monsters via AI (Phase C). Phase A makes the executor EXIST, be DRIVEN each tick (StepAll), and be UNIT-TRIGGERABLE
// (TryStart). While an entity is mid-action the executor DRIVES its movement; the caller suppresses normal input
// integration for that entity (GameServer skips HandleMoveIntent integration when IsActive — design §4).
//
// DETERMINISM is the whole point (Phase B predicts this exact trajectory): the XY uses the SAME shared resolver +
// walls + radius as ordinary movement; the Z uses the SHARED BallisticArc over integer ticks + derived constants;
// the ActionContext is captured once at trigger and never re-read from live state. An identical trigger yields a
// byte-identical path — the contract Phase B's prediction depends on. The wall-query + apply-landing seams are
// INJECTED (delegates, like HopLocomotion) so the executor is unit-testable against a bare TileGrid / WorldState
// without a live Zone, and so it reuses the EXACT same collision derivation a player/monster move does.
public sealed class ServerActionExecutor
{
    // Fills `scratch` with the collision walls near a swept move (from, delta, radius) in stable row-major order —
    // the SAME TileGrid.QueryNearbyWalls / TileWalls helper the player integrator + hop use. Injected so the executor
    // collides byte-identically to ordinary movement and is unit-testable without a live Zone.
    public delegate void QueryWallsDelegate(
        WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Wall> scratch);

    // Applies the resolved XY landing to `entity` (WorldEntity.ApplyResolvedMove) AND migrates its spatial-grid
    // bucket on a tile cross — the SAME bookkeeping Zone.IntegrateMovement / ApplyMonsterLanding run. Returns whether
    // the rounded tile crossed (unused by the executor; kept to mirror the existing apply-seam signature). Injected.
    public delegate bool ApplyResolvedMoveDelegate(WorldEntity entity, WorldVector resolvedPosition);

    // PLAYER↔MONSTER COLLISION: fills `scratch` with the body Circles `actor` should collide with this action step —
    // KIND-AWARE: a MONSTER actor (a hop arc / charge dash) gathers nearby PLAYERS so it STOPS at the player instead of
    // arcing through it; a PLAYER actor (a predicted jump) gathers NOTHING this phase, so player jumps stay byte-
    // identical (and parity-safe — the client predictor's action path is unchanged). Injected like the wall query and
    // OPTIONAL — null ⇒ no body obstacles (the executor's Phase-A/B unit tests, unchanged). The GameServer impl gathers
    // via the SAME spatial index + body radius ordinary movement uses; monster↔monster stays the separation pass's job.
    public delegate void QueryObstaclesDelegate(
        WorldEntity actor, WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Circle> scratch);

    private readonly int _tickRate;
    private readonly Func<double> _bodyRadiusUnits;
    private readonly QueryWallsDelegate _queryWalls;
    private readonly QueryObstaclesDelegate? _queryObstacles;
    private readonly ApplyResolvedMoveDelegate _applyResolvedMove;
    private readonly List<ContinuousCollision.Wall> _wallScratch = new();
    private readonly List<ContinuousCollision.Circle> _obstacleScratch = new();

    // The per-entity active action instance (one at a time — design §2.8). Absent ⇒ the entity is not in an action.
    private readonly Dictionary<ulong, ActionInstance> _active = new();

    // Per-(entity, action) cooldown clock: the earliest server tick the action may re-trigger on that entity. Its OWN
    // clock, NOT the move/attack cooldown (design §1.1). Keyed by (entityId, actionId) so distinct actions cool down
    // independently. Only populated when an action ends (or is cancelled) with a non-zero cooldown.
    private readonly Dictionary<(ulong EntityId, ActionId Action), uint> _cooldownUntil = new();

    public ServerActionExecutor(
        int tickRate,
        Func<double> bodyRadiusUnits,
        QueryWallsDelegate queryWalls,
        ApplyResolvedMoveDelegate applyResolvedMove,
        QueryObstaclesDelegate? queryObstacles = null)
    {
        if (tickRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickRate), "Tick rate must be positive.");
        }

        _tickRate = tickRate;
        _bodyRadiusUnits = bodyRadiusUnits ?? throw new ArgumentNullException(nameof(bodyRadiusUnits));
        _queryWalls = queryWalls ?? throw new ArgumentNullException(nameof(queryWalls));
        _applyResolvedMove = applyResolvedMove ?? throw new ArgumentNullException(nameof(applyResolvedMove));
        _queryObstacles = queryObstacles;
    }

    // True iff `entity` currently has a running action — the predicate the caller uses to SUPPRESS normal input
    // integration for that entity (design §4: the action owns its position for its duration).
    public bool IsActive(ulong entityId) => _active.ContainsKey(entityId);

    public bool IsActive(WorldEntity entity) => IsActive(entity.Id);

    // The ActionId currently running on `entity`, or None if it is not in an action. Phase B replicates this.
    public ActionId ActiveAction(ulong entityId) =>
        _active.TryGetValue(entityId, out var inst) ? inst.Def.Id : ActionId.None;

    // MOVEMENT-ACTIONS (Phase D, i-frame authority — design §2.7): TRUE iff `entityId` is INSIDE its running action's
    // i-frame window at `serverTick`. READ-ONLY — the DAMAGE seam (ApplyMonsterAttack) queries it; nothing here or on
    // the wire lets a client carry, fake, or extend a window (the intent carries only actionId + heading, and the
    // window is the SERVER def's data anchored at the SERVER-side start tick). The window is the def's inclusive
    // [IFrameStartTick, IFrameEndTick] in action-local ticks, evaluated as elapsed = serverTick − StartTick — anchored
    // on the CLOCK rather than TickInAction so the answer is identical whether damage resolves before or after this
    // tick's StepAll pass (the monster-AI attack pass runs BEFORE StepAll in the tick loop). No action / no window /
    // outside the window ⇒ false. A pre-start tick underflows huge in uint arithmetic and correctly reads false.
    public bool HasActiveIFrames(ulong entityId, uint serverTick)
    {
        if (!_active.TryGetValue(entityId, out var inst))
        {
            return false;
        }

        var def = inst.Def;
        if (!def.HasIFrameWindow)
        {
            return false;
        }

        var elapsed = serverTick - inst.StartTick;
        return elapsed >= def.IFrameStartTick && elapsed <= def.IFrameEndTick;
    }

    // True iff `entity` may trigger `def` at `serverTick`: not already in an action (one-at-a-time, design §2.8), off
    // this action's cooldown clock (design §1.1), and NOT movement-rooted (design §2.1 can-act "not rooted"). Pure —
    // does NOT mutate. These are the ENTITY-level can-act gates the executor owns; the SESSION-level gate (alive) and
    // the authored-tick window stay with the player handler (HandleActionIntent).
    public bool CanStart(WorldEntity entity, MovementActionDef def, uint serverTick)
    {
        if (_active.ContainsKey(entity.Id))
        {
            return false; // an action is already running — strictly serial (no queue)
        }

        if (entity.IsMovementFrozen(serverTick))
        {
            // Movement-rooted (e.g. mid swing-root via ApplyAttackMovementRoot) — cannot start a movement action.
            // Without this a player could trigger a jump to RELOCATE via the executor during the attack-root window,
            // bypassing IsMovementFrozen (which only gates the ordinary HandleMoveIntent integrator) — a root-escape.
            return false;
        }

        if (_cooldownUntil.TryGetValue((entity.Id, def.Id), out var until) && serverTick < until)
        {
            return false; // still inside this action's cooldown window
        }

        return true;
    }

    // Trigger `def` on `entity` at `serverTick`. Validates CanStart; on success snapshots an ActionContext (origin =
    // current XY, heading = the supplied launch heading, speed = live speed, GroundZ = GroundHeightAt(origin)),
    // caches the derived ballistic constants (g/v0), starts the instance, and IMMEDIATELY applies tick 0 (which for a
    // pure jump is z(0) = ground, no XY move) so the action "owns" the entity from this tick. Returns true iff the
    // action started. NO trigger source in Phase A — a test (Phase A) / the wire (Phase B) / the AI (Phase C) call
    // this. `heading` is a unit vector (the locked launch heading); Zero ⇒ no forward travel (treated like InPlace).
    public bool TryStart(WorldEntity entity, MovementActionDef def, WorldVector heading, uint serverTick)
    {
        if (!CanStart(entity, def, serverTick))
        {
            return false;
        }

        var origin = entity.Position;
        var ctx = new ActionContext(
            Origin: origin,
            Heading: heading.LengthSquared > 0d ? heading.Normalized() : WorldVector.Zero,
            Speed: entity.SpeedUnitsPerSecond,
            DtPerTick: 1d / _tickRate,
            GroundZ: GroundHeight.GroundHeightAt(origin))
        {
            TickRate = _tickRate,
        };

        var gravity = BallisticArc.Gravity(def.JumpHeight, def.AirborneTicks, _tickRate);
        var launchVelocity = BallisticArc.LaunchVelocity(def.JumpHeight, def.AirborneTicks, _tickRate);

        var instance = new ActionInstance(def, ctx, gravity, launchVelocity, startTick: serverTick);
        _active[entity.Id] = instance;

        // Face the locked launch heading once at trigger (held for the duration — design §1.4.6 / §4). A zero heading
        // leaves facing as-is.
        entity.SetFacingFromUnit(ctx.Heading);

        // Tick 0 is the takeoff frame: z(0) = GroundZ (on the ground), no XY displacement. Apply it so the entity is
        // grounded-at-origin at the start; ticks 1..N are the airborne arc advanced by Step.
        entity.SetVerticalOffset(BallisticArc.HeightOffsetAtTick(gravity, launchVelocity, _tickRate, 0));

        return true;
    }

    // Advance `entity`'s active action by ONE server tick at `serverTick`. For tick i in 1..DurationTicks:
    //   * XY: desired delta = def.Trajectory(ctx, i); resolve it through the shared swept-circle resolver against the
    //     nearby walls (so an airborne/dashing entity can still be wall-blocked → lands short); ApplyResolvedMove.
    //   * Z:  VerticalOffset = BallisticArc height at tick i (free, never wall-constrained).
    // On the FINAL tick (i == DurationTicks): SNAP VerticalOffset to GroundHeightAt(landingXY) exactly (no float-seam
    // drift), END the action, and ARM the cooldown. No-op if the entity is not in an action. Returns true iff the
    // action is STILL active after this step (false once it ended this tick).
    public bool Step(WorldEntity entity, uint serverTick)
    {
        if (!_active.TryGetValue(entity.Id, out var inst))
        {
            return false;
        }

        inst.TickInAction++;
        var i = inst.TickInAction;
        var def = inst.Def;

        // XY: resolve the trajectory's desired delta through the shared collision (skip the work for a zero delta —
        // an InPlace jump or a zero-heading forward arc — so it never bumps a tile or queries walls needlessly).
        // ActionContext is a get-only property; bind it to a local so it can be passed by `in` (the explicit
        // call-site `in` modifier requires an addressable variable, not a property value).
        var ctx = inst.Context;
        var desired = def.Trajectory(in ctx, i);
        if (desired.LengthSquared > 0d)
        {
            var radius = _bodyRadiusUnits();
            var start = entity.Position;
            _queryWalls(start, desired, radius, _wallScratch);

            // PLAYER↔MONSTER COLLISION: gather the kind-aware body obstacles (a monster's hop arc / charge dash collides
            // with nearby PLAYERS; a player jump gathers nothing this phase) and resolve the arc against them too, so a
            // hopping/dashing monster STOPS at the player instead of arcing through it. No gather / empty set ⇒ the
            // walls-only path, byte-identical to before (the player-jump path and the executor's unit tests).
            _obstacleScratch.Clear();
            _queryObstacles?.Invoke(entity, start, desired, radius, _obstacleScratch);
            var resolved = _obstacleScratch.Count == 0
                ? ContinuousCollision.Resolve(start, desired, radius, _wallScratch)
                : ContinuousCollision.Resolve(start, desired, radius, _wallScratch, _obstacleScratch);
            _applyResolvedMove(entity, resolved);
        }

        if (i >= def.DurationTicks)
        {
            // Landing tick: snap Z to the ground at the landing XY (explicit — no reliance on z(N) rounding), end the
            // action, arm the cooldown on its own clock.
            entity.SnapToGround(GroundHeight.GroundHeightAt(entity.Position));
            EndInstance(entity, inst, serverTick);
            return false;
        }

        // Airborne tick: drive the ballistic height from the cached constants.
        entity.SetVerticalOffset(BallisticArc.HeightOffsetAtTick(inst.Gravity, inst.LaunchVelocity, _tickRate, i));
        return true;
    }

    // Drive EVERY entity currently in an action by one tick (the tick-loop entry point — call it next to
    // StepMonsterAi / player integration). Iterates a snapshot of the active ids so an action ending mid-pass (it
    // removes itself from _active) does not perturb the enumeration. Phase A: GameServer calls this each tick (the
    // set is empty until a trigger source exists, so it is ~free until Phase B/C).
    public void StepAll(WorldState world, uint serverTick)
    {
        if (_active.Count == 0)
        {
            return;
        }

        // Snapshot ids into the reused scratch (the dictionary mutates as actions end mid-pass). Small set (one entry
        // per actively-jumping entity), so the snapshot is cheap.
        _idScratch.Clear();
        foreach (var id in _active.Keys)
        {
            _idScratch.Add(id);
        }

        foreach (var entityId in _idScratch)
        {
            if (world.TryGet(entityId, out var entity))
            {
                Step(entity, serverTick);
            }
            else
            {
                // The entity vanished (despawn/disconnect) mid-action — drop the orphaned instance so it cannot leak.
                _active.Remove(entityId);
            }
        }
    }

    // CANCEL/INTERRUPT an entity's action (design §2.5): stop the instance, land an airborne entity to the ground at
    // its current XY (the same boundary the normal landing uses), and arm the cooldown. Phase A exposes this so a
    // future interrupt source (stun/death/server cancel) has the seam; nothing triggers it yet. No-op if not active.
    public void Cancel(WorldEntity entity, uint serverTick)
    {
        if (!_active.TryGetValue(entity.Id, out var inst))
        {
            return;
        }

        entity.SnapToGround(GroundHeight.GroundHeightAt(entity.Position));
        EndInstance(entity, inst, serverTick);
    }

    // Drop ALL action state for an entity LEAVING the world (despawn / disconnect / death): its active instance AND
    // any cooldown entries keyed on it. Call from the despawn seam so the cooldown map cannot grow unbounded over a
    // long-lived server, and a REUSED entity id can never inherit a stale cooldown from a prior occupant. Idempotent
    // — a no-op for an entity that had no action state.
    public void ClearEntity(ulong entityId)
    {
        _active.Remove(entityId);

        if (_cooldownUntil.Count == 0)
        {
            return;
        }

        _cooldownKeyScratch.Clear();
        foreach (var key in _cooldownUntil.Keys)
        {
            if (key.EntityId == entityId)
            {
                _cooldownKeyScratch.Add(key);
            }
        }

        foreach (var key in _cooldownKeyScratch)
        {
            _cooldownUntil.Remove(key);
        }
    }

    private void EndInstance(WorldEntity entity, ActionInstance inst, uint serverTick)
    {
        _active.Remove(entity.Id);
        if (inst.Def.CooldownTicks > 0)
        {
            _cooldownUntil[(entity.Id, inst.Def.Id)] = serverTick + inst.Def.CooldownTicks;
        }

        // ACTION-END STOP-EDGE (todo/S-dash-end-replication-bump): re-publish the entity's FINAL action position.
        // The instance is removed above, so IsActive no longer force-includes it, and a FLAT dash's last sub-tile
        // step has no other re-publish path: SnapToGround only bumps when VerticalOffset actually changed (a
        // JumpHeight=0 dash keeps Z at exactly 0 — no-op), ApplyResolvedMove only bumps on a rounded-tile crossing,
        // and a standstill-triggered dash has Velocity 0. Without this, a dash whose final tick doesn't cross a
        // tile leaves remote viewers holding the previous tick's position INDEFINITELY (delta'd out — up to ~0.67u
        // ghost offset). The jump landing already re-published via SnapToGround's Z-change bump; this makes the
        // action-end re-publish unconditional for every action shape (the same StateRevision stop-edge mechanism
        // as StopMovement / MarkRepositioned — one discrete re-send per action end, never a per-tick cost).
        entity.MarkRepositioned();
    }

    // Reused scratch list of active entity ids for StepAll (avoids a per-tick alloc while still iterating a stable
    // snapshot the dictionary mutation can't disturb).
    private readonly List<ulong> _idScratch = new();

    // Reused scratch for ClearEntity's cooldown-key removal (collect-then-remove so we don't mutate the dictionary
    // mid-enumeration). Only touched on a despawn, never on the hot tick path.
    private readonly List<(ulong EntityId, ActionId Action)> _cooldownKeyScratch = new();

    // The mutable per-entity action instance: the immutable def + the fixed context + the cached ballistic constants
    // + the advancing tick counter. A class (mutated in place by Step via TickInAction); the Context is a fixed
    // readonly struct captured at trigger (never re-read from live state — the determinism rule, design §2.3).
    private sealed class ActionInstance
    {
        public ActionInstance(
            MovementActionDef def, ActionContext context, double gravity, double launchVelocity, uint startTick)
        {
            Def = def;
            Context = context;
            Gravity = gravity;
            LaunchVelocity = launchVelocity;
            StartTick = startTick;
        }

        public MovementActionDef Def { get; }
        public ActionContext Context { get; }
        public double Gravity { get; }
        public double LaunchVelocity { get; }
        public uint StartTick { get; }

        // The action-local tick, 0 at trigger (takeoff), advanced to DurationTicks across the airborne span.
        public uint TickInAction { get; set; }
    }
}
