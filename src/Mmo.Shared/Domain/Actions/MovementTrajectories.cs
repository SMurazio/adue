namespace Mmo.Shared.Domain.Actions;

// MOVEMENT-ACTIONS (Phase A): the SHARED, DETERMINISTIC seed trajectory functions (design §1.2). Each is a pure
// (ctx, tickInAction) -> per-tick XY delta function — the crux of the determinism contract (design §2.3): client
// predict, client replay, and server execute call the IDENTICAL function, so "adding a new action is cheap" (a new
// def + a one-liner here). Phase A ships only the Jump XY trajectories; Charge/DodgeRoll trajectories are Phase D.
//
// THE Z IS NOT HERE. A jump's vertical comes from BallisticArc over the def's (JumpHeight, AirborneTicks) — the
// XY/Z split (design §1.4.1). These functions produce ONLY the ground-plane displacement; the executor adds the
// ballistic height separately. Keeping XY pure-deterministic + collision-resolved and Z pure-deterministic + free is
// the whole model.
public static class MovementTrajectories
{
    // FORWARD-ARC XY (design §1.4.3, the Jump default): advance forward along the LOCKED launch heading by an EQUAL
    // share of the def's ForwardDistanceUnits each tick. The per-tick delta is constant — Heading · (distance /
    // DurationTicks) — so the un-collided XY path is a straight line of the def's total length over the action; the
    // shared resolver may shorten it at a wall (lands short). DurationTicks is read from the registry def at the call
    // site (the executor closes over it), so this function needs only ctx + the precomputed per-tick step it carries.
    //
    // We don't have DurationTicks inside the pure function, so the per-tick distance is derived by the executor and
    // the function is parameterised at build time (see ForwardArc(perTickDistance)). This factory returns a closed
    // delegate so the registry can store one ready-to-call MovementTrajectory per def.
    public static MovementTrajectory ForwardArc(double perTickDistanceUnits)
    {
        return (in ActionContext ctx, uint tickInAction) =>
        {
            // Heading is a unit vector (or Zero). A constant forward step every tick — deterministic, no per-tick
            // state. tickInAction is unused for a constant-velocity forward arc (the Z, not the XY, varies per tick).
            _ = tickInAction;
            return ctx.Heading * perTickDistanceUnits;
        };
    }

    // IN-PLACE XY (design §1.4.3 alternative): no horizontal motion — the entity jumps straight up and comes straight
    // down. The trajectory yields a zero XY delta every tick (costs nothing extra; the Z still arcs via BallisticArc).
    public static readonly MovementTrajectory InPlace = (in ActionContext ctx, uint tickInAction) =>
    {
        _ = ctx;
        _ = tickInAction;
        return WorldVector.Zero;
    };
}
