namespace Mmo.Shared.Domain.Actions;

// MOVEMENT-ACTIONS (Phase A): the static, SHARED registry mapping ActionId -> MovementActionDef (design §1.3).
// Server and client load the SAME registry from the SAME shared assembly, so prediction and execution use identical
// defs — the "kill client/server constant duplication" discipline. Phase A shipped the seed Jump def (a player-class
// forward-arc ballistic jump); Phase D adds the player Charge (a fast grounded dash) and DodgeRoll (a short grounded
// dash with a server-authoritative i-frame window). The slime's low-hop Jump variant and the gnoll's monster charge
// stay PER-INSTANCE defs built by GameServer off the monster-type tuning (BeginMonsterHop/BeginMonsterCharge) — the
// registry holds the PLAYER-triggered defs HandleActionIntent resolves from the wire.
//
// Live tuning (AdminSetTuning under action.<id>.<field> + an ActionTuningMessage) is design §1.3 / Phase B — NOT
// wired in Phase A. Today the defs are compile-time constants; that is sufficient to build + headless-test the
// executor. Because a def's XY trajectory is closed over its (ForwardDistanceUnits / DurationTicks) per-tick step,
// the registry BUILDS each def through a factory so the trajectory and the stored params can never drift apart.
public sealed class MovementActionRegistry
{
    private readonly Dictionary<ActionId, MovementActionDef> _defs;

    private MovementActionRegistry(Dictionary<ActionId, MovementActionDef> defs)
    {
        _defs = defs;
    }

    // The default seed registry. PHASE A seeded the player-class Jump — a ballistic forward-arc jump with a modest
    // apex and reach over a short airborne span. PHASE D adds the player Charge + DodgeRoll (grounded dashes on the
    // SAME ForwardArc primitive). All the numbers are first-cut feel placeholders (tunable later via the Phase-B
    // live-tuning path); what matters is that each def is internally consistent (trajectory built from the same
    // ForwardDistanceUnits/DurationTicks it stores) and drives a real, deterministic path.
    public static MovementActionRegistry Default { get; } = Build();

    public bool TryGet(ActionId id, out MovementActionDef def) => _defs.TryGetValue(id, out def!);

    public MovementActionDef Get(ActionId id) =>
        _defs.TryGetValue(id, out var def)
            ? def
            : throw new KeyNotFoundException($"No MovementActionDef registered for {id}.");

    public IReadOnlyCollection<MovementActionDef> All => _defs.Values;

    private static MovementActionRegistry Build()
    {
        var defs = new Dictionary<ActionId, MovementActionDef>
        {
            [ActionId.Jump] = BuildForwardArcJump(
                id: ActionId.Jump,
                durationTicks: 12,        // ~12 ticks airborne (the action lasts exactly the airborne span)
                jumpHeight: 1.5d,         // apex 1.5 world units (the real replicated height)
                forwardDistanceUnits: 2.5d, // reach 2.5 tiles forward along the locked heading
                cooldownTicks: 18,        // its own re-trigger clock
                animationId: 1),

            // PHASE D — CHARGE (design §1.2): a fast forward dash along the LOCKED trigger heading (no steering,
            // locked decision #4). GROUNDED (no Z arc) and committed. SlideStop at a wall/body comes free from the
            // shared resolver — the per-tick forward delta pins deterministically at the contact on BOTH sides (the
            // P5 gnoll-charge precedent; the gnoll's own charge stays a per-instance def off its type tuning). The
            // span/speed mirror the gnoll's fast dash: 4 units over 6 ticks (300 ms @ 20 Hz ⇒ ~13.3 u/s, well above
            // the ~5 u/s walk). No i-frames — a charge is an aggressive gap-closer, not an evade.
            [ActionId.Charge] = BuildGroundDash(
                id: ActionId.Charge,
                durationTicks: 6,         // a short, FAST dash (300 ms @ 20 Hz)
                distanceUnits: 4.0d,      // 4 tiles forward along the locked heading
                cooldownTicks: 40,        // 2 s re-trigger clock (server-enforced; the client mirrors it)
                animationId: 2),          // placeholder; the charge animation is Phase E

            // PHASE D — DODGE-ROLL (design §1.2 / §2.7): a SHORT dash along the locked heading with a brief
            // SERVER-AUTHORITATIVE i-frame window mid-roll. The window [1, 4] covers the roll's active ticks except
            // the final recovery tick (land vulnerable) — decided ONLY at the server damage seam off this def; the
            // wire carries no i-frame claim a client could fake or extend. Deviation from the design table noted:
            // the per-tick distance is CONSTANT (ForwardArc), not an eased curve — the B2 client predictor models an
            // action as (average speed × duration), so an eased server curve would diverge from the prediction;
            // easing is presentation polish for Phase E.
            [ActionId.DodgeRoll] = BuildGroundDash(
                id: ActionId.DodgeRoll,
                durationTicks: 5,         // a snappy 250 ms roll @ 20 Hz
                distanceUnits: 2.5d,      // 2.5 tiles — shorter than the charge (an evade, not a closer)
                cooldownTicks: 20,        // 1 s re-trigger clock
                animationId: 3,           // placeholder; the roll animation is Phase E
                iFrameStartTick: 1,       // invulnerable from the first active tick…
                iFrameEndTick: 4),        // …through tick 4; the landing/recovery tick 5 is vulnerable
        };

        return new MovementActionRegistry(defs);
    }

    // Build a ForwardArc ballistic Jump def, deriving the per-tick forward step from (forwardDistanceUnits /
    // durationTicks) so the stored ForwardDistanceUnits and the trajectory the executor calls are guaranteed
    // consistent. AirborneTicks == DurationTicks for a pure jump (the action lasts exactly the airborne span).
    public static MovementActionDef BuildForwardArcJump(
        ActionId id,
        uint durationTicks,
        double jumpHeight,
        double forwardDistanceUnits,
        uint cooldownTicks,
        int animationId)
    {
        var perTick = durationTicks == 0 ? 0d : forwardDistanceUnits / durationTicks;
        return new MovementActionDef
        {
            Id = id,
            DurationTicks = durationTicks,
            Trajectory = MovementTrajectories.ForwardArc(perTick),
            CooldownTicks = cooldownTicks,
            Interruptible = false, // airborne is committed (design §1.2 / §2.5)
            CanSteer = false,      // locked heading (design decisions #3/#4)
            JumpHeight = jumpHeight,
            AirborneTicks = durationTicks,
            HorizontalMode = HorizontalMode.ForwardArc,
            ForwardDistanceUnits = forwardDistanceUnits,
            AnimationId = animationId,
        };
    }

    // MOVEMENT-ACTIONS (Phase D): build a GROUNDED forward DASH def (charge / dodge-roll) — the SAME ForwardArc
    // primitive the jump/hop use with a ZERO apex (BallisticArc yields a flat always-0 arc for H=0, so VerticalOffset
    // never leaves the ground; the executor's per-tick Z write and landing snap are no-ops at 0). SlideStop at a
    // wall/body needs NO executor feature: the constant per-tick forward delta resolves through the shared
    // swept-circle resolver, so a dash into a wall pins at the face — deterministically, on both sides — for the
    // remaining ticks (motion early-stops; the instance still runs out its short DurationTicks, exactly like the P5
    // gnoll charge). The optional i-frame window is DATA the server damage seam reads (design §2.7); the charge
    // leaves it empty, the dodge-roll authors it.
    public static MovementActionDef BuildGroundDash(
        ActionId id,
        uint durationTicks,
        double distanceUnits,
        uint cooldownTicks,
        int animationId,
        uint iFrameStartTick = 0,
        uint iFrameEndTick = 0)
    {
        var perTick = durationTicks == 0 ? 0d : distanceUnits / durationTicks;
        return new MovementActionDef
        {
            Id = id,
            DurationTicks = durationTicks,
            Trajectory = MovementTrajectories.ForwardArc(perTick),
            CooldownTicks = cooldownTicks,
            Interruptible = false, // committed (design §1.2 — a charge/roll runs to completion; no interrupt source yet)
            CanSteer = false,      // locked heading (design decision #4)
            JumpHeight = 0d,       // GROUNDED — a flat dash, no Z arc (BallisticArc gives 0 height for H=0)
            AirborneTicks = durationTicks,
            HorizontalMode = HorizontalMode.ForwardArc,
            ForwardDistanceUnits = distanceUnits,
            AnimationId = animationId,
            IFrameStartTick = iFrameStartTick,
            IFrameEndTick = iFrameEndTick,
        };
    }

    // Build an InPlace ballistic Jump def (XY stays at Origin, Z arcs). Used by the InPlace headless test and
    // available per-def for a "hop in place" telegraph (design §1.4.3).
    public static MovementActionDef BuildInPlaceJump(
        ActionId id,
        uint durationTicks,
        double jumpHeight,
        uint cooldownTicks,
        int animationId)
    {
        return new MovementActionDef
        {
            Id = id,
            DurationTicks = durationTicks,
            Trajectory = MovementTrajectories.InPlace,
            CooldownTicks = cooldownTicks,
            Interruptible = false,
            CanSteer = false,
            JumpHeight = jumpHeight,
            AirborneTicks = durationTicks,
            HorizontalMode = HorizontalMode.InPlace,
            ForwardDistanceUnits = 0d,
            AnimationId = animationId,
        };
    }
}
