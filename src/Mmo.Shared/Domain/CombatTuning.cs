namespace Mmo.Shared.Domain;

// SWING-COMMIT: shared combat-timing constants + the ms->ticks conversion, kept in ONE place so the server and
// the client predictor derive the SAME integer tick counts from the SAME ms feel-knobs. Parity is the whole
// point of this file: a melee swing briefly ROOTS the attacker's movement (a committed swing), implemented as a
// movement-cooldown bump that MUST be applied identically on the server (WorldEntity.ApplyAttackMovementRoot,
// driven from GameServer.HandleAttack) and mirrored in the client (LocalPlayerPredictor.ApplyAttackMovementRoot,
// driven from MmoClient.SendAttack). Both sides call RootTicks(tickRate) so the rootTicks value can never drift.
public static class CombatTuning
{
    // The movement-root duration of a melee swing, in ms. ~200 ms reads as a committed beat (the attacker can't
    // start a new step until it elapses) without feeling sticky. This is a FEEL KNOB — the single edit point for
    // tuning how long a swing roots movement. Distinct from (and shorter than) the ~600 ms attack cooldown, which
    // is an INDEPENDENT gate on attack cadence (WorldEntity._nextEligibleAttackTick) the root never touches.
    public const int MovementRootMs = 200;

    // Converts MovementRootMs to an INTEGER number of server ticks for a given tick rate, clamped >= 1 (a swing
    // always roots for at least one tick). Uses Ceiling — the SAME ms->ticks rounding the server uses for the
    // attack cooldown (GameServer.AttackCooldownTicks) — so the root window is never shorter than the configured
    // ms. The server and the client predictor BOTH call this with their respective tick rate (derived from the
    // same wire-advertised cadence/tick interval), guaranteeing they compute the identical rootTicks.
    public static uint RootTicks(int tickRate)
    {
        if (tickRate <= 0)
        {
            return 1u;
        }

        var tickIntervalMs = 1000d / tickRate;
        return (uint)System.Math.Max(1, (int)System.Math.Ceiling(MovementRootMs / tickIntervalMs));
    }

    // Predictor-side overload: derive rootTicks straight from the tick interval in ms (the predictor knows
    // _tickMs, not a tick rate). Mathematically identical to RootTicks(tickRate) because tickRate = 1000/tickMs,
    // so MovementRootMs/tickMs == MovementRootMs/(1000/tickRate). Same Ceiling, same >= 1 clamp — same value.
    public static uint RootTicksFromTickMs(double tickMs)
    {
        if (!(tickMs > 0))
        {
            return 1u;
        }

        return (uint)System.Math.Max(1, (int)System.Math.Ceiling(MovementRootMs / tickMs));
    }

    // COMBAT-TUNING (live): the same ms->ticks conversions, but driven by a LIVE rootMs (the replicated
    // combat.rootMs knob) instead of the MovementRootMs constant. The combat-tuning panel makes the swing root a
    // server-authoritative + replicated value; the server computes its rootTicks from the live ServerTuning.RootMs
    // (RootTicks(tickRate, rootMs)) and the client predictor from the replicated snapshot's RootMs
    // (RootTicksFromTickMs(tickMs, rootMs)). Both still Ceiling + clamp >= 1, so for rootMs == MovementRootMs they
    // return EXACTLY the old constant-based values (parity preserved). A negative/zero rootMs floors to 1 tick.
    public static uint RootTicks(int tickRate, int rootMs)
    {
        if (tickRate <= 0)
        {
            return 1u;
        }

        var tickIntervalMs = 1000d / tickRate;
        return (uint)System.Math.Max(1, (int)System.Math.Ceiling(rootMs / tickIntervalMs));
    }

    public static uint RootTicksFromTickMs(double tickMs, int rootMs)
    {
        if (!(tickMs > 0))
        {
            return 1u;
        }

        return (uint)System.Math.Max(1, (int)System.Math.Ceiling(rootMs / tickMs));
    }

    // SWING-SLOW: the default movement factor applied DURING the swing window. In [0,1]: 0 = full stop (the old
    // hard root), 1 = no slow, 0.4 (the default) = move at 40% speed. This is the fallback the client predictor
    // uses for the swing-slow BEFORE the first replicated CombatTuningSnapshot arrives; the server seeds the live
    // combat.swingMoveFactor from the SAME constant, so both default identically (parity before login). Steady-
    // state both sides read the replicated value. Distinct from MovementRootMs (the swing window DURATION, which
    // still governs how LONG the slow lasts) — this controls how HARD the slow is within that window.
    public const double DefaultSwingMoveFactor = 0.4d;

    // SWING-SLOW (the load-bearing parity formula — ONE place, both sides call it). Given the base per-step
    // cooldown in ticks and the swing move factor in [0,1], returns the EFFECTIVE per-step cooldown a step that
    // lands inside the swing window costs. Longer cooldown == slower movement:
    //
    //   * factor <= eps  ⇒ SwingBlockCooldownTicks (a sentinel "blocked" — the caller never accepts a step inside
    //                       the window, exactly reproducing the old full root). This is the 0 = full-stop case.
    //   * factor in (0,1] ⇒ ceil(baseCooldownTicks / factor), clamped >= baseCooldownTicks (a factor can only
    //                       SLOW, never speed up) and >= 1. factor == 1 ⇒ baseCooldownTicks unchanged (no slow).
    //
    // Ceiling + the >= base clamp are deterministic integer ops with no platform-dependent rounding, so the server
    // (off _tuning) and the predictor (off the replicated CombatTuning) compute the IDENTICAL integer for the same
    // (base, factor) — the whole point of centralising it here.
    public const uint SwingBlockCooldownTicks = uint.MaxValue;

    // The factor below which the swing is treated as a full block (the 0 = full-stop case). A tiny epsilon guards
    // against a divide-by-~0 producing an absurd (but finite) cooldown; at/under it we block instead.
    private const double SwingMoveFactorBlockEpsilon = 1e-6d;

    // SWING-SLOW: does this factor mean a FULL STOP (the old root)? True iff a step inside the window must be
    // BLOCKED rather than slowed. The single shared predicate both the server (WorldEntity.IsBlockedBySwingSlow)
    // and the predictor (LocalPlayerPredictor.IsBlockedBySwingSlow) key their block decision on, so they agree
    // exactly on which factors block. Uses the SAME epsilon as SlowedStepCooldownTicks.
    public static bool IsSwingBlockFactor(double factor)
        => !double.IsFinite(factor) || factor <= SwingMoveFactorBlockEpsilon;

    public static uint SlowedStepCooldownTicks(uint baseCooldownTicks, double factor)
    {
        // Non-finite or <= epsilon ⇒ a full stop: the caller blocks the step (old root semantics).
        if (IsSwingBlockFactor(factor))
        {
            return SwingBlockCooldownTicks;
        }

        // Clamp the factor into (0,1]: a factor > 1 would SPEED movement up during a swing, which is never the
        // intent — the swing can only slow. (The server registry also clamps to [0,1]; this is defence in depth so
        // the formula alone can't produce a sub-base cooldown.)
        var clamped = factor > 1d ? 1d : factor;
        var slowed = (long)System.Math.Ceiling(baseCooldownTicks / clamped);
        // Never below the base cooldown (a slow can't make you faster) and never below 1 tick.
        var floor = System.Math.Max(1L, (long)baseCooldownTicks);
        if (slowed < floor)
        {
            slowed = floor;
        }

        return slowed > uint.MaxValue ? uint.MaxValue : (uint)slowed;
    }
}
