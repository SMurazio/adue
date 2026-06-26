namespace Mmo.Shared.Domain.Actions;

// MOVEMENT-ACTIONS (Phase A): the SHARED, DETERMINISTIC ballistic-Z formula (design §1.4.2). A jump's vertical is a
// textbook projectile under constant gravity, TICK-QUANTISED so it is byte-reproducible on client and server. This
// type owns the derivation (g/v0 from a height + hang-time) AND the per-tick height sample, both PURE — no state, no
// RNG, no clocks, all `double` (never float). Lives in Mmo.Shared.Domain alongside ContinuousCollision/TileWalls
// because the Phase-B client predictor must compute the IDENTICAL height from the IDENTICAL constants over the
// IDENTICAL integer ticks — the Z half of the determinism contract (design §2.3). Do NOT introduce a second copy.
//
// THE MODEL. Over N = AirborneTicks ticks at fixed timestep dt = 1/TickRate, the elapsed action-time at integer
// tick i is t = i·dt, and the height above the takeoff ground plane is
//
//     z(i) = GroundZ + v0·t − ½·g·t²            (t = i·dt)
//
// with v0 and g DERIVED (never client-supplied) from the def's apex height H = JumpHeight and the full hang-time
// T = N·dt so the arc peaks at the midpoint t = T/2 and returns to the plane at t = T:
//
//     g  = 8·H / T²            (gravity giving apex H at hang-time T)
//     v0 = g·(T/2) = 4·H / T   (launch velocity)
//
// (derivation: apex H = v0²/(2g) with full-flight time T = 2·v0/g). These are pure functions of (H, N, TickRate),
// so they are fixed in the def/registry and identical on both sides. The client cannot inflate height or hang-time;
// the wire (Phase B) carries only a heading. LANDING is handled by the EXECUTOR, not here: it is airborne for ticks
// 1..N and at tick N the executor SNAPS VerticalOffset to GroundHeightAt(landingXY) explicitly (no reliance on
// z(N) rounding back to exactly the ground value — design §1.4.2 "no float drift at the seam").
public static class BallisticArc
{
    // Derive the constant gravity `g` (world-units / second²) that gives apex `jumpHeight` over a full hang-time of
    // `airborneTicks` ticks at `tickRate` Hz: g = 8·H / T² with T = N·dt = N / tickRate. Returns 0 for a
    // degenerate input (non-positive height or ticks or rate) so a "no vertical" def is a flat (always-ground) arc.
    public static double Gravity(double jumpHeight, uint airborneTicks, int tickRate)
    {
        if (jumpHeight <= 0d || airborneTicks == 0 || tickRate <= 0)
        {
            return 0d;
        }

        var t = airborneTicks / (double)tickRate; // total hang-time T in seconds
        return 8d * jumpHeight / (t * t);
    }

    // Derive the launch velocity `v0` (world-units / second) for the same (jumpHeight, airborneTicks, tickRate):
    // v0 = 4·H / T with T = N / tickRate. Returns 0 for a degenerate input (a flat arc).
    public static double LaunchVelocity(double jumpHeight, uint airborneTicks, int tickRate)
    {
        if (jumpHeight <= 0d || airborneTicks == 0 || tickRate <= 0)
        {
            return 0d;
        }

        var t = airborneTicks / (double)tickRate;
        return 4d * jumpHeight / t;
    }

    // The height above the ground plane at integer action-tick `tickInAction`, for the given arc parameters. This is
    // z(i) − GroundZ (the OFFSET above ground; the caller adds GroundZ if it wants absolute world height). Derives g
    // and v0 internally so a caller need only pass the def's (jumpHeight, airborneTicks) + the tickRate — the single
    // source of the constants. PURE; identical on client and server for the same integer tick. Negative results are
    // floored to 0 (the arc never dips below the takeoff plane within ticks 0..N; this guards a caller that samples
    // past N).
    public static double HeightOffsetAtTick(double jumpHeight, uint airborneTicks, int tickRate, uint tickInAction)
    {
        var g = Gravity(jumpHeight, airborneTicks, tickRate);
        var v0 = LaunchVelocity(jumpHeight, airborneTicks, tickRate);
        return HeightOffsetAtTick(g, v0, tickRate, tickInAction);
    }

    // The height-above-ground at integer tick `tickInAction` from PRE-DERIVED constants (g, v0) — the hot form the
    // executor uses once it has cached g/v0 at trigger. z = v0·t − ½·g·t² with t = tickInAction / tickRate. PURE.
    // Floors negatives to 0 (see the derived overload).
    public static double HeightOffsetAtTick(double gravity, double launchVelocity, int tickRate, uint tickInAction)
    {
        if (tickRate <= 0)
        {
            return 0d;
        }

        var t = tickInAction / (double)tickRate;
        var z = (launchVelocity * t) - (0.5d * gravity * t * t);
        return z > 0d ? z : 0d;
    }
}
