namespace Mmo.Server.Runtime;

// DUO-WAVE2 (exp/duo-abilities): the tier a co-op timing coincidence earns. None = the two presses were too far apart
// to count as a coincidence; Good/Perfect scale the ability's payoff. Shared by ability 2 (both shield presses) and
// ability 4 (initiate then confirm).
public enum PairTier
{
    None = 0,
    Good = 1,
    Perfect = 2,
}

// DUO-WAVE2: the pure "two press ticks -> tier" classifier — the shared timing rule ability 2 (Unison Shield) and
// ability 4 (Midpoint Detonation) both consume. No state, no world: it is directly unit-testable at the exact
// window boundaries. Windows are in SERVER TICKS (20Hz), tunable per-ability by the caller; Perfect is the tighter
// (smaller) window and is checked first so a coincidence that qualifies for both earns the stronger tier. The tick
// delta is computed underflow-safe (either order) because tick order is not guaranteed by the caller.
public static class PairedTimingWindow
{
    // Classify the coincidence of two press ticks. Returns Perfect when |a-b| <= perfectWindowTicks, else Good when
    // |a-b| <= goodWindowTicks, else None. Inclusive on both bounds (a press exactly on the boundary counts) — the
    // fair-and-responsive pillar: the drawn/felt window IS the rule, so the edge is generous, not clipped. Assumes
    // perfectWindowTicks <= goodWindowTicks (the caller's tunables); if they are equal, the delta simply resolves to
    // Perfect first.
    public static PairTier Classify(uint tickA, uint tickB, uint perfectWindowTicks, uint goodWindowTicks)
    {
        var delta = tickA >= tickB ? tickA - tickB : tickB - tickA;
        if (delta <= perfectWindowTicks)
        {
            return PairTier.Perfect;
        }

        if (delta <= goodWindowTicks)
        {
            return PairTier.Good;
        }

        return PairTier.None;
    }
}
