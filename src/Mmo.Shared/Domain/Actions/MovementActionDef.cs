namespace Mmo.Shared.Domain.Actions;

// MOVEMENT-ACTIONS (Phase A): the declarative definition of a movement action (design §1.1). It is DATA + a shared
// per-tick XY trajectory function; triggering one starts a short-lived action instance (ServerActionExecutor) that
// drives the entity's movement for DurationTicks. Lives in Mmo.Shared so the Phase-B client predictor loads the
// SAME defs (the registry is shared) and runs the SAME trajectory — the determinism contract (design §2.3).
//
// PHASE A SCOPE. Only the fields the headless ballistic-jump executor needs are wired: Id, DurationTicks, the XY
// Trajectory, Cooldown, the gameplay Properties used now (Interruptible / CanSteer), the Vertical (ballistic-Z)
// params, and AnimationId (carried, presentation-only, used by Phase E). PHASE D adds the IFrame window (dodge-roll)
// as pure DATA below — it slotted in additively exactly as planned. CollisionMode / Hitbox from the full design table
// remain deferred: SlideStop-at-a-wall comes free from the shared resolver (the per-tick delta pins at the wall face,
// the P5 gnoll-charge precedent), and the contact hitbox is a later combat hook. The executor is action-AGNOSTIC: it
// reads these fields, it has no per-action branches.
public sealed record MovementActionDef
{
    public required ActionId Id { get; init; }

    // The trajectory length in SERVER TICKS (fixed-point time, latency-independent). For a pure jump this equals the
    // airborne span: the action lasts exactly as long as the entity is off the ground (design §1.1 note).
    public required uint DurationTicks { get; init; }

    // The SHARED deterministic XY trajectory: (ctx, tickInAction) -> the desired GROUND-PLANE displacement for THIS
    // one tick, PRE-collision. The executor resolves it through the shared swept-circle resolver, so an airborne or
    // dashing entity can still be wall-blocked in XY. The vertical is produced SEPARATELY from the Vertical params
    // via BallisticArc (the XY/Z split, design §1.4.1) — the trajectory never touches Z. Must be pure + deterministic.
    public required MovementTrajectory Trajectory { get; init; }

    // The re-trigger gate in TICKS — its OWN clock, NOT the move/attack cooldown (design §1.1). Armed when the action
    // starts; a trigger arriving before it elapses is rejected (the cooldown re-trigger test, design §5).
    public uint CooldownTicks { get; init; }

    // Can a damage/stun/server-cancel EVENT interrupt this action mid-flight? (design §2.5). False for the seed Jump
    // (airborne is committed). Phase A only stores it (the executor exposes Cancel for a future interrupt source);
    // the interrupt wiring is Phase B/later.
    public bool Interruptible { get; init; }

    // May the entity change heading mid-action? false = LOCKED heading (jump/charge — locked design decisions #3/#4).
    // No seed action steers; the field stays for future curved actions. Phase A holds the launch heading regardless.
    public bool CanSteer { get; init; }

    // BALLISTIC-Z params (design §1.4) — present (non-zero) only for jump-class actions. JumpHeight is the apex in
    // world units (0 ⇒ no vertical, a flat arc); AirborneTicks is the hang-time in ticks from which g/v0 are DERIVED
    // (BallisticArc). HorizontalMode selects ForwardArc (XY tracks heading) vs InPlace (straight up).
    public double JumpHeight { get; init; }
    public uint AirborneTicks { get; init; }
    public HorizontalMode HorizontalMode { get; init; }

    // For ForwardArc actions: the total GROUND-PLANE distance (world units) the arc travels along the locked heading
    // over DurationTicks. Per the design's recommended open-question answer, jump reach is an EXPLICIT per-def
    // distance (tuned independently of walk speed) rather than derived from Speed×AirborneTicks. 0 ⇒ no forward
    // travel (e.g. an InPlace jump). The per-tick XY delta is ForwardDistanceUnits / DurationTicks.
    public double ForwardDistanceUnits { get; init; }

    // The client visual id (presentation-only). For a jump the animation is DRIVEN BY the real replicated Z, not a
    // separate cosmetic arc (design §1.1). Phase A only carries it; Phase E wires the animations.
    public int AnimationId { get; init; }

    // MOVEMENT-ACTIONS (Phase D): the SERVER-AUTHORITATIVE invulnerability window (design §1.1 IFrameTicks / §2.7),
    // in ACTION-LOCAL ticks, INCLUSIVE at both ends, anchored at the trigger tick (elapsed = serverTick − startTick;
    // tick 0 is the trigger/takeoff frame, ticks 1..DurationTicks are the active span). Empty — both 0, the default —
    // means NO i-frames (jump/charge). Only the dodge-roll authors it. Pure DATA: the executor exposes the window via
    // HasActiveIFrames for the DAMAGE seam; the wire carries only (actionId, heading), so a client can neither claim
    // nor extend a window (anti-cheat, design §2.7) — the client only RENDERS the roll, the server DECIDES the damage.
    public uint IFrameStartTick { get; init; }
    public uint IFrameEndTick { get; init; }

    // True iff this def authors a non-empty i-frame window (an inclusive [start, end] with end >= start and end > 0).
    public bool HasIFrameWindow => IFrameEndTick > 0 && IFrameEndTick >= IFrameStartTick;
}

// MOVEMENT-ACTIONS (Phase A): the per-tick XY trajectory delegate (design §1.1). PURE + DETERMINISTIC + SHARED —
// returns the desired ground-plane displacement for THIS one tick, pre-collision. The only inputs are the fixed
// ActionContext and the integer tick, so client predict, client replay, and server execute all produce the same
// per-tick deltas. The vertical is NOT here — it comes from BallisticArc over the def's Vertical params.
public delegate WorldVector MovementTrajectory(in ActionContext ctx, uint tickInAction);

// MOVEMENT-ACTIONS (Phase A): horizontal behaviour during a jump (design §1.4.3). ForwardArc (default) advances XY
// forward along the locked heading while Z arcs (resolved by the shared collision — can be wall-blocked → lands
// short). InPlace holds XY at Origin (straight up / down). Per-def, so both are first-class without an executor change.
public enum HorizontalMode : byte
{
    ForwardArc = 0,
    InPlace = 1,
}
