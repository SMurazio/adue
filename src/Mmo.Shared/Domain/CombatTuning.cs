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
}
