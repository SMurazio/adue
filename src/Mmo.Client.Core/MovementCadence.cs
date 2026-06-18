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
}
