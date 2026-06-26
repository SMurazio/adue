namespace Mmo.Shared.Domain.Actions;

// MOVEMENT-ACTIONS (Phase A): the per-action-instance context captured ONCE at trigger and NEVER re-read from live
// mutable state (design §1.1 / §2.3 "context fixed at trigger"). Holding the trajectory's inputs immutable is half
// the determinism contract: a divergence in live state mid-action cannot desync the path, because the trajectory
// only ever sees this fixed struct + the integer tick. Everything here is GROUND-PLANE (the existing 2D WorldVector)
// except GroundZ, the scalar takeoff height (the elevation hook, always 0 today) — the XY/Z split (design §1.4.1).
//
// A readonly record struct: copied by value at trigger, no aliasing back to the entity's mutable fields.
public readonly record struct ActionContext(
    // The ground-plane (XY) position at trigger — action tick 0 / the arc origin.
    WorldVector Origin,
    // The unit heading at trigger (the locked launch heading for Jump/Charge). Zero ⇒ no horizontal motion.
    WorldVector Heading,
    // The entity's speed (world-units / sec) at trigger. Snapshotted, so a live speed retune mid-action is
    // intentionally ignored for this action's duration (design §4 "speed-multiplier / retune").
    double Speed,
    // FIXED 1/TickRate. Actions are tick-quantised, NOT per-frame dt-driven — the single biggest divergence from
    // ordinary movement and the reason an action is byte-reproducible regardless of frame rate (design §1.1).
    double DtPerTick,
    // The ground height at Origin (the takeoff-side elevation hook, GroundHeight.GroundHeightAt(Origin)). ALWAYS 0
    // today; the landing side re-reads GroundHeightAt(landingXY) at the end of the arc.
    double GroundZ)
{
    // The server TickRate this context was captured at — needed to derive the ballistic constants (g/v0) over
    // integer ticks. Stored alongside DtPerTick (= 1/TickRate) so a trajectory has both forms without re-deriving.
    public int TickRate { get; init; }
}
