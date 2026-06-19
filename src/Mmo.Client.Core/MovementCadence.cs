namespace Mmo.Client.Core;

public static class MovementCadence
{
    public static double EffectiveStepCadenceMs(int stepCooldownMs, int tickRate)
    {
        if (tickRate <= 0)
        {
            return Math.Max(1, stepCooldownMs);
        }

        var tickIntervalMs = 1000d / tickRate;
        var cooldownTicks = Math.Max(1, (int)Math.Ceiling(stepCooldownMs / tickIntervalMs));
        return cooldownTicks * tickIntervalMs;
    }

    // S63 turn delay quantised to whole ticks, mirroring ServerOptions.TurnDelayTicks (Ceiling, Max(1, …))
    // so the predictor's turn cost is the SAME number of ticks the server applies. A turn always costs at
    // least one tick — never instant — preserving the rotate-in-place-on-whip beat.
    public static double EffectiveTurnDelayMs(int turnDelayMs, int tickRate)
    {
        if (tickRate <= 0)
        {
            return Math.Max(1, turnDelayMs);
        }

        var tickIntervalMs = 1000d / tickRate;
        var turnTicks = Math.Max(1, (int)Math.Ceiling(turnDelayMs / tickIntervalMs));
        return turnTicks * tickIntervalMs;
    }
}
