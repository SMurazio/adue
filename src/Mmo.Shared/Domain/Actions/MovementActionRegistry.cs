namespace Mmo.Shared.Domain.Actions;

// MOVEMENT-ACTIONS (Phase A): the static, SHARED registry mapping ActionId -> MovementActionDef (design §1.3).
// Server and client load the SAME registry from the SAME shared assembly, so prediction and execution use identical
// defs — the "kill client/server constant duplication" discipline. Phase A ships the seed Jump def (a player-class
// forward-arc ballistic jump); the slime's low-hop Jump variant and Charge/DodgeRoll come in later phases.
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

    // The default seed registry. PHASE A: a single player-class Jump — a ballistic forward-arc jump with a modest
    // apex and reach over a short airborne span. The numbers are first-cut feel placeholders (tunable later via the
    // Phase-B live-tuning path); what matters for Phase A is that the def is internally consistent (trajectory built
    // from the same ForwardDistanceUnits/DurationTicks it stores) and drives a real, deterministic arc.
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
